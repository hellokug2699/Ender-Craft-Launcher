// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.World.Worlds;

namespace Ecl.World.Dimensions;

/// <summary>维度的独立存储类别。缺失某一类别不等于维度不存在（ADR 0031 §2）。</summary>
public enum DimensionStorageCategory
{
    Region,
    Entities,
    Poi,

    /// <summary>维度级 <c>data/</c>（例如 raids.dat、模组数据附件）；不属于区块存储，但同样是维度数据。</summary>
    Data,
}

/// <summary>某一存储类别的映射状态；不同类别独立表达。</summary>
public enum DimensionStorageStatus
{
    /// <summary>目录存在且与版本规则/元数据一致，可读取。</summary>
    Available,

    /// <summary>目录存在，但无法确认其逻辑身份或与当前版本规则不一致；只读并保持不确定。</summary>
    Unconfirmed,

    /// <summary>该类别没有可发现的存储目录。</summary>
    Missing,

    /// <summary>同一维度映射到多个目录，无法可靠选择；拒绝自动读写。</summary>
    Conflicted,

    /// <summary>目录存在但不可访问（权限/重解析点/IO 错误）。</summary>
    Unreadable,
}

/// <summary>版本规则对该存储类别的期望，与“实际是否存在”分开记录。</summary>
public enum DimensionStorageExpectation
{
    Expected,
    NotApplicable,
    Unknown,
}

/// <summary>映射依据，用于解释“为什么认为这个目录属于这个维度”。</summary>
public enum DimensionStorageEvidence
{
    VanillaLayout,
    DatapackLayout,
    LevelDatRegistry,
    DirectoryOnly,
    None,
}

/// <summary>一个维度的某一存储类别到磁盘位置的映射，包含状态与依据。</summary>
public sealed record DimensionStorageMapping(
    DimensionStorageCategory Category,
    DimensionStorageStatus Status,
    DimensionStorageExpectation Expectation,
    DimensionStorageEvidence Evidence,
    string? AbsolutePath,
    string? RelativePath,
    string? Note)
{
    public bool IsReadable => Status is DimensionStorageStatus.Available or DimensionStorageStatus.Unconfirmed &&
                              AbsolutePath is not null;
}

public enum DimensionResolutionStatus
{
    /// <summary>身份与存储映射可靠确认。</summary>
    Resolved,

    /// <summary>只能由目录布局推断，缺少元数据确认；可只读浏览基础结构，但不冒充已识别维度。</summary>
    Unconfirmed,

    /// <summary>同一 ID 映射到冲突的存储位置，或注册信息互相矛盾。</summary>
    Conflicted,
}

/// <summary>已识别的维度描述符：逻辑身份、支撑状态与各存储类别映射。</summary>
public sealed record DimensionDescriptor(
    DimensionId Id,
    string DisplayName,
    DimensionKind Kind,
    DimensionResolutionStatus Resolution,
    string RootDirectory,
    IReadOnlyList<DimensionStorageMapping> Storage,
    string? Note,
    bool DeclaredByLevelDat)
{
    public bool IsCustom => Kind is DimensionKind.Custom or DimensionKind.Unknown;

    public bool HasUsableStorage =>
        Resolution != DimensionResolutionStatus.Conflicted && Storage.Any(mapping => mapping.IsReadable);

    public DimensionStorageMapping? Find(DimensionStorageCategory category) =>
        Storage.FirstOrDefault(mapping => mapping.Category == category);

    public DimensionStorageMapping? FindReadable(DimensionStorageCategory category) =>
        Storage.FirstOrDefault(mapping => mapping.Category == category && mapping.IsReadable);
}

/// <summary>
/// 能发现维度存储目录、但无法确认其逻辑身份时保留的候选。候选不会伪装成已识别维度，
/// 也不会参与任何写入或自动修复（ADR 0031 §8）。
/// </summary>
public sealed record DimensionStorageCandidate(
    string CandidateKey,
    string RootDirectory,
    IReadOnlyList<DimensionStorageMapping> Storage,
    string Reason);

public sealed record DimensionDiscoveryResult(
    IReadOnlyList<DimensionDescriptor> Dimensions,
    IReadOnlyList<DimensionStorageCandidate> Candidates,
    IReadOnlyList<string> Warnings,
    int DataVersion,
    string? VersionName,
    string? LevelDatError)
{
    public static DimensionDiscoveryResult Empty(int dataVersion = 0, string? error = null) =>
        new([], [], error is null ? [] : [error], dataVersion, null, error);

    public DimensionDescriptor? Find(DimensionId id) =>
        Dimensions.FirstOrDefault(dimension => dimension.Id == id);

    public DimensionDescriptor? Find(string id) =>
        DimensionId.TryParse(id, out var parsed) ? Find(parsed) : null;

    public bool HasUnconfirmedContent =>
        Dimensions.Any(dimension => dimension.Resolution != DimensionResolutionStatus.Resolved) ||
        Candidates.Count > 0;
}

/// <summary>
/// 稳定的世界 + 维度身份，用于跨模块对象标识、缓存键与异步任务标签。
/// 相同坐标在不同维度、不同世界必须是不同对象（ADR 0031 §4）。
/// </summary>
public readonly struct WorldDimensionKey : IEquatable<WorldDimensionKey>
{
    private readonly string _worldId;

    public WorldDimensionKey(string worldDirectory, DimensionId dimension)
    {
        if (string.IsNullOrWhiteSpace(worldDirectory))
        {
            throw new ArgumentException("世界目录不能为空。", nameof(worldDirectory));
        }

        _worldId = NormalizeWorldId(worldDirectory);
        Dimension = dimension;
    }

    /// <summary>规范化的世界目录；它只在本地用于稳定标识，不写入诊断或日志。</summary>
    public string WorldId => _worldId;

    public DimensionId Dimension { get; }

    public static string NormalizeWorldId(string worldDirectory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(worldDirectory));

    public bool Equals(WorldDimensionKey other) =>
        string.Equals(_worldId, other._worldId, StringComparison.OrdinalIgnoreCase) &&
        Dimension.Equals(other.Dimension);

    public override bool Equals(object? obj) => obj is WorldDimensionKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(_worldId),
        Dimension);

    public static bool operator ==(WorldDimensionKey left, WorldDimensionKey right) => left.Equals(right);

    public static bool operator !=(WorldDimensionKey left, WorldDimensionKey right) => !left.Equals(right);

    public override string ToString() => $"{_worldId}#{Dimension.Value}";
}

/// <summary>Region 级对象身份：World + Dimension + Region 坐标。</summary>
public readonly record struct RegionIdentity(WorldDimensionKey World, int X, int Z);

/// <summary>Chunk 级对象身份：World + Dimension + Chunk 坐标。</summary>
public readonly record struct ChunkIdentity(WorldDimensionKey World, int X, int Z)
{
    public RegionIdentity Region => new(World, X >> 5, Z >> 5);
}

/// <summary>Block 级对象身份：World + Dimension + Block 坐标。</summary>
public readonly record struct BlockIdentity(WorldDimensionKey World, int X, int Y, int Z)
{
    public ChunkIdentity Chunk => new(World, X >> 4, Z >> 4);
}
