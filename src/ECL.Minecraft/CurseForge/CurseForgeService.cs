// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Localization;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.Instances;
using Ecl.Operations;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.CurseForge;

public interface ICurseForgeService
{
    CurseForgeStatus Status { get; }

    Task<Result<CurseForgeSearchResult>> SearchAsync(
        CurseForgeSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<Result<CurseForgeInstallReport>> InstallAsync(
        string instanceId,
        string modReference,
        int? fileId = null,
        bool includeDependencies = true,
        IProgress<CurseForgeProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// CurseForge 安装入口。F-06 S4 起不再自带依赖解析与下载循环：只负责把用户给的引用解析成稳定项目身份，
/// 然后交给统一 Planner 生成计划、由唯一执行器落盘，最后按计划节点写安装记录（更新检查的依据）。
/// 依赖版本选择只发生在计划阶段，安装阶段不二次猜测。
/// </summary>
public sealed class CurseForgeService(
    IInstanceService instances,
    ICurseForgeClient client,
    ILocalizer localizer,
    ILogger<CurseForgeService> logger,
    IContentInstallPlanner planner,
    IContentPlanExecutor planExecutor,
    ICurseForgeInstallRecordStore records,
    IOperationService? operations = null) : ICurseForgeService
{
    public CurseForgeStatus Status => client.Status;

    public Task<Result<CurseForgeSearchResult>> SearchAsync(
        CurseForgeSearchQuery query,
        CancellationToken cancellationToken = default) =>
        client.SearchModsAsync(query, cancellationToken);

    public async Task<Result<CurseForgeInstallReport>> InstallAsync(
        string instanceId,
        string modReference,
        int? fileId = null,
        bool includeDependencies = true,
        IProgress<CurseForgeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modReference))
        {
            return Result<CurseForgeInstallReport>.Fail("curseforge.invalid_mod", "请提供 CurseForge 项目 ID、slug 或链接。");
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<CurseForgeInstallReport>.Fail("instance.not_found", "实例不存在。");
        }

        using var operation = new OperationProgressBridge<CurseForgeProgress>(
            operations,
            new OperationStart
            {
                Kind = OperationKind.Install,
                Title = modReference.Trim(),
                StageId = "curseforge",
                Source = "CurseForge",
                CanCancel = true,
                CanRetry = true,
            },
            progress,
            MapCurseForgeProgress);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = cancellation.Token;
        operation.AttachControl(
            cancellation,
            retryToken => InstallAsync(instanceId, modReference, fileId, includeDependencies, progress, retryToken));

        try
        {
            var result = await InstallCoreAsync(
                    instance,
                    modReference,
                    fileId,
                    includeDependencies,
                    operation,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                operation.Complete();
            }
            else
            {
                operation.Fail(OperationErrors.FromCode(result.Error!.Code, result.Error.Message, result.Error.Detail));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            operation.Fail(OperationErrors.FromException("curseforge.install_failed", exception));
            throw;
        }
    }

    private async Task<Result<CurseForgeInstallReport>> InstallCoreAsync(
        GameInstance instance,
        string modReference,
        int? fileId,
        bool includeDependencies,
        IProgress<CurseForgeProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new CurseForgeProgress("resolve", localizer.Get("curseforge.stage.resolve")));
        if (CurseForgeLoaders.FromInstanceLoader(instance.Loader) is null)
        {
            return Result<CurseForgeInstallReport>.Fail(
                "curseforge.loader_unsupported",
                "原版实例不能安装 CurseForge Mod，请先创建带加载器的实例。");
        }

        var modResult = await ResolveModAsync(modReference, instance, cancellationToken).ConfigureAwait(false);
        if (!modResult.IsSuccess)
        {
            return Result<CurseForgeInstallReport>.Fail(modResult.Error!);
        }

        var mod = modResult.Value!;

        var planned = await planner.PlanAsync(
                instance.Id,
                new ContentInstallPlanRequest(
                    ContentSourceKind.CurseForge,
                    mod.Id.ToString(),
                    fileId?.ToString(),
                    IncludeOptionalDependencies: false,
                    IncludeRequiredDependencies: includeDependencies,
                    Kind: ContentKind.Mod),
                cancellationToken)
            .ConfigureAwait(false);
        if (!planned.IsSuccess)
        {
            return Result<CurseForgeInstallReport>.Fail(planned.Error!);
        }

        var plan = planned.Value!;
        if (!plan.IsInstallable)
        {
            return Result<CurseForgeInstallReport>.Fail(
                "curseforge.plan_blocked",
                $"{mod.Name} 的安装计划存在阻断项，未安装任何文件。",
                string.Join("; ", plan.Blockers));
        }

        var forwarding = new ForwardingProgress<ContentProgress>(update =>
            progress?.Report(new CurseForgeProgress(
                update.Stage,
                update.Message,
                update.CompletedFiles,
                update.TotalFiles)
            {
                CompletedBytes = update.CompletedBytes,
                TotalBytes = update.TotalBytes,
                BytesPerSecond = update.BytesPerSecond,
                CurrentFile = update.CurrentFile,
            }));

        var executed = await planExecutor
            .ExecuteAsync(plan, forwarding, cancellationToken)
            .ConfigureAwait(false);
        if (!executed.IsSuccess)
        {
            // 保留 CurseForge 入口的稳定错误码；下载失败仍以 curseforge.download_failed 报告。
            return executed.Error!.Code == "download.failed"
                ? Result<CurseForgeInstallReport>.Fail(
                    "curseforge.download_failed",
                    executed.Error.Message,
                    executed.Error.Detail)
                : Result<CurseForgeInstallReport>.Fail(executed.Error!);
        }

        var warnings = new List<string>(executed.Value!.Warnings);
        var records = plan.Files
            .Where(node => node.Source == ContentSourceKind.CurseForge &&
                           int.TryParse(node.ProjectId, out _) &&
                           int.TryParse(node.VersionId, out _) &&
                           !string.IsNullOrWhiteSpace(node.FileName))
            .ToArray();
        WriteInstalledRecords(instance, records, warnings);

        var root = plan.Root;
        var rootFileId = int.TryParse(root?.VersionId, out var parsedFileId) ? parsedFileId : 0;
        var installedFiles = executed.Value.InstalledFiles;
        progress?.Report(new CurseForgeProgress(
            "done",
            localizer.Get("curseforge.stage.done"),
            installedFiles.Count,
            installedFiles.Count));
        logger.LogInformation(
            "已从 CurseForge 安装 {Mod}（项目 {ProjectId}，文件 {FileId}，计划 {PlanId}）到实例 {InstanceId}，共 {Count} 个文件。",
            mod.Name,
            mod.Id,
            rootFileId,
            plan.PlanId,
            instance.Id,
            installedFiles.Count);

        return Result<CurseForgeInstallReport>.Ok(new CurseForgeInstallReport(
            mod.Name,
            mod.Id,
            rootFileId,
            root?.FileName ?? installedFiles.FirstOrDefault() ?? string.Empty,
            installedFiles,
            warnings));
    }

    /// <summary>
    /// 按计划节点写安装记录：只有来自 CurseForge、带稳定项目/文件 ID 的节点才写入，
    /// 失败只记警告，不把“记录未写”伪装成安装失败（文件已经提交）。
    /// </summary>
    private void WriteInstalledRecords(
        GameInstance instance,
        IReadOnlyList<ContentDependencyNode> installedNodes,
        List<string> warnings)
    {
        if (installedNodes.Count == 0)
        {
            return;
        }

        var updated = new List<CurseForgeInstallRecord>();
        foreach (var node in installedNodes)
        {
            if (!int.TryParse(node.ProjectId, out var projectId) ||
                !int.TryParse(node.VersionId, out var fileId) ||
                string.IsNullOrWhiteSpace(node.FileName))
            {
                continue;
            }

            updated.Add(new CurseForgeInstallRecord(projectId, fileId, node.FileName!));
        }

        var applied = records.Apply(instance, updated);
        if (!applied.IsSuccess)
        {
            warnings.Add(applied.Error!.Message);
            logger.LogWarning(
                "写入 CurseForge 安装记录失败：{Code} {Message}",
                applied.Error.Code,
                applied.Error.Message);
        }
    }

    private async Task<Result<CurseForgeMod>> ResolveModAsync(
        string reference,
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        var trimmed = reference.Trim();
        if (int.TryParse(trimmed, out var modId) && modId > 0)
        {
            return await client.GetModAsync(modId, cancellationToken).ConfigureAwait(false);
        }

        var slug = ExtractSlug(trimmed);
        var bySlug = await client.FindModBySlugAsync(slug, cancellationToken).ConfigureAwait(false);
        if (!bySlug.IsSuccess)
        {
            return Result<CurseForgeMod>.Fail(bySlug.Error!);
        }

        if (bySlug.Value is not null)
        {
            return Result<CurseForgeMod>.Ok(bySlug.Value);
        }

        var search = await client.SearchModsAsync(
                new CurseForgeSearchQuery
                {
                    SearchFilter = trimmed,
                    GameVersion = instance.GameVersion,
                    Loader = CurseForgeLoaders.FromInstanceLoader(instance.Loader),
                    PageSize = 10,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!search.IsSuccess)
        {
            return Result<CurseForgeMod>.Fail(search.Error!);
        }

        var mod = search.Value!.Mods.FirstOrDefault();
        return mod is null
            ? Result<CurseForgeMod>.Fail(
                "curseforge.mod_not_found",
                $"没有找到 CurseForge 项目：{reference}",
                $"游戏版本 {instance.GameVersion}，加载器 {instance.Loader}")
            : Result<CurseForgeMod>.Ok(mod);
    }

    private static string ExtractSlug(string reference)
    {
        if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri) ||
            !(string.Equals(uri.Host, "curseforge.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".curseforge.com", StringComparison.OrdinalIgnoreCase)))
        {
            return reference;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var projectIndex = Array.LastIndexOf(segments, "mc-mods");
        if (projectIndex >= 0 && projectIndex + 1 < segments.Length)
        {
            return segments[projectIndex + 1];
        }

        return segments.Length == 0 ? reference : segments[^1];
    }

    /// <summary>
    /// CurseForge 安装进度映射到任务中心：解析、下载与完成阶段使用稳定 StageId，
    /// 下载阶段提供字节、速度与当前文件。
    /// </summary>
    private static OperationProgressUpdate MapCurseForgeProgress(CurseForgeProgress progress) => new()
    {
        StageId = $"curseforge.{progress.Stage}",
        StageText = progress.Message,
        CurrentItem = progress.CurrentFile,
        Completed = progress.Completed,
        Total = progress.Total > 0 ? progress.Total : null,
        BytesDownloaded = progress.CompletedBytes,
        TotalBytes = progress.TotalBytes > 0 ? progress.TotalBytes : null,
        BytesPerSecond = progress.BytesPerSecond > 0 ? progress.BytesPerSecond : null,
    };

    private sealed class ForwardingProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
