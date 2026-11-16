// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Storage.Discovery;

/// <summary>来源根目录的结构（F-02 §4.2）。与旧 <c>InstanceSourceStructure</c> 并存，语义更严格。</summary>
public enum InstanceRootStructure
{
    Unknown,

    /// <summary>其他 ECL 工作区的 <c>instances/&lt;id&gt;/instance.json</c>。</summary>
    EclManaged,

    /// <summary><c>versions/&lt;id&gt;/&lt;id&gt;.json</c>。</summary>
    Versioned,

    /// <summary>根目录本身就是一个实例（含隔离标记）。</summary>
    SingleInstance,
}

/// <summary>发现条目的类别（F-02 §4.3）。</summary>
public enum DiscoveryEntryKind
{
    ExternalVersioned,
    ExternalSingle,
    ManagedForeign,
    SharedRootCandidate,
}

/// <summary>发现条目的状态。不可访问/未发现不等于删除。</summary>
public enum DiscoveryEntryStatus
{
    Active,
    Missing,
    SourceMissing,
    Unbound,
    PotentialDuplicate,
    IdentityConflict,
    AdoptionRequired,
    Invalid,
}

/// <summary>
/// 一次扫描识别到的来源候选（F-02 §4.3）。它是 ECL 的本地派生登记，
/// 不是 <c>GameInstance</c>，不得被当作可启动实例。
/// </summary>
public sealed record DiscoveryEntry
{
    public required string DiscoveryId { get; init; }

    public required string RootId { get; init; }

    public required string RelativePath { get; init; }

    public required string GameDirectory { get; init; }

    public string? CanonicalGameDirectory { get; init; }

    public string? PathIdentityKey { get; init; }

    public DiscoveryEntryKind Kind { get; init; }

    public DiscoveryEntryStatus Status { get; init; }

    public required string DisplayName { get; init; }

    public string? GameVersion { get; init; }

    public string Loader { get; init; } = "unknown";

    public string? VersionId { get; init; }

    public string? MarkerInstanceId { get; init; }

    public string? DuplicateGroupId { get; init; }

    public string? DuplicateOfDiscoveryId { get; init; }

    /// <summary>用户显式重复处理结果，例如 <c>keep-separate</c>；扫描时保留。</summary>
    public string? DuplicateResolution { get; init; }

    public string? ImportedInstanceId { get; init; }

    public string? ProblemCode { get; init; }

    public string? ProblemDetail { get; init; }

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastSeenAt { get; init; }

    public string? LastScanId { get; init; }
}

/// <summary>每个根的扫描覆盖摘要，用于回答“扫描了哪些根、是否完整”。</summary>
public sealed record DiscoveryRootState(
    string RootId,
    string? PathIdentityKey,
    string? LastScanId,
    bool IsComplete,
    DateTimeOffset? LastScanAt,
    string? LastErrorCode);

/// <summary><c>config/instance-engine/discovery-catalog.json</c>（schema v1）。</summary>
public sealed record DiscoveryCatalog
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset? GeneratedAt { get; init; }

    public IReadOnlyList<DiscoveryRootState> Roots { get; init; } = [];

    public IReadOnlyList<DiscoveryEntry> Entries { get; init; } = [];

    public static DiscoveryCatalog Empty { get; } = new();
}

/// <summary>扫描请求。null 根列表表示所有启用的根。</summary>
public sealed record DiscoveryScanRequest
{
    public IReadOnlyList<string>? RootIds { get; init; }

    public bool IncludeManagedForeign { get; init; } = true;

    public bool ReadVersionMetadata { get; init; } = true;

    public int? MaxEntriesPerRoot { get; init; }

    public int? MaxDepth { get; init; }

    public TimeSpan? PerRootTimeout { get; init; }
}

public sealed record DiscoveryError(string RootId, string? DiscoveryId, string Code, string Detail);

public sealed record DiscoveryRootReport(
    string RootId,
    WorkspaceStatus RootStatus,
    InstanceRootStructure Structure,
    int EntriesFound,
    int DirectoriesScanned,
    int FilesInspected,
    int SkippedByExclusion,
    int SkippedByLimit,
    int SkippedByReparsePoint,
    int ErrorCount,
    bool TimedOut,
    bool Cancelled,
    TimeSpan Duration,
    IReadOnlyList<DiscoveryError> Errors)
{
    public bool IsComplete => !TimedOut && !Cancelled && Errors.All(error =>
        error.Code is "source.unknown_structure" or "discovery.adoption_required");
}

public sealed record DiscoveryChange(string Kind, string RootId, string? DiscoveryId, string? Detail);

/// <summary>
/// 一次扫描的覆盖报告。部分结果绝不显示为完整；<see cref="IsComplete"/> 为 false 时
/// <see cref="Warnings"/> 必须说明原因（截断/超时/取消/失败）。
/// </summary>
public sealed record DiscoveryScanReport(
    IReadOnlyList<DiscoveryRootReport> Roots,
    IReadOnlyList<DiscoveryChange> Changes,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool IsComplete,
    IReadOnlyList<string> Warnings);

public sealed record DiscoveryProgress(string RootId, string? DiscoveryId, int EntriesFound, string Stage);

/// <summary>用户对重复条目的显式处理方式；领域层不自动按时间/名称决定代表。</summary>
public enum DuplicateResolution
{
    /// <summary>两个条目保留为独立实例（仅当路径身份不同）。</summary>
    KeepSeparate,

    /// <summary>保留代表条目，其余标记为重复；不删除任何条目。</summary>
    UseRepresentative,

    /// <summary>只删除重复的发现条目，不删除文件或实例记录。</summary>
    ForgetDuplicate,
}

/// <summary>
/// 发现扫描/目录服务（F-02 §5.2）。扫描只读来源；唯一写入是 ECL 自己的根状态与发现目录。
/// 外部实例的显式导入不属于本接口，由 Minecraft 层的导入服务完成。
/// </summary>
public interface IInstanceDiscoveryService
{
    Task<Result<DiscoveryScanReport>> ScanAsync(
        DiscoveryScanRequest? request = null,
        IProgress<DiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Result<DiscoveryCatalog>> GetCatalogAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    Task<Result<DiscoveryEntry>> GetEntryAsync(
        string discoveryId,
        CancellationToken cancellationToken = default);

    Task<Result<DiscoveryEntry>> RefreshEntryAsync(
        string discoveryId,
        CancellationToken cancellationToken = default);

    Task<Result<DiscoveryEntry>> ResolveDuplicateAsync(
        string discoveryId,
        DuplicateResolution resolution,
        CancellationToken cancellationToken = default);

    Task<Result> ForgetEntryAsync(
        string discoveryId,
        CancellationToken cancellationToken = default);

    /// <summary>移除某个来源的全部发现条目（<see cref="RootRemovalMode.Forget"/> 使用）。</summary>
    Task<Result<int>> ForgetRootEntriesAsync(
        string rootId,
        CancellationToken cancellationToken = default);

    /// <summary>更新条目的显式导入结果（导入成功后调用）。</summary>
    Task<Result<DiscoveryEntry>> MarkImportedAsync(
        string discoveryId,
        string instanceId,
        CancellationToken cancellationToken = default);

    Task<Result<DiscoveryEntry>> ClearImportedAsync(
        string discoveryId,
        CancellationToken cancellationToken = default);
}
