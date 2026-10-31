// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Modrinth;
using Ecl.Operations;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Mods;

/// <summary>缺失依赖下载的进度。阶段：scan / resolve / install / done。</summary>
public sealed record ModDependencyProgress(
    string Stage,
    string Message,
    int Completed,
    int Total,
    string? CurrentFile);

public sealed record ModDependencyInstallReport(
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 扫描实例中“已启用但缺少强制依赖”的 Mod，按 mod id 在 Modrinth / CurseForge
/// 精确匹配项目并调用统一内容安装流程。
/// </summary>
public interface IModDependencyInstaller
{
    Task<Result<ModDependencyInstallReport>> InstallMissingAsync(
        string instanceId,
        IProgress<ModDependencyProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ModDependencyInstaller(
    IInstanceService instances,
    IModService mods,
    IModrinthClient modrinth,
    ICurseForgeClient curseForge,
    IContentInstallService contentInstall,
    ILocalizer localizer,
    ILogger<ModDependencyInstaller> logger,
    IOperationService? operations = null) : IModDependencyInstaller
{
    public async Task<Result<ModDependencyInstallReport>> InstallMissingAsync(
        string instanceId,
        IProgress<ModDependencyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ModDependencyInstallReport>.Fail("instance.not_found", "实例不存在。");
        }

        using var operation = new OperationProgressBridge<ModDependencyProgress>(
            operations,
            new OperationStart
            {
                Kind = OperationKind.Install,
                Title = localizer.Get("mods.dependencies.taskTitle", instance.Name),
                StageId = "mods.dependencies",
                Source = "Modrinth / CurseForge",
                CanCancel = true,
                CanRetry = true,
            },
            progress,
            MapProgress);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = cancellation.Token;
        operation.AttachControl(
            cancellation,
            retryToken => InstallMissingAsync(instanceId, progress, retryToken));

        try
        {
            var result = await InstallCoreAsync(instance, operation, cancellationToken).ConfigureAwait(false);
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            operation.Fail(OperationErrors.FromException("mods.dependencies.failed", exception));
            throw;
        }
    }

    private async Task<Result<ModDependencyInstallReport>> InstallCoreAsync(
        GameInstance instance,
        IProgress<ModDependencyProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ModDependencyProgress(
            "scan",
            localizer.Get("mods.dependencies.scanning"),
            0,
            0,
            null));

        var scan = await mods.ScanAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        if (!scan.IsSuccess)
        {
            return Result<ModDependencyInstallReport>.Fail(scan.Error!);
        }

        var missing = scan.Value!.Issues
            .Where(issue => issue.Kind == ModIssueKind.MissingDependency)
            .Select(issue => issue.RelatedModId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missing.Length == 0)
        {
            progress?.Report(new ModDependencyProgress(
                "done",
                localizer.Get("mods.dependencies.none"),
                0,
                0,
                null));
            return Result<ModDependencyInstallReport>.Ok(new ModDependencyInstallReport([], [], [], []));
        }

        var installed = new List<string>();
        var unresolved = new List<string>();
        var failed = new List<string>();
        var warnings = new List<string>();

        for (var index = 0; index < missing.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dependencyId = missing[index];

            progress?.Report(new ModDependencyProgress(
                "resolve",
                localizer.Get("mods.dependencies.resolving", dependencyId),
                index,
                missing.Length,
                dependencyId));

            var hit = await ResolveAsync(dependencyId, instance, cancellationToken).ConfigureAwait(false);
            if (hit is null)
            {
                unresolved.Add(dependencyId);
                warnings.Add(localizer.Get("mods.dependencies.unresolved", dependencyId));
                continue;
            }

            progress?.Report(new ModDependencyProgress(
                "install",
                localizer.Get("mods.dependencies.installing", hit.Name),
                index,
                missing.Length,
                hit.Name));

            var install = await contentInstall
                .InstallAsync(instance.Id, hit, progress: null, cancellationToken)
                .ConfigureAwait(false);
            if (install.IsSuccess)
            {
                installed.Add(dependencyId);
            }
            else
            {
                failed.Add(dependencyId);
                warnings.Add($"{dependencyId}: {install.Error?.Message}");
                logger.LogWarning(
                    "下载依赖 {Dependency} 到实例 {InstanceId} 失败：{Error}",
                    dependencyId,
                    instance.Id,
                    install.Error);
            }
        }

        progress?.Report(new ModDependencyProgress(
            "done",
            localizer.Get("mods.dependencies.done", installed.Count, unresolved.Count, failed.Count),
            missing.Length,
            missing.Length,
            null));

        return Result<ModDependencyInstallReport>.Ok(
            new ModDependencyInstallReport(installed, unresolved, failed, warnings));
    }

    /// <summary>
    /// 依赖声明的是 mod id（例如 <c>fabric-api</c>），不是项目 ID。
    /// 先按 slug/项目 ID 精确查询 Modrinth，再回退 CurseForge；不做模糊匹配，避免装错 Mod。
    /// </summary>
    private async Task<ContentHit?> ResolveAsync(
        string dependencyId,
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        var modrinthHit = await ResolveModrinthAsync(dependencyId, instance, cancellationToken).ConfigureAwait(false);
        if (modrinthHit is not null)
        {
            return modrinthHit;
        }

        return await ResolveCurseForgeAsync(dependencyId, instance, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContentHit?> ResolveModrinthAsync(
        string dependencyId,
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        var project = await modrinth.GetProjectAsync(dependencyId, cancellationToken).ConfigureAwait(false);
        if (!project.IsSuccess ||
            project.Value is not { } value ||
            value.MappedProjectType != ModrinthProjectType.Mod)
        {
            return null;
        }

        var versions = await modrinth
            .GetVersionsAsync(
                value.Id,
                instance.GameVersion,
                ModrinthApi.LoaderForModrinth(instance.Loader),
                cancellationToken)
            .ConfigureAwait(false);
        if (!versions.IsSuccess || versions.Value!.Count == 0)
        {
            return null;
        }

        return new ContentHit(
            ContentSource.Modrinth,
            value.Id,
            value.Slug,
            string.IsNullOrWhiteSpace(value.Title) ? dependencyId : value.Title,
            value.Description ?? string.Empty,
            null,
            value.Downloads,
            null,
            value.SourceUrl,
            value.IconUrl,
            ContentKind.Mod);
    }

    private async Task<ContentHit?> ResolveCurseForgeAsync(
        string dependencyId,
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        var bySlug = await curseForge.FindModBySlugAsync(dependencyId, cancellationToken).ConfigureAwait(false);
        var mod = bySlug.IsSuccess ? bySlug.Value : null;

        if (mod is null)
        {
            var query = new CurseForgeSearchQuery
            {
                Slug = dependencyId,
                GameVersion = instance.GameVersion,
                Loader = CurseForgeLoaders.FromInstanceLoader(instance.Loader),
                PageSize = 10,
            };
            var search = await curseForge.SearchModsAsync(query, cancellationToken).ConfigureAwait(false);
            mod = search.IsSuccess
                ? search.Value!.Mods.FirstOrDefault(candidate => Matches(candidate, dependencyId))
                : null;
        }

        if (mod is null)
        {
            return null;
        }

        return new ContentHit(
            ContentSource.CurseForge,
            mod.Id.ToString(CultureInfo.InvariantCulture),
            mod.Slug,
            string.IsNullOrWhiteSpace(mod.Name) ? dependencyId : mod.Name,
            mod.Summary ?? string.Empty,
            null,
            mod.DownloadCount,
            mod.DateModified,
            mod.WebsiteUrl,
            mod.IconUrl,
            ContentKind.Mod);
    }

    private static bool Matches(CurseForgeMod mod, string dependencyId) =>
        string.Equals(mod.Slug, dependencyId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mod.Name, dependencyId, StringComparison.OrdinalIgnoreCase);

    private static OperationProgressUpdate MapProgress(ModDependencyProgress progress) => new()
    {
        StageId = $"mods.dependencies.{progress.Stage}",
        StageText = progress.Message,
        CurrentItem = progress.CurrentFile,
        Completed = progress.Completed,
        Total = progress.Total > 0 ? progress.Total : null,
    };
}
