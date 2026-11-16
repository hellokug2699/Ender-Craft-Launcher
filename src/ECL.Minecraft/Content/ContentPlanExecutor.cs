// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Download;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Content;

/// <summary>计划执行结果：真实写入的文件与计划级警告，不包含“推测成功”。</summary>
public sealed record ContentPlanExecutionResult(
    string PlanId,
    string InstanceId,
    ContentKind Kind,
    IReadOnlyList<string> InstalledFiles,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 计划执行入口（F-06 S4）：唯一把 <see cref="ContentInstallPlan.Files"/> 落进实例的路径。
/// 只消费计划中已经解析好的文件（稳定版本 ID、下载地址、哈希、大小），不再按项目 ID 选版本；
/// 下载走启动器 staging，写入走 F-04 实例事务。
/// </summary>
public interface IContentPlanExecutor
{
    Task<Result<ContentPlanExecutionResult>> ExecuteAsync(
        ContentInstallPlan plan,
        IProgress<ContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// F-06 S4 安装执行器。所有安装入口（内容安装、CurseForge 安装、依赖安装、CLI）都通过它执行，
/// 从而保证“安装阶段不二次猜测依赖版本”这一约束在代码层只有一个落点。
///
/// 执行前置校验：
/// - 计划必须属于同一实例，且实例当前 GameVersion/Loader 与计划一致；否则 <c>content.plan.stale</c>。
/// - 计划过期、存在阻断项或没有可安装文件时拒绝执行。
/// - 下载地址按来源重新校验（Modrinth 只接受 https://*.modrinth.com；CurseForge 只接受官方 CDN）。
/// </summary>
public sealed class ContentPlanExecutor : IContentPlanExecutor
{
    private readonly IInstanceService _instances;
    private readonly IDownloadService _downloads;
    private readonly IAppPaths _paths;
    private readonly IInstanceTransactionService _transactions;
    private readonly ILocalizer _localizer;
    private readonly ILogger<ContentPlanExecutor> _logger;
    private readonly TimeProvider _time;

    public ContentPlanExecutor(
        IInstanceService instances,
        IDownloadService downloads,
        IAppPaths paths,
        IInstanceTransactionService transactions,
        ILocalizer localizer,
        ILogger<ContentPlanExecutor>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _downloads = downloads;
        _paths = paths;
        _transactions = transactions;
        _localizer = localizer;
        _logger = logger ?? NullLogger<ContentPlanExecutor>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<ContentPlanExecutionResult>> ExecuteAsync(
        ContentInstallPlan plan,
        IProgress<ContentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Kind is ContentKind.ModPack or ContentKind.DataPack)
        {
            return Result<ContentPlanExecutionResult>.Fail(
                "content.plan.unsupported_kind",
                "该内容种类不使用实例内容计划执行入口。");
        }

        var instance = await _instances.GetAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ContentPlanExecutionResult>.Fail("instance.not_found", "实例不存在。");
        }

        if (_time.GetUtcNow() > plan.ExpiresAt)
        {
            return Result<ContentPlanExecutionResult>.Fail(
                "content.plan.stale",
                "安装计划已过期，请重新生成计划。");
        }

        if (!string.Equals(instance.GameVersion, plan.GameVersion, StringComparison.Ordinal) ||
            !string.Equals(instance.Loader, plan.Loader, StringComparison.OrdinalIgnoreCase))
        {
            return Result<ContentPlanExecutionResult>.Fail(
                "content.plan.stale",
                "实例版本或加载器在生成计划后发生变化，请重新生成计划。");
        }

        if (!plan.IsInstallable)
        {
            return Result<ContentPlanExecutionResult>.Fail(
                "content.plan.blocked",
                "计划存在阻断项或没有可安装文件，未执行任何写入。",
                string.Join("; ", plan.Blockers));
        }

        var warnings = new List<string>(plan.Warnings);
        var files = DeduplicateFiles(plan, warnings);
        if (files.Count == 0)
        {
            return Result<ContentPlanExecutionResult>.Fail(
                "content.plan.empty",
                "计划没有可安装文件。");
        }

        var requests = new List<DownloadRequest>(files.Count);
        var staging = StagingDirectory();
        foreach (var file in files)
        {
            if (!TryCreateAllowedUri(file, out var uri))
            {
                return Result<ContentPlanExecutionResult>.Fail(
                    "content.plan.invalid_file",
                    $"计划文件 {file.FileName} 的下载地址不受允许来源约束，已拒绝安装。");
            }

            requests.Add(new DownloadRequest(
                Path.Combine(staging, file.FileName!),
                [uri],
                file.Sha1,
                HashingAlgorithm.Sha1,
                file.Size,
                file.FileName));
        }

        Directory.CreateDirectory(staging);

        try
        {
            var downloading = _localizer.Get("content.stage.download");
            var forwarding = new ForwardingProgress<DownloadProgress>(update =>
                progress?.Report(new ContentProgress(
                    "download",
                    downloading,
                    update.CompletedFiles,
                    update.TotalFiles)
                {
                    CompletedBytes = update.CompletedBytes,
                    TotalBytes = update.IsTotalBytesKnown ? update.TotalBytes : 0,
                    BytesPerSecond = update.BytesPerSecond,
                    CurrentFile = update.CurrentFile,
                }));

            try
            {
                await _downloads.DownloadAsync(requests, forwarding, cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadException exception)
            {
                return Result<ContentPlanExecutionResult>.Fail(
                    "download.failed",
                    $"下载失败：{exception.Message}",
                    string.Join("; ", exception.Failures.Take(5)));
            }

            var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
            var targetDirectory = Path.Combine(gameDirectory, ContentKinds.FolderName(plan.Kind));
            Directory.CreateDirectory(targetDirectory);
            var operations = requests
                .Select(request =>
                {
                    var destination = Path.Combine(targetDirectory, Path.GetFileName(request.DestinationPath));
                    var relative = Path.GetRelativePath(gameDirectory, destination)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    return InstanceChangeOperation.CopyFrom(relative, request.DestinationPath);
                })
                .ToArray();

            var installedFiles = requests
                .Select(request => Path.GetFileName(request.DestinationPath))
                .ToArray();
            var written = await _transactions.ExecuteAsync(
                instance.Id,
                new InstanceChangePlan(operations, "安装内容文件"),
                cancellationToken).ConfigureAwait(false);
            if (!written.IsSuccess)
            {
                return Result<ContentPlanExecutionResult>.Fail(written.Error!);
            }

            _logger.LogInformation(
                "计划 {PlanId} 已应用到实例 {InstanceId}：{Source}/{Kind}，文件 {Files}。",
                plan.PlanId,
                plan.InstanceId,
                plan.Source,
                plan.Kind,
                string.Join(", ", installedFiles));
            return Result<ContentPlanExecutionResult>.Ok(new ContentPlanExecutionResult(
                plan.PlanId,
                plan.InstanceId,
                plan.Kind,
                installedFiles,
                warnings));
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// 同名文件只保留第一个（依赖图可能在不同分支解析到同一文件名）；保留警告，不静默覆盖。
    /// </summary>
    private static IReadOnlyList<ContentDependencyNode> DeduplicateFiles(
        ContentInstallPlan plan,
        List<string> warnings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ContentDependencyNode>();
        foreach (var file in plan.Files)
        {
            if (!seen.Add(file.FileName!))
            {
                var warning = $"计划中存在重复文件名 {file.FileName}，已只安装第一份。";
                if (!warnings.Contains(warning, StringComparer.Ordinal))
                {
                    warnings.Add(warning);
                }

                continue;
            }

            result.Add(file);
        }

        return result;
    }

    private string StagingDirectory() => Path.Combine(
        _paths.CacheDirectory,
        "content-staging",
        Guid.NewGuid().ToString("N"));

    private static bool TryCreateAllowedUri(ContentDependencyNode file, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(file.DownloadUrl) ||
            !Uri.TryCreate(file.DownloadUrl, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (file.Source == ContentSourceKind.Modrinth)
        {
            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !(string.Equals(parsed.Host, "modrinth.com", StringComparison.OrdinalIgnoreCase) ||
                  parsed.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            uri = parsed;
            return true;
        }

        return CurseForgeDownloads.TryCreateUri(parsed.ToString(), out uri);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // staging 残留不影响已提交的结果；缓存清理会在之后回收。
        }
    }

    private sealed class ForwardingProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
