// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Storage;

public sealed record StorageInventoryScanOptions
{
    /// <summary>每个分类最多在 UI 中列出的文件数；总量仍统计全部文件。</summary>
    public int MaxEntriesPerCategory { get; init; } = 200;

    /// <summary>一次扫描最多处理的文件数；超过时标记为不完整，不伪造“没有更多文件”。</summary>
    public int MaxFiles { get; init; } = 250_000;
}

public interface IStorageInventoryService
{
    Task<StorageInventory> ScanAsync(
        StorageInventoryScanOptions? options = null,
        IProgress<StorageScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<StorageDeleteResult> DeleteAsync(
        IReadOnlyList<StorageFileEntry> entries,
        IProgress<StorageDeleteProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 存储清单的唯一扫描与清理入口（ADR 0022、storage-management.md）。
/// 扫描只读取并分类；删除只处理白名单中仍满足所有权、路径、年龄与活动状态的项目。
/// 页面不拼接路径、不自行遍历目录，也不根据删除结果推算新的存储数字。
/// </summary>
public sealed class StorageInventoryService(
    IAppPaths paths,
    IInstanceService instances,
    DownloadArtifactLifecycle downloads,
    IGameSessionService sessions,
    ILogger<StorageInventoryService> logger) : IStorageInventoryService
{
    public async Task<StorageInventory> ScanAsync(
        StorageInventoryScanOptions? options = null,
        IProgress<StorageScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StorageInventoryScanOptions();
        var now = DateTimeOffset.UtcNow;
        var scanners = StorageCategorySpec.All.ToDictionary(
            spec => spec.Id,
            spec => new CategoryAccumulator(spec, options.MaxEntriesPerCategory),
            StringComparer.Ordinal);
        var diskBytes = new Dictionary<string, Dictionary<StorageGroup, long>>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeRoots = ActiveRoots();
        var fileCount = 0;
        var complete = true;
        var rootIndex = 0;
        var roots = new List<ScanRoot>();

        var allInstances = await instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var instance in allInstances)
        {
            var gameDirectory = InstancePaths.GetGameDirectory(paths, instance);
            if (Directory.Exists(gameDirectory))
            {
                roots.Add(new ScanRoot(gameDirectory, (full, root) => ClassifyInstance(instance, root, full)));
            }
        }

        if (Directory.Exists(paths.GameRoot) &&
            !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.GameRoot)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Root)),
                StringComparison.OrdinalIgnoreCase))
        {
            roots.Add(new ScanRoot(paths.GameRoot, ClassifySharedGameRoot));
        }

        if (Directory.Exists(paths.Root))
        {
            roots.Add(new ScanRoot(paths.Root, ClassifyEclRoot));
        }

        await Task.Run(
                () =>
                {
                    foreach (var root in roots)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        rootIndex++;
                        progress?.Report(new StorageScanProgress(
                            "scanning",
                            root.Path,
                            roots.Count == 0 ? null : (double)rootIndex / roots.Count));
                        try
                        {
                            foreach (var file in EnumerateFiles(root.Path, options.MaxFiles - fileCount, cancellationToken))
                            {
                                fileCount++;
                                if (fileCount > options.MaxFiles)
                                {
                                    complete = false;
                                    warnings.Add($"文件数量超过扫描上限 {options.MaxFiles}，清单不完整。");
                                    break;
                                }

                                if (!seen.Add(file))
                                {
                                    continue;
                                }

                                var resolved = root.Classify(file, root.Path);
                                if (resolved is null)
                                {
                                    continue;
                                }

                                ScanFile(file, resolved, scanners, diskBytes, activeRoots, now);
                            }
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            complete = false;
                            warnings.Add($"{root.Path}：{exception.Message}");
                        }

                        if (fileCount > options.MaxFiles)
                        {
                            break;
                        }
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        var categories = StorageCategorySpec.All
            .Select(spec => scanners[spec.Id].Build())
            .ToArray();
        var disks = BuildDisks(diskBytes, warnings);
        var known = categories.Sum(category => category.KnownBytes);
        var unavailable = categories.Sum(category => category.UnknownSizeCount);
        if (!complete)
        {
            warnings.Insert(0, "扫描未完整完成；未知/不可访问项不会被当作 0 字节或空分类。");
        }

        progress?.Report(new StorageScanProgress("complete", null, 1));
        return new StorageInventory(
            categories,
            disks,
            known,
            unavailable,
            allInstances.Count,
            complete,
            now,
            warnings);
    }

    public Task<StorageDeleteResult> DeleteAsync(
        IReadOnlyList<StorageFileEntry> entries,
        IProgress<StorageDeleteProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Delete(entries, progress, cancellationToken),
            cancellationToken);

    private StorageDeleteResult Delete(
        IReadOnlyList<StorageFileEntry> entries,
        IProgress<StorageDeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        var activeRoots = ActiveRoots();
        var results = new List<StorageDeleteItemResult>(entries.Count);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new StorageDeleteProgress(entry.Id, StorageDeleteItemState.Running, 0, null));

            var decision = EvaluateDeletion(entry, activeRoots, DateTimeOffset.UtcNow);
            if (decision is not null)
            {
                var state = decision.Value.Skip ? StorageDeleteItemState.Skipped : StorageDeleteItemState.Failed;
                var result = new StorageDeleteItemResult(entry, state, 0, decision.Value.Reason);
                results.Add(result);
                progress?.Report(new StorageDeleteProgress(entry.Id, state, 0, decision.Value.Reason));
                continue;
            }

            try
            {
                var length = new FileInfo(entry.FullPath).Length;
                File.Delete(entry.FullPath);
                results.Add(new StorageDeleteItemResult(entry, StorageDeleteItemState.Succeeded, length, null));
                progress?.Report(new StorageDeleteProgress(entry.Id, StorageDeleteItemState.Succeeded, length, null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                results.Add(new StorageDeleteItemResult(entry, StorageDeleteItemState.Failed, 0, exception.Message));
                progress?.Report(new StorageDeleteProgress(entry.Id, StorageDeleteItemState.Failed, 0, exception.Message));
            }
        }

        return new StorageDeleteResult(results);
    }

    private void ScanFile(
        string file,
        ClassifiedPath resolved,
        Dictionary<string, CategoryAccumulator> scanners,
        Dictionary<string, Dictionary<StorageGroup, long>> diskBytes,
        IReadOnlyList<string> activeRoots,
        DateTimeOffset now)
    {
        if (!scanners.TryGetValue(resolved.CategoryId, out var accumulator))
        {
            return;
        }

        long? size = null;
        DateTimeOffset? lastWrite = null;
        try
        {
            var info = new FileInfo(file);
            if (info.Exists)
            {
                size = info.Length;
                lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "无法读取存储文件信息：{File}", file);
        }

        var rule = StorageCleanupPolicy.Evaluate(
            resolved.CategoryId,
            file,
            lastWrite,
            now,
            activeRoots,
            downloads);
        var entry = new StorageFileEntry
        {
            Id = "file:" + file,
            CategoryId = resolved.CategoryId,
            Group = accumulator.Spec.Group,
            FullPath = file,
            AllowedRoot = resolved.AllowedRoot,
            Size = size,
            LastWriteTimeUtc = lastWrite,
            InstanceId = resolved.InstanceId,
            InstanceName = resolved.InstanceName,
            Owner = resolved.Owner,
            CanClean = rule is { CanClean: true } && size is not null,
            ProtectionReason = size is null ? "sizeUnknown" : rule?.Reason,
            Risk = rule?.Risk ?? StorageRiskLevel.None,
            CleanupRule = rule?.RuleId,
        };

        accumulator.Add(entry);
        if (size is { } known)
        {
            var disk = Path.GetPathRoot(file);
            if (!string.IsNullOrWhiteSpace(disk))
            {
                if (!diskBytes.TryGetValue(disk, out var groups))
                {
                    groups = [];
                    diskBytes[disk] = groups;
                }

                groups[accumulator.Spec.Group] = groups.GetValueOrDefault(accumulator.Spec.Group) + known;
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(
        string root,
        int remaining,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root) || remaining <= 0)
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(
                    root,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        MaxRecursionDepth = 24,
                    })
                .Take(remaining)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IReadOnlyList<string> ActiveRoots()
    {
        var roots = new List<string>();
        foreach (var session in sessions.List())
        {
            var state = session.Manifest.State;
            if (state is not (
                GameSessionState.Preparing
                or GameSessionState.Starting
                or GameSessionState.Running
                or GameSessionState.GameExitedDraining
                or GameSessionState.RecoveryPending))
            {
                continue;
            }

            roots.Add(session.DirectoryPath);
            if (!string.IsNullOrWhiteSpace(session.Manifest.GameDirectory))
            {
                roots.Add(session.Manifest.GameDirectory);
            }
        }

        return roots;
    }

    private (bool Skip, string Reason)? EvaluateDeletion(
        StorageFileEntry entry,
        IReadOnlyList<string> activeRoots,
        DateTimeOffset now)
    {
        var spec = StorageCategorySpec.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, entry.CategoryId, StringComparison.Ordinal));
        if (spec is null || spec.Group != entry.Group)
        {
            return (true, "categoryChanged");
        }

        if (!StoragePathSafety.IsContainedIn(entry.AllowedRoot, entry.FullPath) ||
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.FullPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.AllowedRoot)),
                StringComparison.OrdinalIgnoreCase))
        {
            return (true, "outsideAllowedRoot");
        }

        if (!File.Exists(entry.FullPath))
        {
            return (true, "missing");
        }

        if ((File.GetAttributes(entry.FullPath) & FileAttributes.ReparsePoint) != 0)
        {
            return (true, "reparsePoint");
        }

        DateTimeOffset? lastWrite = null;
        try
        {
            lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(entry.FullPath), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (true, "unreadable");
        }

        var rule = StorageCleanupPolicy.Evaluate(
            entry.CategoryId,
            entry.FullPath,
            lastWrite,
            now,
            activeRoots,
            downloads);
        if (rule is null || !rule.CanClean)
        {
            return (true, rule?.Reason ?? "notWhitelisted");
        }

        return null;
    }

    private static IReadOnlyList<StorageDisk> BuildDisks(
        Dictionary<string, Dictionary<StorageGroup, long>> diskBytes,
        List<string> warnings)
    {
        var result = new List<StorageDisk>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                drives = [];
                warnings.Add($"无法枚举磁盘：{exception.Message}");
            }

            foreach (var drive in drives)
            {
                var root = drive.Name;
                if (seen.Contains(root))
                {
                    continue;
                }

                seen.Add(root);
                var eclBytes = diskBytes.GetValueOrDefault(root) ?? [];
                var known = eclBytes.Values.Sum();
                if (drive.DriveType is DriveType.NoRootDirectory or DriveType.Unknown)
                {
                    continue;
                }

                if (!drive.IsReady)
                {
                    result.Add(new StorageDisk(
                        root,
                        root,
                        StorageDiskState.Unavailable,
                        null,
                        null,
                        known,
                        eclBytes));
                    continue;
                }

                try
                {
                    result.Add(new StorageDisk(
                        root,
                        root,
                        StorageDiskState.Ready,
                        drive.TotalSize,
                        drive.AvailableFreeSpace,
                        known,
                        eclBytes));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    result.Add(new StorageDisk(
                        root,
                        root,
                        StorageDiskState.Unknown,
                        null,
                        null,
                        known,
                        eclBytes));
                    warnings.Add($"{root}：{exception.Message}");
                }
            }
        }

        foreach (var root in diskBytes.Keys)
        {
            if (seen.Contains(root))
            {
                continue;
            }

            var eclBytes = diskBytes[root];
            result.Add(new StorageDisk(
                root,
                root,
                StorageDiskState.Unknown,
                null,
                null,
                eclBytes.Values.Sum(),
                eclBytes));
        }

        if (result.Count == 0)
        {
            result.Add(new StorageDisk(
                Path.GetPathRoot(AppContext.BaseDirectory) ?? "/",
                Path.GetPathRoot(AppContext.BaseDirectory) ?? "/",
                StorageDiskState.Unknown,
                null,
                null,
                0,
                new Dictionary<StorageGroup, long>()));
        }

        return result
            .OrderBy(disk => disk.Root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ClassifiedPath? ClassifyInstance(GameInstance instance, string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var lower = relative.ToLowerInvariant();
        var owner = instance.Name;
        var category = lower switch
        {
            _ when lower.StartsWith("saves/", StringComparison.Ordinal) &&
                   lower.Contains("/datapacks/", StringComparison.Ordinal) => "instance.dataPacks",
            _ when lower.StartsWith("logs/debug/", StringComparison.Ordinal) ||
                   lower.StartsWith("natives/", StringComparison.Ordinal) ||
                   lower.StartsWith("caches/", StringComparison.Ordinal) ||
                   lower.StartsWith(".fabric/", StringComparison.Ordinal) ||
                   lower.StartsWith(".mixin.out/", StringComparison.Ordinal) => "instance.runtimeCache",
            _ when lower.StartsWith("logs/", StringComparison.Ordinal) => "instance.logs",
            _ when lower.StartsWith("crash-reports/", StringComparison.Ordinal) => "instance.crashReports",
            _ when lower.StartsWith("saves/", StringComparison.Ordinal) => "instance.saves",
            _ when lower.StartsWith("saves", StringComparison.Ordinal) => "instance.saves",
            _ when lower.StartsWith("mods/", StringComparison.Ordinal) => "instance.mods",
            _ when lower.StartsWith("config/worldedit/schematics/", StringComparison.Ordinal) ||
                   lower.StartsWith("config/litematica/", StringComparison.Ordinal) ||
                   lower.StartsWith("schematics/", StringComparison.Ordinal) ||
                   lower.StartsWith("schematic/", StringComparison.Ordinal) => "instance.schematics",
            _ when lower.StartsWith("config/", StringComparison.Ordinal) ||
                   lower.StartsWith("defaultconfigs/", StringComparison.Ordinal) => "instance.modConfig",
            _ when lower.StartsWith("resourcepacks/", StringComparison.Ordinal) => "instance.resourcePacks",
            _ when lower.StartsWith("shaderpacks/", StringComparison.Ordinal) => "instance.shaderPacks",
            _ when lower.StartsWith("datapacks/", StringComparison.Ordinal) => "instance.dataPacks",
            _ when lower.StartsWith("screenshots/", StringComparison.Ordinal) => "instance.screenshots",
            _ when lower.StartsWith("server-resource-packs/", StringComparison.Ordinal) => "instance.serverResourcePacks",
            _ when lower.StartsWith("versions/", StringComparison.Ordinal) ||
                   lower.StartsWith("libraries/", StringComparison.Ordinal) ||
                   lower.StartsWith("assets/", StringComparison.Ordinal) => "instance.versionFiles",
            _ => "instance.other",
        };

        return new ClassifiedPath(category, root, owner, instance.Id, instance.Name, null);
    }

    private static ClassifiedPath? ClassifySharedGameRoot(string fullPath, string root)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var lower = relative.ToLowerInvariant();
        if (lower.StartsWith("instances/", StringComparison.Ordinal))
        {
            return null;
        }

        var category = lower switch
        {
            _ when lower.StartsWith("logs/", StringComparison.Ordinal) => "instance.logs",
            _ when lower.StartsWith("versions/", StringComparison.Ordinal) ||
                   lower.StartsWith("libraries/", StringComparison.Ordinal) ||
                   lower.StartsWith("assets/", StringComparison.Ordinal) => "instance.versionFiles",
            _ => "instance.other",
        };

        return new ClassifiedPath(category, root, null, null, null, null);
    }

    private static ClassifiedPath? ClassifyEclRoot(string fullPath, string root)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var lower = relative.ToLowerInvariant();
        if (lower.StartsWith("runtimes/", StringComparison.Ordinal) ||
            lower.StartsWith("java/", StringComparison.Ordinal))
        {
            // Java 运行环境不属于本页面的分类与清理范围。
            return null;
        }

        if (lower.StartsWith("additions/", StringComparison.Ordinal))
        {
            var segments = lower.Split('/');
            var owner = segments.Length > 1 ? segments[1] : null;
            var category = lower switch
            {
                _ when lower.Contains("/cache/", StringComparison.Ordinal) => "additions.cache",
                _ when lower.Contains("/data/", StringComparison.Ordinal) ||
                       lower.EndsWith(".db", StringComparison.Ordinal) ||
                       lower.EndsWith(".sqlite", StringComparison.Ordinal) => "additions.database",
                _ when lower.Contains("/tmp/", StringComparison.Ordinal) ||
                       lower.Contains("/temp/", StringComparison.Ordinal) => "additions.temp",
                _ => "additions.other",
            };
            return new ClassifiedPath(category, Path.Combine(root, "additions"), owner, null, null, null);
        }

        if (lower.StartsWith("snapshots/", StringComparison.Ordinal))
        {
            var segments = lower.Split('/');
            var owner = segments.Length > 1 ? segments[1] : null;
            return new ClassifiedPath("backups.instance", Path.Combine(root, "snapshots"), owner, null, null, null);
        }

        if (lower.StartsWith("backups/", StringComparison.Ordinal))
        {
            var category = lower switch
            {
                _ when lower.StartsWith("backups/worlds/", StringComparison.Ordinal) => "backups.world",
                _ when lower.StartsWith("backups/config/", StringComparison.Ordinal) ||
                       lower.EndsWith(".bak", StringComparison.Ordinal) => "backups.config",
                _ => "backups.other",
            };
            return new ClassifiedPath(category, Path.Combine(root, "backups"), null, null, null, null);
        }

        if (lower.StartsWith("downloads/", StringComparison.Ordinal))
        {
            return new ClassifiedPath("ecl.downloads", Path.Combine(root, "downloads"), null, null, null, null);
        }

        if (lower.StartsWith("cache/", StringComparison.Ordinal))
        {
            var category = lower switch
            {
                _ when lower.StartsWith("cache/install/", StringComparison.Ordinal) => "ecl.installTemp",
                _ when lower.StartsWith("cache/tmp/", StringComparison.Ordinal) => "ecl.tempFiles",
                _ when lower.StartsWith("cache/thumbnails/", StringComparison.Ordinal) ||
                       lower.StartsWith("cache/images/", StringComparison.Ordinal) ||
                       lower.StartsWith("cache/screenshots/", StringComparison.Ordinal) => "ecl.imageCache",
                _ => "ecl.metadataCache",
            };
            return new ClassifiedPath(category, Path.Combine(root, "cache"), null, null, null, null);
        }

        if (lower.StartsWith("logs/", StringComparison.Ordinal))
        {
            var category = lower switch
            {
                _ when lower.StartsWith("logs/sessions/", StringComparison.Ordinal) => "ecl.diagnostics",
                _ when lower.StartsWith("logs/diagnostics/", StringComparison.Ordinal) => "ecl.diagnostics",
                _ when lower.StartsWith("logs/launch-status/", StringComparison.Ordinal) ||
                       lower.StartsWith("logs/launch/", StringComparison.Ordinal) => "ecl.other",
                _ when Path.GetFileName(lower).StartsWith("crash-", StringComparison.Ordinal) => "ecl.crashReports",
                _ => "ecl.logs",
            };
            return new ClassifiedPath(category, Path.Combine(root, "logs"), null, null, null, null);
        }

        if (lower.StartsWith("config/diagnostics/", StringComparison.Ordinal))
        {
            return new ClassifiedPath("ecl.diagnostics", Path.Combine(root, "config", "diagnostics"), null, null, null, null);
        }

        if (lower.StartsWith("staging/", StringComparison.Ordinal) ||
            lower.EndsWith(".old", StringComparison.Ordinal))
        {
            return new ClassifiedPath("ecl.updateLeftovers", root, null, null, null, null);
        }

        if (lower.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return new ClassifiedPath("ecl.tempFiles", root, null, null, null, null);
        }

        return new ClassifiedPath("ecl.other", root, null, null, null, null);
    }

    private sealed record ScanRoot(string Path, Func<string, string, ClassifiedPath?> Classify);

    private sealed record ClassifiedPath(
        string CategoryId,
        string AllowedRoot,
        string? Owner,
        string? InstanceId,
        string? InstanceName,
        string? Detail);

    private sealed class CategoryAccumulator(StorageCategorySpec spec, int maxEntries)
    {
        private readonly List<StorageFileEntry> _entries = [];
        private long _knownBytes;
        private int _knownFiles;
        private int _unknown;
        private bool _truncated;

        public StorageCategorySpec Spec { get; } = spec;

        public void Add(StorageFileEntry entry)
        {
            if (entry.Size is null)
            {
                _unknown++;
            }
            else
            {
                _knownBytes += entry.Size.Value;
                _knownFiles++;
            }

            if (_entries.Count < maxEntries * 4)
            {
                _entries.Add(entry);
            }
            else
            {
                _truncated = true;
            }
        }

        public StorageCategory Build() =>
            new(
                Spec.Id,
                Spec.Group,
                _entries
                    .OrderByDescending(entry => entry.Size ?? -1)
                    .ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
                    .Take(maxEntries)
                    .ToArray(),
                _knownBytes,
                _knownFiles,
                _unknown,
                _truncated || _entries.Count > maxEntries);
    }
}

/// <summary>
/// 清理白名单的唯一策略（ADR 0022 §3–§8）。新增类别必须同时更新这里与测试，
/// 不得让页面通过勾选绕过所有权、路径、年龄或活动状态判断。
/// </summary>
internal static class StorageCleanupPolicy
{
    private static readonly TimeSpan ShortRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan LongRetention = TimeSpan.FromDays(14);

    public static CleanupDecision? Evaluate(
        string categoryId,
        string path,
        DateTimeOffset? lastWriteUtc,
        DateTimeOffset now,
        IReadOnlyList<string> activeRoots,
        DownloadArtifactLifecycle downloads)
    {
        if (activeRoots.Any(root => StoragePathSafety.IsContainedIn(root, path)))
        {
            return new CleanupDecision(false, "activeTask", StorageRiskLevel.None, null);
        }

        if (downloads.IsActive(path))
        {
            return new CleanupDecision(false, "activeDownload", StorageRiskLevel.None, null);
        }

        if (lastWriteUtc is null)
        {
            return new CleanupDecision(false, "unreadable", StorageRiskLevel.None, null);
        }

        var age = now - lastWriteUtc.Value;
        return categoryId switch
        {
            "ecl.downloads" when Path.GetExtension(path).Equals(".part", StringComparison.OrdinalIgnoreCase) =>
                new CleanupDecision(false, "inProgressDownload", StorageRiskLevel.None, null),
            "ecl.downloads" => AgeDecision(age, ShortRetention, "downloadCache"),
            "ecl.installTemp" => AgeDecision(age, ShortRetention, "installTemp"),
            "ecl.updateLeftovers" => AgeDecision(age, ShortRetention, "updateLeftover"),
            "ecl.logs" => AgeDecision(age, LongRetention, "eclLog"),
            "ecl.diagnostics" when path.Replace('\\', '/').Contains("/logs/sessions/", StringComparison.OrdinalIgnoreCase) =>
                new CleanupDecision(false, "sessionEvidence", StorageRiskLevel.None, null),
            "ecl.diagnostics" => AgeDecision(age, ShortRetention, "diagnosticTemp"),
            "ecl.imageCache" => AgeDecision(age, LongRetention, "imageCache"),
            "ecl.metadataCache" => AgeDecision(age, LongRetention, "metadataCache"),
            "ecl.tempFiles" => AgeDecision(age, ShortRetention, "tempFile"),
            "instance.logs" => AgeDecision(age, LongRetention, "instanceLog"),
            "instance.runtimeCache" => AgeDecision(age, LongRetention, "runtimeCache"),
            "additions.cache" => AgeDecision(age, LongRetention, "additionCache"),
            "additions.temp" => AgeDecision(age, ShortRetention, "additionTemp"),
            "instance.versionFiles" => UserData("versionFiles"),
            "instance.saves" or "instance.mods" or "instance.modConfig" or "instance.resourcePacks"
                or "instance.shaderPacks" or "instance.dataPacks" or "instance.schematics"
                or "instance.screenshots" or "instance.serverResourcePacks" or "instance.crashReports"
                or "instance.other" => UserData("userData"),
            "ecl.crashReports" => UserData("crashEvidence"),
            "ecl.other" => UserData("eclData"),
            "additions.database" or "additions.other" => UserData("additionData"),
            "backups.instance" or "backups.world" or "backups.config" or "backups.other" =>
                UserData("backup"),
            _ => UserData("notWhitelisted"),
        };
    }

    private static CleanupDecision AgeDecision(TimeSpan age, TimeSpan retention, string rule) =>
        age >= retention
            ? new CleanupDecision(true, null, rule == "runtimeCache" ? StorageRiskLevel.High : StorageRiskLevel.Caution, rule)
            : new CleanupDecision(false, "tooRecent", StorageRiskLevel.None, rule);

    private static CleanupDecision UserData(string reason) =>
        new(false, reason, StorageRiskLevel.None, null);
}

internal sealed record CleanupDecision(
    bool CanClean,
    string? Reason,
    StorageRiskLevel Risk,
    string? RuleId);
