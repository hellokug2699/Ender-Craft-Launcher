// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Engine.Snapshots;

public enum SnapshotScope
{
    /// <summary>完整实例快照：排除日志、崩溃报告、natives、.ecl 引擎元数据等易变/派生内容。</summary>
    Full,

    /// <summary>游戏环境：模组、配置、资源包、光影、默认配置与启动选项。</summary>
    Environment,

    /// <summary>单个世界存档。</summary>
    World,
}

public enum SnapshotStatus
{
    Complete,
    Incomplete,
    Corrupt,
}

public sealed record SnapshotFile(
    string RelativePath,
    long Size,
    string Sha256,
    DateTimeOffset ModifiedAt);

/// <summary>
/// 用户快照清单（ADR 0048 §2）。清单与内容寻址对象共同构成可验证快照；
/// 不完整创建的快照不得标记为 Complete。
/// </summary>
public sealed record SnapshotManifest(
    string SchemaVersion,
    string SnapshotId,
    string InstanceId,
    string InstanceName,
    string GameDirectory,
    DateTimeOffset CreatedAt,
    SnapshotScope Scope,
    string? WorldName,
    IReadOnlyList<string> IncludeRoots,
    IReadOnlyList<string> ExcludeRoots,
    IReadOnlyList<SnapshotFile> Files,
    long TotalBytes,
    SnapshotStatus Status,
    string? Note = null,
    string? ConsistencyWarning = null)
{
    public int FileCount => Files.Count;
}

public sealed record SnapshotCreateOptions(
    SnapshotScope Scope,
    string? WorldName = null,
    bool AllowRunningWorld = false);

public sealed record SnapshotProgress(string Stage, int Processed, int Total, string? CurrentItem = null);

public sealed record SnapshotVerification(
    string SnapshotId,
    bool IsValid,
    int MissingObjects,
    int CorruptObjects,
    int VerifiedObjects,
    IReadOnlyList<string> Problems);

public enum SnapshotDiffStatus
{
    Added,
    Removed,
    Modified,
    Unchanged,
    Unknown,
}

public sealed record SnapshotDiffEntry(
    string RelativePath,
    SnapshotDiffStatus Status,
    long? SnapshotSize,
    long? CurrentSize,
    string? Detail = null);

public sealed record SnapshotDiffSummary(
    string SnapshotId,
    int Added,
    int Removed,
    int Modified,
    int Unchanged,
    int Unknown,
    IReadOnlyList<SnapshotDiffEntry> Entries);

public sealed record SnapshotRestoreOptions(
    bool DeleteExtraFiles = true,
    bool CreateSafetySnapshot = true);

public sealed record SnapshotRestoreResult(
    string SnapshotId,
    string TransactionId,
    int Written,
    int Deleted,
    int Unchanged,
    string? SafetySnapshotId,
    IReadOnlyList<string> Warnings);

public interface IInstanceSnapshotService
{
    Task<Result<SnapshotManifest>> CreateAsync(
        string instanceId,
        SnapshotCreateOptions options,
        IProgress<SnapshotProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<SnapshotManifest>>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    Task<Result<SnapshotManifest>> GetAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default);

    Task<Result<SnapshotVerification>> VerifyAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default);

    Task<Result<SnapshotDiffSummary>> DiffAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default);

    Task<Result<SnapshotRestoreResult>> RestoreAsync(
        string instanceId,
        string snapshotId,
        SnapshotRestoreOptions? options = null,
        IProgress<SnapshotProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除用户快照。对象存储中的共享内容仅在显式 PruneOrphans 时清理。</summary>
    Task<Result> DeleteAsync(
        string instanceId,
        string snapshotId,
        CancellationToken cancellationToken = default);

    /// <summary>清理不再被任何快照清单引用的对象；必须由用户显式发起。</summary>
    Task<Result<long>> PruneOrphansAsync(CancellationToken cancellationToken = default);
}
