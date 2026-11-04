// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.World.Dimensions;
using Ecl.World.Nbt;

namespace Ecl.World.Worlds;

public enum DimensionKind
{
    Overworld,
    Nether,
    End,
    Custom,
    Unknown,
}

/// <summary>
/// 维度在工作台中的兼容投影。逻辑身份、存储路径与解析状态分开保存：
/// <see cref="Id"/> 是 Dimension ID，目录字段只是某一存储类别的映射结果（ADR 0031 §2）。
/// </summary>
public sealed record DimensionInfo(
    string Id,
    string DisplayName,
    DimensionKind Kind,
    string RootDirectory,
    string? RegionDirectory,
    string? EntitiesDirectory,
    string? PoiDirectory)
{
    public bool IsCustom => Kind is DimensionKind.Custom or DimensionKind.Unknown;

    public bool HasStorage => RegionDirectory is not null || EntitiesDirectory is not null || PoiDirectory is not null;

    /// <summary>身份与存储映射的解析状态；只有 Resolved 才视为已确认。</summary>
    public DimensionResolutionStatus Resolution { get; init; } = DimensionResolutionStatus.Resolved;

    public bool IsConfirmed => Resolution == DimensionResolutionStatus.Resolved;

    /// <summary>Region/Entities/POI 各自的映射状态与依据。</summary>
    public IReadOnlyList<DimensionStorageMapping> StorageMappings { get; init; } = [];

    public string? SupportNote { get; init; }

    public bool DeclaredByLevelDat { get; init; }

    /// <summary>可直接显示在维度选择器中的解析状态摘要。</summary>
    public string StorageStatus => BuildStorageStatus();

    public bool CanRead => IsConfirmed && HasStorage;

    private string BuildStorageStatus()
    {
        if (Resolution == DimensionResolutionStatus.Conflicted)
        {
            return "存储位置冲突";
        }

        if (!HasStorage)
        {
            return IsConfirmed ? "尚无存储数据" : "未确认";
        }

        var parts = new List<string>();
        foreach (var mapping in StorageMappings)
        {
            if (mapping.Category is not (DimensionStorageCategory.Region
                or DimensionStorageCategory.Entities
                or DimensionStorageCategory.Poi
                or DimensionStorageCategory.Data))
            {
                continue;
            }

            parts.Add($"{mapping.Category} {StatusText(mapping.Status)}");
        }

        var prefix = Resolution == DimensionResolutionStatus.Unconfirmed ? "未确认" : "已确认";
        return parts.Count == 0 ? prefix : $"{prefix} · {string.Join(" · ", parts)}";
    }

    private static string StatusText(DimensionStorageStatus status) => status switch
    {
        DimensionStorageStatus.Available => "可用",
        DimensionStorageStatus.Unconfirmed => "未确认",
        DimensionStorageStatus.Missing => "缺失",
        DimensionStorageStatus.Conflicted => "冲突",
        DimensionStorageStatus.Unreadable => "不可读",
        _ => status.ToString(),
    };
}

public sealed record WorldLevelInfo
{
    public string LevelName { get; init; } = string.Empty;

    public int DataVersion { get; init; }

    public string? VersionName { get; init; }

    public long Seed { get; init; }

    public int GameType { get; init; }

    public int Difficulty { get; init; }

    public bool Hardcore { get; init; }

    public long DayTime { get; init; }

    public DateTimeOffset? LastPlayed { get; init; }

    public string? Error { get; init; }

    public bool IsReadable => Error is null;
}

public sealed record WorldSave(
    string Name,
    string DirectoryPath,
    string LevelDatPath,
    WorldLevelInfo Level,
    IReadOnlyList<DimensionInfo> Dimensions,
    string? IconPath,
    long? SizeBytes = null,
    string? SizeError = null)
{
    public string FolderName => Path.GetFileName(DirectoryPath);

    /// <summary>维度发现与存储解析的完整结果，包含候选、警告与解析依据。</summary>
    public DimensionDiscoveryResult? Discovery { get; init; }

    public DimensionInfo? FindDimension(string id) =>
        DimensionId.TryParse(id, out var dimensionId)
            ? Dimensions.FirstOrDefault(dimension => string.Equals(dimension.Id, dimensionId.Value, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>只返回身份已确认且存在可读存储的维度；不会回退到其他维度。</summary>
    public DimensionInfo? FindReadableDimension(string id) =>
        FindDimension(id) is { CanRead: true } dimension ? dimension : null;
}

/// <summary>
/// 只读读取 level.dat 与维度存储。字段缺失时保留 null/默认值并记录错误，
/// 不把不确定内容解释成主世界或原版元数据。
/// </summary>
public static class WorldLevelReader
{
    public static WorldLevelInfo Read(string levelDatPath)
    {
        if (!File.Exists(levelDatPath))
        {
            return new WorldLevelInfo { Error = "level.dat 不存在。" };
        }

        try
        {
            var root = NbtFile.ReadFile(levelDatPath);
            var data = root.Child("Data") ?? root;
            var version = data.Child("Version");
            var lastPlayed = data.Child("LastPlayed")?.GetLong();
            DateTimeOffset? lastPlayedTime = lastPlayed is > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(lastPlayed.Value)
                : null;
            return new WorldLevelInfo
            {
                LevelName = data.Child("LevelName")?.GetString() ?? string.Empty,
                DataVersion = data.Child("DataVersion")?.GetInt() ?? 0,
                VersionName = version?.Child("Name")?.GetString(),
                Seed = data.Child("Seed")?.GetLong() ?? 0,
                GameType = data.Child("GameType")?.GetInt() ?? 0,
                Difficulty = data.Child("Difficulty")?.GetInt() ?? 0,
                Hardcore = data.Child("hardcore")?.GetByte() == 1,
                DayTime = data.Child("DayTime")?.GetLong() ?? 0,
                LastPlayed = lastPlayedTime,
            };
        }
        catch (Exception exception) when (exception is NbtFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new WorldLevelInfo { Error = $"level.dat 读取失败：{exception.Message}" };
        }
    }

    /// <summary>
    /// 兼容入口：返回维度投影。实际解析由 <see cref="DimensionDiscoveryService"/> 完成，
    /// 逻辑 ID 与磁盘路径、Region/Entities/POI 映射状态均独立表达。
    /// </summary>
    public static IReadOnlyList<DimensionInfo> DiscoverDimensions(string worldDirectory) =>
        new DimensionDiscoveryService()
            .Discover(worldDirectory)
            .Dimensions
            .Select(DimensionProjection.ToDimensionInfo)
            .ToArray();
}

public sealed record WorldSizeResult(long? Bytes, string? Error)
{
    public bool IsKnown => Bytes.HasValue;
}

public static class WorldSizeCalculator
{
    public static Task<WorldSizeResult> CalculateAsync(string directory, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                if (!Directory.Exists(directory))
                {
                    return new WorldSizeResult(null, "目录不存在。");
                }

                long total = 0;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(
                                 directory,
                                 "*",
                                 new EnumerationOptions
                                 {
                                     RecurseSubdirectories = true,
                                     IgnoreInaccessible = true,
                                     AttributesToSkip = FileAttributes.ReparsePoint,
                                     MaxRecursionDepth = 64,
                                 }))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            total += new FileInfo(file).Length;
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            // 单个文件不可访问时保留已统计值。
                        }
                    }

                    return new WorldSizeResult(total, null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return new WorldSizeResult(total, $"大小统计不完整：{exception.Message}");
                }
            },
            cancellationToken);
}
