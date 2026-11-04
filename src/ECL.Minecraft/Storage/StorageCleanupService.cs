// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.Instances;

namespace Ecl.Minecraft.Storage;

public enum CleanupItemKind
{
    DownloadPart,
    RebuildableCache,
    InstanceLog,
    DiagnosticTemp,
}

public sealed record CleanupPreviewItem(
    string Id,
    CleanupItemKind Kind,
    string Path,
    string AllowedRoot,
    long Bytes,
    string? InstanceId,
    string Reason);

public sealed record CleanupPreview(
    IReadOnlyList<CleanupPreviewItem> Items,
    int SkippedCount,
    IReadOnlyList<string> Warnings)
{
    public long TotalBytes => Items.Sum(item => item.Bytes);

    public bool HasItems => Items.Count > 0;
}

public sealed record CleanupExecutionResult(
    int Deleted,
    int Skipped,
    int Failed,
    long BytesFreed,
    IReadOnlyList<string> Errors);

public sealed record StorageCleanupRequest
{
    public bool IncludeDownloadParts { get; init; } = true;

    public bool IncludeRebuildableCache { get; init; } = true;

    public bool IncludeInstanceLogs { get; init; } = true;

    public bool IncludeDiagnosticTemps { get; init; } = true;

    /// <summary>为空时处理所有 ECL 实例；指定时只处理该实例的日志。</summary>
    public string? InstanceId { get; init; }
}

/// <summary>
/// 白名单清理。只处理下载 .part、可重建缓存、实例历史日志与诊断临时文件；
/// 世界存档、模组、配置、资源包、截图和未知文件永远不在清理范围内。
/// 预览与执行分离，执行前重新校验路径、活动下载与文件年龄。
/// </summary>
public sealed class StorageCleanupService(
    IAppPaths paths,
    IInstanceService instances,
    DownloadArtifactLifecycle artifacts)
{
    private static readonly TimeSpan PartRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CacheRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan DiagnosticTempRetention = TimeSpan.FromDays(7);

    public async Task<CleanupPreview> PreviewAsync(
        StorageCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        var items = new List<CleanupPreviewItem>();
        var warnings = new List<string>();
        var skipped = 0;

        if (request.IncludeDownloadParts)
        {
            foreach (var root in new[] { paths.DownloadsDirectory, paths.CacheDirectory })
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var part in EnumerateFiles(root, "*.part", cancellationToken))
                {
                    if (!IsOlderThan(part, PartRetention))
                    {
                        skipped++;
                        continue;
                    }

                    using var lease = artifacts.TryAcquire(part);
                    if (lease is null)
                    {
                        skipped++;
                        continue;
                    }

                    if (!File.Exists(part))
                    {
                        continue;
                    }

                    items.Add(new CleanupPreviewItem(
                        "part:" + Path.GetRelativePath(paths.Root, part),
                        CleanupItemKind.DownloadPart,
                        part,
                        root,
                        new FileInfo(part).Length,
                        null,
                        "超过保留期且没有被活动下载引用的 .part 临时产物。"));
                }
            }
        }

        if (request.IncludeRebuildableCache)
        {
            foreach (var file in EnumerateFiles(paths.CacheDirectory, "*", cancellationToken))
            {
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".part.json", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".part.lock", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsOlderThan(file, CacheRetention))
                {
                    continue;
                }

                items.Add(new CleanupPreviewItem(
                    "cache:" + Path.GetRelativePath(paths.CacheDirectory, file),
                    CleanupItemKind.RebuildableCache,
                    file,
                    paths.CacheDirectory,
                    SafeLength(file),
                    null,
                    "ECL 可重新生成的缓存文件。"));
            }
        }

        if (request.IncludeInstanceLogs)
        {
            var all = await instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
            var selected = request.InstanceId is null
                ? all
                : all.Where(instance => string.Equals(instance.Id, request.InstanceId, StringComparison.Ordinal)).ToArray();
            foreach (var instance in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var logRoots = new[]
                {
                    paths.GetInstanceLogsDirectory(instance.Id),
                    Path.Combine(InstancePaths.GetGameDirectory(paths, instance), "logs"),
                };
                foreach (var root in logRoots.Where(Directory.Exists))
                {
                    foreach (var file in EnumerateFiles(root, "*", cancellationToken)
                                 .Where(file => IsLogFile(file)))
                    {
                        if (!IsOlderThan(file, LogRetention))
                        {
                            continue;
                        }

                        items.Add(new CleanupPreviewItem(
                            "log:" + Path.GetRelativePath(root, file),
                            CleanupItemKind.InstanceLog,
                            file,
                            root,
                            SafeLength(file),
                            instance.Id,
                            "超过保留期的实例历史日志。"));
                    }
                }
            }
        }

        if (request.IncludeDiagnosticTemps)
        {
            var diagnostics = Path.Combine(paths.ConfigDirectory, "diagnostics");
            foreach (var file in EnumerateFiles(diagnostics, "*.tmp", cancellationToken))
            {
                if (!IsOlderThan(file, DiagnosticTempRetention))
                {
                    continue;
                }

                items.Add(new CleanupPreviewItem(
                    "diag:" + Path.GetRelativePath(diagnostics, file),
                    CleanupItemKind.DiagnosticTemp,
                    file,
                    diagnostics,
                    SafeLength(file),
                    null,
                    "诊断导出留下的临时文件。"));
            }
        }

        return new CleanupPreview(items, skipped, warnings);
    }

    public async Task<CleanupExecutionResult> ExecuteAsync(
        CleanupPreview preview,
        CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        var skipped = 0;
        var failed = 0;
        long freed = 0;
        var errors = new List<string>();
        foreach (var item in preview.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StoragePathSafety.IsContainedIn(item.AllowedRoot, item.Path) ||
                string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.AllowedRoot)),
                    StringComparison.OrdinalIgnoreCase))
            {
                failed++;
                errors.Add($"拒绝越界清理：{item.Path}");
                continue;
            }

            if (!File.Exists(item.Path))
            {
                skipped++;
                continue;
            }

            if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0)
            {
                skipped++;
                continue;
            }

            if (item.Kind == CleanupItemKind.DownloadPart)
            {
                using var lease = artifacts.TryAcquire(item.Path);
                if (lease is null)
                {
                    skipped++;
                    continue;
                }

                if (!IsOlderThan(item.Path, PartRetention))
                {
                    skipped++;
                    continue;
                }
            }

            try
            {
                var length = SafeLength(item.Path);
                File.Delete(item.Path);
                if (item.Kind == CleanupItemKind.DownloadPart)
                {
                    TryDelete(DownloadArtifactLifecycle.MetadataPath(item.Path));
                }

                deleted++;
                freed += length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
                errors.Add($"删除失败：{item.Path}（{exception.Message}）");
            }
        }

        return new CleanupExecutionResult(deleted, skipped, failed, freed, errors);
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(
                    root,
                    pattern,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        MaxRecursionDepth = 8,
                    })
                .Take(50_000)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsOlderThan(string path, TimeSpan retention)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= retention;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsLogFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 元数据残留由后续清理周期处理。
        }
    }
}
