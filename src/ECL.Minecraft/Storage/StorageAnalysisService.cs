// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Storage;
using Ecl.Minecraft.Instances;

namespace Ecl.Minecraft.Storage;

public enum StorageUsageStatus
{
    Scanned,
    Estimated,
    Unavailable,
    NotScanned,
    Overlapping,
}

public sealed record StorageUsage(
    string Id,
    string Label,
    string? Path,
    long? Bytes,
    int FileCount,
    StorageUsageStatus Status,
    string? Detail,
    bool IsCleanable,
    string? Error)
{
    public bool HasBytes => Bytes.HasValue;
}

public sealed record StorageScanOptions
{
    public bool IncludeExternalSources { get; init; } = true;

    public bool IncludeInstanceSources { get; init; } = true;
}

public sealed record StorageScanProgress(string Stage, string? Detail, double? Fraction);

public sealed record StorageAnalysisResult(
    IReadOnlyList<StorageUsage> Usages,
    long KnownBytes,
    int UnavailableCount,
    int InstanceCount,
    DateTimeOffset ScannedAt,
    IReadOnlyList<string> Warnings)
{
    public static StorageAnalysisResult Empty => new([], 0, 0, 0, DateTimeOffset.UtcNow, []);
}

/// <summary>
/// 存储空间统计。目录遍历全部异步、可取消，区分已扫描/估算/不可访问；
/// 不把未知目录显示为 0，也不把逻辑大小冒充实际磁盘占用。
/// </summary>
public sealed class StorageAnalysisService(
    IAppPaths paths,
    InstanceSourceService sources)
{
    private const long MaxFilesPerRoot = 250_000;

    public async Task<StorageAnalysisResult> AnalyzeAsync(
        StorageScanOptions? options = null,
        IProgress<StorageScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StorageScanOptions();
        var usages = new List<StorageUsage>();
        var warnings = new List<string>();
        var measuredRoots = new List<string>();

        await AddAsync(
                "ecl-data",
                "ECL 配置与日志",
                [paths.ConfigDirectory, paths.AccountsDirectory, paths.LogsDirectory, paths.InjectorDirectory],
                isCleanable: false,
                "配置、账户、日志与注入组件。",
                usages,
                measuredRoots,
                warnings,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        await AddAsync(
                "downloads",
                "下载目录",
                [paths.DownloadsDirectory],
                isCleanable: true,
                "已完成的下载缓存；可重新下载。",
                usages,
                measuredRoots,
                warnings,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        await AddAsync(
                "cache",
                "缓存与临时产物",
                [paths.CacheDirectory],
                isCleanable: true,
                "缩略图、索引、解压临时目录与下载 .part 产物。",
                usages,
                measuredRoots,
                warnings,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        await AddAsync(
                "runtimes",
                "Java Runtime",
                [paths.RuntimesDirectory],
                isCleanable: false,
                "ECL 管理的 Java 运行时；由 Java 管理页维护。",
                usages,
                measuredRoots,
                warnings,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        await AddAsync(
                "workspace",
                "Minecraft 工作区",
                [paths.GameRoot],
                isCleanable: false,
                "实例、版本、库文件与资源；清理实例请使用实例删除流程。",
                usages,
                measuredRoots,
                warnings,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        var instanceCount = 0;
        if (options.IncludeInstanceSources)
        {
            var catalog = await sources.ScanAllAsync(cancellationToken).ConfigureAwait(false);
            instanceCount = catalog.InstanceCount;
            warnings.AddRange(catalog.Warnings);
            foreach (var group in catalog.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = "source:" + group.Source.Id;
                if (PathsOverlap(group.Source.Path, paths.Root) || PathsOverlap(group.Source.Path, paths.GameRoot))
                {
                    usages.Add(new StorageUsage(
                        id,
                        $"实例来源 · {group.Source.Name}",
                        group.Source.Path,
                        null,
                        group.Instances.Count,
                        StorageUsageStatus.Overlapping,
                        "该来源与 ECL 数据目录或工作区重叠，已从总计中排除以避免重复计数。",
                        false,
                        null));
                    continue;
                }

                if (!options.IncludeExternalSources)
                {
                    continue;
                }

                var measured = await MeasureAsync(group.Source.Path, cancellationToken).ConfigureAwait(false);
                usages.Add(new StorageUsage(
                    id,
                    $"实例来源 · {group.Source.Name}",
                    group.Source.Path,
                    measured.Bytes,
                    measured.Files,
                    measured.Status,
                    $"{group.Instances.Count} 个实例（{group.Structure}）",
                    false,
                    measured.Error));
            }
        }

        var known = usages.Where(usage => usage.Bytes.HasValue).Sum(usage => usage.Bytes!.Value);
        var unavailable = usages.Count(usage =>
            usage.Status is StorageUsageStatus.Unavailable or StorageUsageStatus.NotScanned);
        var allWarnings = warnings.Concat(usages.Where(usage => usage.Error is not null).Select(usage => $"{usage.Label}：{usage.Error}")).ToArray();
        progress?.Report(new StorageScanProgress("complete", null, 1));
        return new StorageAnalysisResult(
            usages,
            known,
            unavailable,
            instanceCount,
            DateTimeOffset.UtcNow,
            allWarnings);
    }

    /// <summary>
    /// 测量单个实例游戏目录的占用，供实例基本信息页的存储卡使用。
    /// 与存储中心共用同一枚举、状态语义与统计上限：未知/不可访问不当作 0 字节。
    /// </summary>
    public async Task<StorageUsage> MeasureInstanceAsync(
        GameInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var directory = InstancePaths.GetGameDirectory(paths, instance);
        var measured = await MeasureAsync(directory, cancellationToken).ConfigureAwait(false);
        var status = measured.Bytes is null
            ? StorageUsageStatus.Unavailable
            : measured.Status;

        return new StorageUsage(
            "instance:" + instance.Id,
            instance.Name,
            directory,
            measured.Bytes,
            measured.Files,
            status,
            "当前实例可识别文件；不可访问或尚未扫描的内容不计入。",
            IsCleanable: false,
            measured.Error);
    }

    private async Task AddAsync(
        string id,
        string label,
        IReadOnlyList<string> roots,
        bool isCleanable,
        string detail,
        List<StorageUsage> usages,
        List<string> measuredRoots,
        List<string> warnings,
        IProgress<StorageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        long total = 0;
        var files = 0;
        var anyKnown = false;
        var status = StorageUsageStatus.NotScanned;
        string? error = null;
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (measuredRoots.Any(measured => PathsOverlap(root, measured)))
            {
                continue;
            }

            progress?.Report(new StorageScanProgress("scanning", root, null));
            var measured = await MeasureAsync(root, cancellationToken).ConfigureAwait(false);
            if (measured.Bytes is { } bytes)
            {
                total += bytes;
                anyKnown = true;
                files += measured.Files;
                status = measured.Status;
            }

            error ??= measured.Error;
            measuredRoots.Add(root);
        }

        if (!anyKnown && status == StorageUsageStatus.NotScanned)
        {
            status = StorageUsageStatus.Unavailable;
        }

        if (error is not null)
        {
            warnings.Add($"{label}：{error}");
        }

        usages.Add(new StorageUsage(
            id,
            label,
            roots.FirstOrDefault(),
            anyKnown ? total : null,
            files,
            status,
            detail,
            isCleanable,
            error));
    }

    private static Task<Measurement> MeasureAsync(string root, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                if (!Directory.Exists(root))
                {
                    return new Measurement(null, 0, StorageUsageStatus.NotScanned, "目录不存在。");
                }

                long bytes = 0;
                var files = 0;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(
                                 root,
                                 "*",
                                 new EnumerationOptions
                                 {
                                     RecurseSubdirectories = true,
                                     IgnoreInaccessible = true,
                                     AttributesToSkip = FileAttributes.ReparsePoint,
                                     MaxRecursionDepth = 16,
                                 }))
                    {
                        if (files >= MaxFilesPerRoot)
                        {
                            return new Measurement(bytes, files, StorageUsageStatus.Estimated, "文件数量超过统计上限，结果为估算值。");
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        files++;
                        try
                        {
                            bytes += new FileInfo(file).Length;
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            // 单个文件不可访问时保留已统计值。
                        }
                    }

                    return new Measurement(bytes, files, StorageUsageStatus.Scanned, null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return new Measurement(bytes, files, StorageUsageStatus.Estimated, $"统计不完整：{exception.Message}");
                }
            },
            cancellationToken);

    private static bool PathsOverlap(string left, string right) =>
        StoragePathSafety.IsContainedIn(left, right) || StoragePathSafety.IsContainedIn(right, left);

    private sealed record Measurement(long? Bytes, int Files, StorageUsageStatus Status, string? Error);
}

internal static class StoragePathSafety
{
    public static bool IsContainedIn(string root, string candidate)
    {
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
                   normalizedCandidate.StartsWith(
                       normalizedRoot + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
