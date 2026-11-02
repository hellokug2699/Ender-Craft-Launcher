// SPDX-License-Identifier: GPL-3.0-or-later

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

    public DimensionInfo? FindDimension(string id) =>
        Dimensions.FirstOrDefault(dimension => string.Equals(dimension.Id, id, StringComparison.Ordinal));
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

    public static IReadOnlyList<DimensionInfo> DiscoverDimensions(string worldDirectory)
    {
        var result = new List<DimensionInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string id, string displayName, DimensionKind kind, string root)
        {
            if (!seen.Add(id))
            {
                return;
            }

            var region = Path.Combine(root, "region");
            var entities = Path.Combine(root, "entities");
            var poi = Path.Combine(root, "poi");
            if (!Directory.Exists(region) && !Directory.Exists(entities) && !Directory.Exists(poi))
            {
                return;
            }

            result.Add(new DimensionInfo(
                id,
                displayName,
                kind,
                root,
                Directory.Exists(region) ? region : null,
                Directory.Exists(entities) ? entities : null,
                Directory.Exists(poi) ? poi : null));
        }

        Add("minecraft:overworld", "主世界", DimensionKind.Overworld, worldDirectory);
        Add("minecraft:the_nether", "下界", DimensionKind.Nether, Path.Combine(worldDirectory, "DIM-1"));
        Add("minecraft:the_end", "末地", DimensionKind.End, Path.Combine(worldDirectory, "DIM1"));

        var dimensionsRoot = Path.Combine(worldDirectory, "dimensions");
        if (Directory.Exists(dimensionsRoot))
        {
            foreach (var namespaceDirectory in SafeEnumerateDirectories(dimensionsRoot))
            {
                var namespaceName = Path.GetFileName(namespaceDirectory);
                foreach (var dimensionDirectory in SafeEnumerateDirectories(namespaceDirectory))
                {
                    var id = $"{namespaceName}:{Path.GetFileName(dimensionDirectory)}";
                    Add(id, id, DimensionKind.Custom, dimensionDirectory);
                }
            }
        }

        return result
            .OrderBy(dimension => dimension.Kind switch
            {
                DimensionKind.Overworld => 0,
                DimensionKind.Nether => 1,
                DimensionKind.End => 2,
                _ => 3,
            })
            .ThenBy(dimension => dimension.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Take(256).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
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
