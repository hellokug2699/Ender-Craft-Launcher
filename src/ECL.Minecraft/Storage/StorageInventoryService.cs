// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Storage;

public sealed record StorageInventoryScanOptions
{
    /// <summary>每个分类最多在 UI 中列出的项目数；总量仍统计全部文件。</summary>
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
/// 存储清理清单的唯一扫描与清理入口（ADR 0022）。扫描只读取、统计并分类；
/// 删除只处理白名单中仍满足所有权、路径、年龄与活动状态的项目。
/// 页面不拼接路径、不自行遍历目录，也不根据删除结果推算新的存储数字。
/// <para>
/// 清理列表只包含可安全重建的垃圾：缓存、临时文件、历史日志、崩溃报告、截图、
/// 服务器资源包缓存与存档文件夹。模组、配置、资源包、光影包、数据包、投影、
/// 版本文件、Java 运行时、扩展数据库与备份只参与磁盘占用统计，不进入列表。
/// 存档以世界文件夹为单位列出并整体删除，不把 .mca 等区域文件拆成独立条目。
/// </para>
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
            if (!Directory.Exists(gameDirectory))
            {
                continue;
            }

            roots.Add(new ScanRoot(
                gameDirectory,
                (full, root) => ClassifyInstance(instance, root, full),
                BuildSaveTargets(instance, gameDirectory)));
        }

        if (Directory.Exists(paths.GameRoot) && !IsSameDirectory(paths.GameRoot, paths.Root))
        {
            roots.Add(new ScanRoot(paths.GameRoot, ClassifySharedGameRoot, []));
        }

        if (Directory.Exists(paths.Root))
        {
            roots.Add(new ScanRoot(paths.Root, ClassifyEclRoot, []));
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
                            // 存档按世界文件夹整体统计，文件级枚举跳过 saves/，避免重复列出 .mca。
                            foreach (var target in root.Directories)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var (size, lastWrite) = MeasureDirectory(target.Path, cancellationToken);
                                ScanDirectory(target, size, lastWrite, scanners, diskBytes, activeRoots, now);
                            }

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
        // 已知 ECL 占用包含未列入清理列表的模组/配置/版本等文件，与磁盘分布条口径一致。
        var known = disks.Sum(disk => disk.KnownEclBytes);
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
                long freed;
                if (entry.IsDirectory)
                {
                    freed = MeasureDirectoryBytes(entry.FullPath) ?? entry.Size ?? 0;
                    Directory.Delete(entry.FullPath, recursive: true);
                }
                else
                {
                    freed = new FileInfo(entry.FullPath).Length;
                    File.Delete(entry.FullPath);
                }

                results.Add(new StorageDeleteItemResult(entry, StorageDeleteItemState.Succeeded, freed, null));
                progress?.Report(new StorageDeleteProgress(entry.Id, StorageDeleteItemState.Succeeded, freed, null));
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

        if (resolved.CategoryId is { } categoryId &&
            scanners.TryGetValue(categoryId, out var accumulator))
        {
            var rule = StorageCleanupPolicy.Evaluate(
                categoryId,
                file,
                lastWrite,
                now,
                activeRoots,
                downloads);
            var entry = new StorageFileEntry
            {
                Id = "file:" + file,
                CategoryId = categoryId,
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
        }

        AddDiskBytes(diskBytes, resolved.Group, file, size);
    }

    private void ScanDirectory(
        DirectoryTarget target,
        long? size,
        DateTimeOffset? lastWrite,
        Dictionary<string, CategoryAccumulator> scanners,
        Dictionary<string, Dictionary<StorageGroup, long>> diskBytes,
        IReadOnlyList<string> activeRoots,
        DateTimeOffset now)
    {
        if (!scanners.TryGetValue(target.CategoryId, out var accumulator))
        {
            return;
        }

        var rule = StorageCleanupPolicy.Evaluate(
            target.CategoryId,
            target.Path,
            lastWrite,
            now,
            activeRoots,
            downloads);
        var entry = new StorageFileEntry
        {
            Id = "dir:" + target.Path,
            CategoryId = target.CategoryId,
            Group = accumulator.Spec.Group,
            FullPath = target.Path,
            AllowedRoot = target.AllowedRoot,
            IsDirectory = true,
            Size = size,
            LastWriteTimeUtc = lastWrite,
            InstanceId = target.InstanceId,
            InstanceName = target.InstanceName,
            Owner = target.Owner,
            CanClean = rule is { CanClean: true } && size is not null,
            ProtectionReason = size is null ? "sizeUnknown" : rule?.Reason,
            Risk = rule?.Risk ?? StorageRiskLevel.None,
            CleanupRule = rule?.RuleId,
        };

        accumulator.Add(entry);
        AddDiskBytes(diskBytes, accumulator.Spec.Group, target.Path, size);
    }

    private static void AddDiskBytes(
        Dictionary<string, Dictionary<StorageGroup, long>> diskBytes,
        StorageGroup group,
        string path,
        long? size)
    {
        if (size is not { } known)
        {
            return;
        }

        var disk = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(disk))
        {
            return;
        }

        if (!diskBytes.TryGetValue(disk, out var groups))
        {
            groups = [];
            diskBytes[disk] = groups;
        }

        groups[group] = groups.GetValueOrDefault(group) + known;
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

    private static IReadOnlyList<DirectoryTarget> BuildSaveTargets(GameInstance instance, string gameDirectory)
    {
        var savesRoot = Path.Combine(gameDirectory, "saves");
        if (!Directory.Exists(savesRoot))
        {
            return [];
        }

        var targets = new List<DirectoryTarget>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(
                         savesRoot,
                         "*",
                         new EnumerationOptions
                         {
                             RecurseSubdirectories = false,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
            {
                targets.Add(new DirectoryTarget(
                    "instance.saves",
                    StorageGroup.MinecraftInstance,
                    directory,
                    savesRoot,
                    instance.Id,
                    instance.Name,
                    instance.Name));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 无法枚举时不伪造世界列表；磁盘占用仍会由文件扫描统计。
        }

        return targets;
    }

    /// <summary>统计世界文件夹大小与最近修改时间；无法完整读取时返回未知大小。</summary>
    private static (long? Size, DateTimeOffset? LastWrite) MeasureDirectory(
        string path,
        CancellationToken cancellationToken)
    {
        long total = 0;
        DateTimeOffset? latest = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(
                         path,
                         "*",
                         new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                             MaxRecursionDepth = 24,
                         }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    total += info.Length;
                    var time = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    if (latest is null || time > latest)
                    {
                        latest = time;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 单个文件不可读时仍保留其余统计，最终大小只是已知部分。
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }

        return (total, latest);
    }

    private static long? MeasureDirectoryBytes(string path)
    {
        try
        {
            return MeasureDirectory(path, CancellationToken.None).Size;
        }
        catch (OperationCanceledException)
        {
            return null;
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
            IsSameDirectory(entry.AllowedRoot, entry.FullPath))
        {
            return (true, "outsideAllowedRoot");
        }

        if (entry.IsDirectory)
        {
            // 目录条目只允许是被允许根的直接子目录（世界存档），不接受更深的路径。
            if (!Directory.Exists(entry.FullPath))
            {
                return (true, "missing");
            }

            var parent = Directory.GetParent(entry.FullPath)?.FullName;
            if (parent is null || !IsSameDirectory(parent, entry.AllowedRoot))
            {
                return (true, "outsideAllowedRoot");
            }
        }
        else if (!File.Exists(entry.FullPath))
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

    private static bool IsSameDirectory(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

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
        if (lower.StartsWith("saves/", StringComparison.Ordinal))
        {
            // 存档以世界文件夹为单位单独统计；文件级条目不再重复列出。
            return null;
        }

        string? category = lower switch
        {
            _ when lower.StartsWith("logs/debug/", StringComparison.Ordinal) ||
                   lower.StartsWith("natives/", StringComparison.Ordinal) ||
                   lower.StartsWith("caches/", StringComparison.Ordinal) ||
                   lower.StartsWith(".fabric/", StringComparison.Ordinal) ||
                   lower.StartsWith(".mixin.out/", StringComparison.Ordinal) => "instance.runtimeCache",
            _ when lower.StartsWith("logs/", StringComparison.Ordinal) => "instance.logs",
            _ when lower.StartsWith("crash-reports/", StringComparison.Ordinal) => "instance.crashReports",
            _ when lower.StartsWith("screenshots/", StringComparison.Ordinal) => "instance.screenshots",
            _ when lower.StartsWith("server-resource-packs/", StringComparison.Ordinal) => "instance.serverResourcePacks",
            _ => null,
        };

        return new ClassifiedPath(
            category,
            StorageGroup.MinecraftInstance,
            root,
            instance.Name,
            instance.Id,
            instance.Name);
    }

    private static ClassifiedPath? ClassifySharedGameRoot(string fullPath, string root)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var lower = relative.ToLowerInvariant();
        if (lower.StartsWith("instances/", StringComparison.Ordinal))
        {
            return null;
        }

        // 基础版本、库与资产是 Minecraft 的运行文件：只计入磁盘占用，不进入清理列表。
        if (lower.StartsWith("versions/", StringComparison.Ordinal) ||
            lower.StartsWith("libraries/", StringComparison.Ordinal) ||
            lower.StartsWith("assets/", StringComparison.Ordinal))
        {
            return new ClassifiedPath(null, StorageGroup.MinecraftInstance, root, null, null, null);
        }

        return null;
    }

    private static ClassifiedPath? ClassifyEclRoot(string fullPath, string root)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var lower = relative.ToLowerInvariant();
        if (lower.StartsWith("runtimes/", StringComparison.Ordinal) ||
            lower.StartsWith("java/", StringComparison.Ordinal))
        {
            // Java 运行环境不属于本页面的清理范围。
            return null;
        }

        if (lower.StartsWith("instances/", StringComparison.Ordinal))
        {
            // 工作区与数据目录重合时，实例文件已由实例根统计，这里不重复计入。
            return null;
        }

        if (lower.StartsWith("versions/", StringComparison.Ordinal) ||
            lower.StartsWith("libraries/", StringComparison.Ordinal) ||
            lower.StartsWith("assets/", StringComparison.Ordinal))
        {
            return new ClassifiedPath(null, StorageGroup.MinecraftInstance, root, null, null, null);
        }

        if (lower.StartsWith("additions/", StringComparison.Ordinal))
        {
            var segments = lower.Split('/');
            var owner = segments.Length > 1 ? segments[1] : null;
            string? category = lower switch
            {
                _ when lower.Contains("/cache/", StringComparison.Ordinal) => "additions.cache",
                _ when lower.Contains("/tmp/", StringComparison.Ordinal) ||
                       lower.Contains("/temp/", StringComparison.Ordinal) => "additions.temp",
                _ => null,
            };
            return new ClassifiedPath(
                category,
                StorageGroup.Additions,
                Path.Combine(root, "additions"),
                owner,
                null,
                null);
        }

        if (lower.StartsWith("snapshots/", StringComparison.Ordinal) ||
            lower.StartsWith("backups/", StringComparison.Ordinal))
        {
            // 备份是用户数据：只计入磁盘占用，不进入清理列表。
            return new ClassifiedPath(null, StorageGroup.Backups, root, null, null, null);
        }

        if (lower.StartsWith("downloads/", StringComparison.Ordinal))
        {
            return new ClassifiedPath("ecl.downloads", StorageGroup.EclOwn, Path.Combine(root, "downloads"), null, null, null);
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
            return new ClassifiedPath(category, StorageGroup.EclOwn, Path.Combine(root, "cache"), null, null, null);
        }

        if (lower.StartsWith("logs/", StringComparison.Ordinal))
        {
            if (lower.StartsWith("logs/sessions/", StringComparison.Ordinal) ||
                lower.StartsWith("logs/diagnostics/", StringComparison.Ordinal))
            {
                return new ClassifiedPath("ecl.diagnostics", StorageGroup.EclOwn, Path.Combine(root, "logs"), null, null, null);
            }

            if (lower.StartsWith("logs/launch-status/", StringComparison.Ordinal))
            {
                // 最近一次启动状态是主页读取的用户数据，不是日志垃圾。
                return new ClassifiedPath(null, StorageGroup.EclOwn, Path.Combine(root, "logs"), null, null, null);
            }

            var fileName = Path.GetFileName(lower);
            var category = fileName.StartsWith("crash-", StringComparison.Ordinal)
                ? "ecl.crashReports"
                : "ecl.logs";
            return new ClassifiedPath(category, StorageGroup.EclOwn, Path.Combine(root, "logs"), null, null, null);
        }

        if (lower.StartsWith("config/diagnostics/", StringComparison.Ordinal))
        {
            return new ClassifiedPath("ecl.diagnostics", StorageGroup.EclOwn, Path.Combine(root, "config", "diagnostics"), null, null, null);
        }

        if (lower.StartsWith("staging/", StringComparison.Ordinal) ||
            lower.EndsWith(".old", StringComparison.Ordinal))
        {
            return new ClassifiedPath("ecl.updateLeftovers", StorageGroup.EclOwn, root, null, null, null);
        }

        if (lower.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return new ClassifiedPath("ecl.tempFiles", StorageGroup.EclOwn, root, null, null, null);
        }

        // 配置、账户、注入器、扩展数据库等属于基本运行数据：只计入磁盘占用。
        return new ClassifiedPath(null, StorageGroup.EclOwn, root, null, null, null);
    }

    private sealed record ScanRoot(
        string Path,
        Func<string, string, ClassifiedPath?> Classify,
        IReadOnlyList<DirectoryTarget> Directories);

    private sealed record DirectoryTarget(
        string CategoryId,
        StorageGroup Group,
        string Path,
        string AllowedRoot,
        string? InstanceId,
        string? InstanceName,
        string? Owner);

    private sealed record ClassifiedPath(
        string? CategoryId,
        StorageGroup Group,
        string AllowedRoot,
        string? Owner,
        string? InstanceId,
        string? InstanceName);

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
/// 清理白名单的唯一策略（ADR 0022）。只有可安全重建或用户明确要求清理的项目可删除：
/// 缓存、临时文件、历史日志、崩溃报告、截图、服务器资源包缓存、世界存档文件夹。
/// 模组、配置、资源包、光影包、数据包、投影、版本文件、Java 运行时、扩展数据库与备份
/// 永远不进入白名单。新增类别必须同时更新这里与测试。
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
            // 过去的 ECL 日志随时可删；本次进程正在写入的当日日志不是垃圾。
            "ecl.logs" when IsCurrentSessionLog(path, now) =>
                new CleanupDecision(false, "currentSession", StorageRiskLevel.None, "eclLog"),
            "ecl.logs" => new CleanupDecision(true, null, StorageRiskLevel.Caution, "eclLog"),
            "ecl.crashReports" => new CleanupDecision(true, null, StorageRiskLevel.Caution, "eclCrashReport"),
            "ecl.diagnostics" when path.Replace('\\', '/').Contains("/logs/sessions/", StringComparison.OrdinalIgnoreCase) =>
                new CleanupDecision(false, "sessionEvidence", StorageRiskLevel.None, null),
            "ecl.diagnostics" => AgeDecision(age, ShortRetention, "diagnosticTemp"),
            "ecl.imageCache" => AgeDecision(age, LongRetention, "imageCache"),
            "ecl.metadataCache" => AgeDecision(age, LongRetention, "metadataCache"),
            "ecl.tempFiles" => AgeDecision(age, ShortRetention, "tempFile"),
            "instance.logs" => new CleanupDecision(true, null, StorageRiskLevel.Caution, "instanceLog"),
            "instance.crashReports" => new CleanupDecision(true, null, StorageRiskLevel.Caution, "instanceCrashReport"),
            // 世界存档以文件夹为单位整体清理；删除整个世界的风险高于普通缓存。
            "instance.saves" => new CleanupDecision(true, null, StorageRiskLevel.High, "instanceSave"),
            "instance.screenshots" => new CleanupDecision(true, null, StorageRiskLevel.Caution, "instanceScreenshot"),
            "instance.serverResourcePacks" => new CleanupDecision(true, null, StorageRiskLevel.Caution, "serverResourcePack"),
            "instance.runtimeCache" => new CleanupDecision(true, null, StorageRiskLevel.Caution, "runtimeCache"),
            "additions.cache" => AgeDecision(age, LongRetention, "additionCache"),
            "additions.temp" => AgeDecision(age, ShortRetention, "additionTemp"),
            _ => new CleanupDecision(false, "notWhitelisted", StorageRiskLevel.None, null),
        };
    }

    /// <summary>Serilog 按天滚动写入 <c>ecl-YYYYMMDD.log</c>；当日文件属于本次运行会话。</summary>
    private static bool IsCurrentSessionLog(string path, DateTimeOffset now)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith("ecl-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = name[4..];
        return suffix.Length == 8 &&
               DateTime.TryParseExact(
                   suffix,
                   "yyyyMMdd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var date) &&
               date == now.ToLocalTime().Date;
    }

    private static CleanupDecision AgeDecision(TimeSpan age, TimeSpan retention, string rule) =>
        age >= retention
            ? new CleanupDecision(true, null, StorageRiskLevel.Caution, rule)
            : new CleanupDecision(false, "tooRecent", StorageRiskLevel.None, rule);
}

internal sealed record CleanupDecision(
    bool CanClean,
    string? Reason,
    StorageRiskLevel Risk,
    string? RuleId);
