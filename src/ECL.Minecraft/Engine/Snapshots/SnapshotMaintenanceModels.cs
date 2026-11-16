// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Engine.Snapshots;

public sealed record SnapshotGroupSummary(
    string InstanceId,
    string? InstanceName,
    int SnapshotCount,
    long TotalBytes,
    DateTimeOffset? OldestCreatedAt,
    DateTimeOffset? NewestCreatedAt,
    int IncompleteCount,
    int CorruptCount,
    bool Orphaned,
    IReadOnlyList<string> SnapshotIds);

public sealed record SnapshotPruneOptions(
    TimeSpan? GracePeriod = null,
    bool DryRun = false,
    long? MaxBytes = null);

public sealed record SnapshotPruneResult(
    long FreedBytes,
    int DeletedObjects,
    int RetainedObjects,
    IReadOnlyList<string> Notes);

public sealed record SnapshotStorageSummary(
    int Groups,
    int SnapshotCount,
    long ObjectBytes,
    long ManifestBytes,
    int OrphanedGroups,
    int OrphanedObjects);

/// <summary>
/// 快照维护（F-05 §5.5）：组/孤儿可见性、显式删除与对象回收。
/// 不静默删除用户快照；实例已删除的组标为 Orphaned，等待用户显式处理。
/// </summary>
public interface ISnapshotMaintenanceService
{
    Task<Result<IReadOnlyList<SnapshotGroupSummary>>> ListGroupsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<SnapshotGroupSummary>> DeleteInstanceSnapshotsAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    Task<Result<SnapshotPruneResult>> PruneOrphansAsync(
        SnapshotPruneOptions options,
        CancellationToken cancellationToken = default);

    Task<Result<SnapshotStorageSummary>> GetStorageSummaryAsync(
        CancellationToken cancellationToken = default);
}
