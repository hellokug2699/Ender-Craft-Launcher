// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.World.Nbt;
using Ecl.World.Worlds;

namespace Ecl.World.Dimensions;

/// <summary>
/// 版本化的存储规则。目录约定随 Minecraft 版本变化，不能把某一版本的布局当成所有版本的事实
/// （ADR 0031 §8）。规则只表达“该版本是否预期独立存储”，实际是否存在单独判断。
/// </summary>
public static class DimensionStorageRules
{
    /// <summary>1.13（DataVersion 1519）起实体数据进入独立的 <c>entities/</c> Region 文件。</summary>
    public const int SeparateEntitiesSince = 1519;

    /// <summary>1.14（DataVersion 1901）起引入 <c>poi/</c> 存储。</summary>
    public const int PoiSince = 1901;

    /// <summary>1.16（DataVersion 2566）起数据包可通过 <c>dimensions/</c> 提供自定义维度。</summary>
    public const int CustomDimensionsSince = 2566;

    public static DimensionStorageExpectation Expectation(DimensionStorageCategory category, int dataVersion)
    {
        if (dataVersion <= 0)
        {
            return DimensionStorageExpectation.Unknown;
        }

        return category switch
        {
            DimensionStorageCategory.Region => DimensionStorageExpectation.Expected,
            DimensionStorageCategory.Data => DimensionStorageExpectation.Expected,
            DimensionStorageCategory.Entities => dataVersion >= SeparateEntitiesSince
                ? DimensionStorageExpectation.Expected
                : DimensionStorageExpectation.NotApplicable,
            DimensionStorageCategory.Poi => dataVersion >= PoiSince
                ? DimensionStorageExpectation.Expected
                : DimensionStorageExpectation.NotApplicable,
            _ => DimensionStorageExpectation.Unknown,
        };
    }

    public static bool SupportsCustomDimensions(int dataVersion) =>
        dataVersion <= 0 || dataVersion >= CustomDimensionsSince;
}

/// <summary>
/// 只读的维度发现与存储解析器（ADR 0031 §3）。它不启动 Minecraft、不执行模组代码，
/// 无法确认的布局保留为未确认候选，不猜测、不回退映射到主世界。
/// </summary>
public sealed class DimensionDiscoveryService
{
    private const int MaxDimensionDirectories = 128;
    private const int MaxCustomDepth = 4;

    private static readonly string[] StorageDirectoryNames = ["region", "entities", "poi", "data"];

    public DimensionDiscoveryResult Discover(string worldDirectory)
    {
        if (string.IsNullOrWhiteSpace(worldDirectory))
        {
            return DimensionDiscoveryResult.Empty(error: "世界目录为空。");
        }

        string root;
        try
        {
            root = WorldDimensionKey.NormalizeWorldId(worldDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DimensionDiscoveryResult.Empty(error: $"世界目录无效：{exception.Message}");
        }

        if (!Directory.Exists(root))
        {
            return DimensionDiscoveryResult.Empty(error: "世界目录不存在。");
        }

        var warnings = new List<string>();
        var level = ReadLevelMetadata(root, warnings);
        var dimensions = new List<DimensionDescriptor>();
        var candidates = new List<DimensionStorageCandidate>();
        var registry = new HashSet<string>(level.DimensionIds, StringComparer.OrdinalIgnoreCase);

        AddVanilla(
            dimensions,
            DimensionId.Overworld,
            "主世界",
            RootSelection.Single(root),
            root,
            level,
            registry,
            warnings);

        AddVanilla(
            dimensions,
            DimensionId.Nether,
            "下界",
            RootSelection.LegacyAndDatapack(
                Path.Combine(root, "DIM-1"),
                Path.Combine(root, "dimensions", "minecraft", "the_nether")),
            root,
            level,
            registry,
            warnings);

        AddVanilla(
            dimensions,
            DimensionId.End,
            "末地",
            RootSelection.LegacyAndDatapack(
                Path.Combine(root, "DIM1"),
                Path.Combine(root, "dimensions", "minecraft", "the_end")),
            root,
            level,
            registry,
            warnings);

        var discoveredIds = new HashSet<string>(dimensions.Select(dimension => dimension.Id.Value), StringComparer.OrdinalIgnoreCase);
        DiscoverCustomDimensions(root, level, registry, dimensions, candidates, discoveredIds, warnings);
        DiscoverRegistryOnlyDimensions(root, level, registry, dimensions, discoveredIds, warnings);
        DiscoverUnknownTopLevelCandidates(root, candidates);

        return new DimensionDiscoveryResult(
            dimensions
                .OrderBy(dimension => dimension.Kind switch
                {
                    DimensionKind.Overworld => 0,
                    DimensionKind.Nether => 1,
                    DimensionKind.End => 2,
                    _ => 3,
                })
                .ThenBy(dimension => dimension.Id.Value, StringComparer.Ordinal)
                .ToArray(),
            candidates
                .GroupBy(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            level.DataVersion,
            level.VersionName,
            level.Error);
    }

    private static void AddVanilla(
        List<DimensionDescriptor> dimensions,
        DimensionId id,
        string displayName,
        RootSelection roots,
        string worldRoot,
        LevelMetadata level,
        HashSet<string> registry,
        List<string> warnings)
    {
        var declared = registry.Contains(id.Value);
        if (level.IsReadable && registry.Count > 0 && !declared)
        {
            warnings.Add($"level.dat 的维度注册信息未包含原版维度 {id.Value}；仍按原版存储规则展示。");
        }

        var existing = roots.ExistingDistinct();
        if (existing.Count > 1)
        {
            var storage = existing
                .SelectMany(item => BuildStorage(worldRoot, item.Path, item.Evidence, DimensionResolutionStatus.Conflicted, level.DataVersion))
                .ToArray();
            dimensions.Add(new DimensionDescriptor(
                id,
                displayName,
                id.Kind,
                DimensionResolutionStatus.Conflicted,
                existing[0].Path,
                storage,
                $"同一维度存在多个存储位置：{string.Join("、", existing.Select(item => ToRelative(worldRoot, item.Path)))}；拒绝自动选择。",
                declared));
            return;
        }

        var selected = existing.FirstOrDefault();
        var root = selected.Path ?? roots.PrimaryPath ?? worldRoot;
        var evidence = selected.Evidence;
        var resolution = DimensionResolutionStatus.Resolved;
        string? note = null;
        if (selected.Path is not null &&
            roots.PrimaryPath is not null &&
            !string.Equals(selected.Path, roots.PrimaryPath, StringComparison.OrdinalIgnoreCase) &&
            !declared)
        {
            // 非传统布局且没有注册信息时，只能保留为未确认，而不是默认把它当作该原版维度。
            resolution = DimensionResolutionStatus.Unconfirmed;
            note = "目录布局与注册信息不一致，身份未确认。";
        }

        var storageMappings = BuildStorage(worldRoot, root, evidence, resolution, level.DataVersion);
        if (selected.Path is null)
        {
            note = note ?? "该维度尚无存储目录；不表示维度不存在。";
        }

        dimensions.Add(new DimensionDescriptor(
            id,
            displayName,
            id.Kind,
            resolution,
            root,
            storageMappings,
            note,
            declared));
    }

    private static void DiscoverCustomDimensions(
        string worldRoot,
        LevelMetadata level,
        HashSet<string> registry,
        List<DimensionDescriptor> dimensions,
        List<DimensionStorageCandidate> candidates,
        HashSet<string> discoveredIds,
        List<string> warnings)
    {
        var dimensionsRoot = Path.Combine(worldRoot, "dimensions");
        if (!Directory.Exists(dimensionsRoot))
        {
            return;
        }

        var count = 0;
        foreach (var namespaceDirectory in SafeEnumerateDirectories(dimensionsRoot))
        {
            var @namespace = Path.GetFileName(namespaceDirectory);
            foreach (var (relativePath, dimensionRoot) in EnumerateDimensionRoots(namespaceDirectory, string.Empty, 0))
            {
                if (++count > MaxDimensionDirectories)
                {
                    warnings.Add($"维度目录数量超过 {MaxDimensionDirectories}，其余目录未解析。");
                    return;
                }

                var idText = $"{@namespace}:{relativePath}";
                if (!DimensionId.TryParse(idText, out var id))
                {
                    candidates.Add(BuildCandidate(
                        worldRoot,
                        dimensionRoot,
                        $"目录布局对应的 ID “{idText}” 不符合资源位置规范（大小写或字符集），未映射为维度。"));
                    continue;
                }

                if (id.IsVanilla)
                {
                    warnings.Add($"维度目录 {ToRelative(worldRoot, dimensionRoot)} 使用了原版维度 ID {id.Value}，但不在该维度的标准布局；按未确认候选保留。");
                    candidates.Add(BuildCandidate(worldRoot, dimensionRoot, $"非原版标准布局的原版维度 ID：{id.Value}。"));
                    continue;
                }

                if (!discoveredIds.Add(id.Value))
                {
                    warnings.Add($"维度 {id.Value} 发现多个存储目录；保留首个映射，其余未合并。");
                    continue;
                }

                var declared = registry.Contains(id.Value);
                var versionSupports = DimensionStorageRules.SupportsCustomDimensions(level.DataVersion);
                var resolution = declared && versionSupports
                    ? DimensionResolutionStatus.Resolved
                    : DimensionResolutionStatus.Unconfirmed;
                var note = resolution == DimensionResolutionStatus.Resolved
                    ? "自定义维度，已由 level.dat 注册信息确认。"
                    : declared
                        ? "DataVersion 早于官方自定义维度布局；目录可能来自模组或外部工具，身份未确认。"
                        : "未在 level.dat 的维度注册信息中找到该 ID；ID 仅由目录布局推断，按未确认处理。";
                if (!declared)
                {
                    warnings.Add(
                        $"维度目录 {ToRelative(worldRoot, dimensionRoot)} 未在 level.dat 注册信息中找到（{id.Value}），按未确认处理。");
                }

                dimensions.Add(new DimensionDescriptor(
                    id,
                    id.Value,
                    DimensionKind.Custom,
                    resolution,
                    dimensionRoot,
                    BuildStorage(worldRoot, dimensionRoot, DimensionStorageEvidence.DatapackLayout, resolution, level.DataVersion),
                    note,
                    declared));
            }
        }
    }

    private static void DiscoverRegistryOnlyDimensions(
        string worldRoot,
        LevelMetadata level,
        HashSet<string> registry,
        List<DimensionDescriptor> dimensions,
        HashSet<string> discoveredIds,
        List<string> warnings)
    {
        foreach (var idText in registry)
        {
            if (DimensionId.IsKnownVanilla(idText) || !DimensionId.TryParse(idText, out var id) || !discoveredIds.Add(id.Value))
            {
                continue;
            }

            var expectedRoot = Path.Combine(worldRoot, "dimensions", id.Namespace, id.Path.Replace('/', Path.DirectorySeparatorChar));
            var storage = BuildStorage(
                worldRoot,
                expectedRoot,
                DimensionStorageEvidence.LevelDatRegistry,
                DimensionResolutionStatus.Resolved,
                level.DataVersion);
            dimensions.Add(new DimensionDescriptor(
                id,
                id.Value,
                DimensionKind.Custom,
                DimensionResolutionStatus.Resolved,
                expectedRoot,
                storage,
                "level.dat 声明了该维度，但磁盘上没有可发现的存储目录。",
                DeclaredByLevelDat: true));
        }
    }

    private static void DiscoverUnknownTopLevelCandidates(
        string worldRoot,
        List<DimensionStorageCandidate> candidates)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "region", "entities", "poi", "dimensions", "datapacks", "data", "playerdata",
            "stats", "advancements", "DIM-1", "DIM1",
        };

        foreach (var directory in SafeEnumerateDirectories(worldRoot))
        {
            var name = Path.GetFileName(directory);
            if (known.Contains(name) || !HasStorage(directory))
            {
                continue;
            }

            candidates.Add(BuildCandidate(
                worldRoot,
                directory,
                "未识别的顶部维度存储目录；无法确认它的 Dimension ID，未映射到任何维度。"));
        }
    }

    private static DimensionStorageCandidate BuildCandidate(string worldRoot, string root, string reason) =>
        new(
            ToRelative(worldRoot, root),
            root,
            BuildStorage(worldRoot, root, DimensionStorageEvidence.DirectoryOnly, DimensionResolutionStatus.Unconfirmed, 0)
                .Select(mapping => mapping.Status == DimensionStorageStatus.Available
                    ? mapping with
                    {
                        Status = DimensionStorageStatus.Unconfirmed,
                        Note = "候选目录，维度身份未确认。",
                    }
                    : mapping)
                .ToArray(),
            reason);

    private static IReadOnlyList<DimensionStorageMapping> BuildStorage(
        string worldRoot,
        string dimensionRoot,
        DimensionStorageEvidence evidence,
        DimensionResolutionStatus resolution,
        int dataVersion)
    {
        var mappings = new List<DimensionStorageMapping>(3);
        foreach (var category in Enum.GetValues<DimensionStorageCategory>())
        {
            var directoryName = directoryNameOf(category);
            var path = Path.Combine(dimensionRoot, directoryName);
            var expectation = DimensionStorageRules.Expectation(category, dataVersion);
            var relative = ToRelative(worldRoot, path);
            if (!Directory.Exists(path))
            {
                mappings.Add(new DimensionStorageMapping(
                    category,
                    DimensionStorageStatus.Missing,
                    expectation,
                    DimensionStorageEvidence.None,
                    null,
                    relative,
                    expectation == DimensionStorageExpectation.NotApplicable
                        ? "当前 Minecraft 版本不使用该独立存储类别。"
                        : null));
                continue;
            }

            if (resolution == DimensionResolutionStatus.Conflicted)
            {
                mappings.Add(new DimensionStorageMapping(
                    category,
                    DimensionStorageStatus.Conflicted,
                    expectation,
                    evidence,
                    path,
                    relative,
                    "维度存在多个候选存储位置，拒绝自动读写。"));
                continue;
            }

            if (IsReparsePoint(path))
            {
                mappings.Add(new DimensionStorageMapping(
                    category,
                    DimensionStorageStatus.Unreadable,
                    expectation,
                    evidence,
                    path,
                    relative,
                    "存储目录是重解析点，拒绝跟随。"));
                continue;
            }

            var status = expectation == DimensionStorageExpectation.NotApplicable
                ? DimensionStorageStatus.Unconfirmed
                : DimensionStorageStatus.Available;
            mappings.Add(new DimensionStorageMapping(
                category,
                status,
                expectation,
                evidence,
                path,
                relative,
                status == DimensionStorageStatus.Unconfirmed
                    ? "当前 DataVersion 不预期该独立存储类别；按未确认处理，只读。"
                    : null));
        }

        return mappings;

        static string directoryNameOf(DimensionStorageCategory category) => category switch
        {
            DimensionStorageCategory.Region => "region",
            DimensionStorageCategory.Entities => "entities",
            DimensionStorageCategory.Poi => "poi",
            DimensionStorageCategory.Data => "data",
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
    }

    private static IEnumerable<(string RelativePath, string Directory)> EnumerateDimensionRoots(
        string directory,
        string relativePath,
        int depth)
    {
        foreach (var child in SafeEnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (StorageDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var nextRelative = string.IsNullOrEmpty(relativePath) ? name : $"{relativePath}/{name}";
            if (HasStorage(child))
            {
                yield return (nextRelative, child);
                continue;
            }

            if (depth + 1 >= MaxCustomDepth)
            {
                continue;
            }

            foreach (var nested in EnumerateDimensionRoots(child, nextRelative, depth + 1))
            {
                yield return nested;
            }
        }
    }

    private static bool HasStorage(string directory) =>
        StorageDirectoryNames.Any(name => Directory.Exists(Path.Combine(directory, name)));

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Take(MaxDimensionDirectories).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ToRelative(string worldRoot, string path)
    {
        try
        {
            return Path.GetRelativePath(worldRoot, path).Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    private static LevelMetadata ReadLevelMetadata(string worldRoot, List<string> warnings)
    {
        var levelDat = Path.Combine(worldRoot, "level.dat");
        if (!File.Exists(levelDat))
        {
            warnings.Add("缺少 level.dat；维度 ID 只能由目录布局推断。");
            return new LevelMetadata(0, null, [], "level.dat 不存在。");
        }

        try
        {
            var root = NbtFile.ReadFile(levelDat);
            var data = root.Child("Data") ?? root;
            var dataVersion = data.Child("DataVersion")?.GetInt() ?? 0;
            var versionName = data.Child("Version")?.Child("Name")?.GetString();
            var ids = new List<string>();
            var dimensions = data.Child("WorldGenSettings")?.Child("dimensions");
            if (dimensions is not null)
            {
                foreach (var child in dimensions.Children)
                {
                    if (!string.IsNullOrWhiteSpace(child.Name))
                    {
                        ids.Add(child.Name);
                    }
                }
            }

            return new LevelMetadata(dataVersion, versionName, ids, null);
        }
        catch (Exception exception) when (exception is NbtFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            warnings.Add($"level.dat 读取失败：{exception.Message}");
            return new LevelMetadata(0, null, [], $"level.dat 读取失败：{exception.Message}");
        }
    }

    private sealed record LevelMetadata(
        int DataVersion,
        string? VersionName,
        IReadOnlyList<string> DimensionIds,
        string? Error)
    {
        public bool IsReadable => Error is null;
    }

    private sealed record RootSelection(string? PrimaryPath, string? AlternativePath)
    {
        public static RootSelection Single(string path) => new(path, null);

        public static RootSelection LegacyAndDatapack(string legacy, string datapack) => new(legacy, datapack);

        public IReadOnlyList<(string Path, DimensionStorageEvidence Evidence)> ExistingDistinct()
        {
            var result = new List<(string, DimensionStorageEvidence)>(2);
            if (PrimaryPath is not null && Directory.Exists(PrimaryPath))
            {
                result.Add((PrimaryPath, DimensionStorageEvidence.VanillaLayout));
            }

            if (AlternativePath is not null && Directory.Exists(AlternativePath))
            {
                result.Add((AlternativePath, DimensionStorageEvidence.DatapackLayout));
            }

            return result;
        }
    }
}
