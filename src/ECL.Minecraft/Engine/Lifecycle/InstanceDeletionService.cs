// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Storage;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Engine.Snapshots;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Lifecycle;

public enum InstanceDeletionEntryRole
{
    LauncherRecord,
    LauncherLogs,
    ManagedGameData,
    ExternalGameData,
    Snapshots,
}

public sealed record InstanceDeletionPlanEntry(
    string Path,
    InstanceDeletionEntryRole Role,
    InstanceOwnership Ownership,
    bool Exists,
    long Bytes,
    int Files,
    bool CanDelete,
    bool ContainsWorldSaves,
    string? ProtectedReason = null);

public sealed record InstanceSnapshotPlanSummary(
    int SnapshotCount,
    long TotalBytes,
    bool HasIncomplete,
    IReadOnlyList<string> SnapshotIds);

public sealed record InstanceDeletionPlanV2(
    string PlanId,
    string PlanHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string InstanceId,
    string InstanceName,
    InstanceOwnership Ownership,
    InstanceDeletionMode Mode,
    IReadOnlyList<InstanceDeletionPlanEntry> Entries,
    long EstimatedBytes,
    int EstimatedFiles,
    bool IncludesWorldSaves,
    bool DeleteSnapshots,
    InstanceSnapshotPlanSummary Snapshots,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string PolicyVersion = "f03-1");

public enum InstanceDeletionState
{
    Planned,
    Deleting,
    Quarantined,
    Purged,
    NeedsRecovery,
    Failed,
}

public sealed record InstanceDeletionJournal(
    int SchemaVersion,
    string PlanId,
    string PlanHash,
    string InstanceId,
    string InstanceName,
    InstanceOwnership Ownership,
    string RecordDirectory,
    string? GameDirectory,
    string? TombstonePath,
    string? DetachedGameDataPath,
    InstanceDeletionState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long QuarantinedBytes,
    bool DeleteSnapshots,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record InstanceDeletionResult(
    InstanceDeletionJournal Journal,
    bool Quarantined,
    long QuarantinedBytes,
    IReadOnlyList<string> RetainedSnapshots,
    string? DetachedGameDataPath);

public interface IInstanceDeletionService
{
    Task<Result<InstanceDeletionPlanV2>> PlanAsync(
        string instanceId,
        bool deleteSnapshots = false,
        InstanceDeletionMode mode = InstanceDeletionMode.DeleteManagedData,
        CancellationToken cancellationToken = default);

    Task<Result<InstanceDeletionResult>> DeleteAsync(
        InstanceDeletionPlanV2 plan,
        CancellationToken cancellationToken = default);

    /// <summary>读取单个删除 journal；恢复入口与诊断使用。</summary>
    Task<Result<InstanceDeletionJournal>> GetAsync(
        string planId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<InstanceDeletionJournal>>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 继续清理隔离区（F-03 §5.8/§6.6）。只对 <c>Quarantined/Deleting/Purging/NeedsRecovery</c> 且隔离区仍存在的
    /// journal 有效；从剩余项继续，失败项保留并重新进入 <see cref="InstanceDeletionState.NeedsRecovery"/>。
    /// 绝不删除隔离区之外的路径。
    /// </summary>
    Task<Result<InstanceDeletionJournal>> ResumeCleanupAsync(
        string planId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// F-03 删除事务：计划哈希 → 隔离区重命名 → 清理 → journal。
/// 外部/共享目录永远只移除 ECL 记录；清理前不宣称空间已释放。
/// </summary>
public sealed class InstanceDeletionService : IInstanceDeletionService
{
    private const int CurrentSchemaVersion = 1;

    private readonly IInstanceService _instances;
    private readonly InstanceIdentityService _identity;
    private readonly IInstanceOwnershipClassifier _ownership;
    private readonly IInstanceActivityGuard _guard;
    private readonly IInstancePathPolicy _pathPolicy;
    private readonly ISnapshotMaintenanceService _snapshots;
    private readonly IWorkspaceService _workspace;
    private readonly IAppPaths _paths;
    private readonly IInstanceLifecycleLock _lifecycleLock;
    private readonly ILogger<InstanceDeletionService> _logger;
    private readonly TimeProvider _time;

    public InstanceDeletionService(
        IInstanceService instances,
        InstanceIdentityService identity,
        IInstanceOwnershipClassifier ownership,
        IInstanceActivityGuard guard,
        IInstancePathPolicy pathPolicy,
        ISnapshotMaintenanceService snapshots,
        IWorkspaceService workspace,
        IAppPaths paths,
        ILogger<InstanceDeletionService>? logger = null,
        TimeProvider? time = null,
        IInstanceLifecycleLock? lifecycleLock = null)
    {
        _instances = instances;
        _identity = identity;
        _ownership = ownership;
        _guard = guard;
        _pathPolicy = pathPolicy;
        _snapshots = snapshots;
        _workspace = workspace;
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceDeletionService>.Instance;
        _time = time ?? TimeProvider.System;
        _lifecycleLock = lifecycleLock ?? new InstanceLifecycleLock(paths);
    }

    public async Task<Result<InstanceDeletionPlanV2>> PlanAsync(
        string instanceId,
        bool deleteSnapshots = false,
        InstanceDeletionMode mode = InstanceDeletionMode.DeleteManagedData,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceDeletionPlanV2>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceDeletionPlanV2>.Fail("instance.not_found", "实例不存在。");
        }

        var ownership = await _ownership.ClassifyAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!ownership.IsSuccess)
        {
            return Result<InstanceDeletionPlanV2>.Fail(ownership.Error!);
        }

        var recordDirectory = _paths.GetInstanceDirectory(instanceId);
        var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
        var entries = new List<InstanceDeletionPlanEntry>
        {
            Entry(
                _paths.GetInstanceMetadataFile(instanceId),
                InstanceDeletionEntryRole.LauncherRecord,
                InstanceOwnership.EclManaged,
                canDelete: true),
            Entry(
                _paths.GetInstanceSettingsFile(instanceId),
                InstanceDeletionEntryRole.LauncherRecord,
                InstanceOwnership.EclManaged,
                canDelete: true),
            Entry(
                _paths.GetInstanceLogsDirectory(instanceId),
                InstanceDeletionEntryRole.LauncherLogs,
                InstanceOwnership.EclManaged,
                canDelete: true),
        };

        var ownershipKind = ownership.Value!.Ownership;
        var managedData = instance.GameDirectory is null;
        var deleteManagedData = managedData &&
                                mode == InstanceDeletionMode.DeleteManagedData &&
                                ownershipKind == InstanceOwnership.EclManaged;
        entries.Add(Entry(
            gameDirectory,
            managedData ? InstanceDeletionEntryRole.ManagedGameData : InstanceDeletionEntryRole.ExternalGameData,
            ownershipKind,
            canDelete: deleteManagedData,
            protectedReason: deleteManagedData
                ? null
                : managedData
                    ? "用户选择仅移除记录，游戏数据保留。"
                    : "外部/共享实例的游戏目录不受 ECL 删除。"));

        var snapshotGroups = await _snapshots.ListGroupsAsync(cancellationToken).ConfigureAwait(false);
        var group = snapshotGroups.IsSuccess
            ? snapshotGroups.Value!.FirstOrDefault(item => string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal))
            : null;
        if (group is not null)
        {
            entries.Add(new InstanceDeletionPlanEntry(
                Path.Combine(_paths.Root, "snapshots", instanceId),
                InstanceDeletionEntryRole.Snapshots,
                InstanceOwnership.EclManaged,
                group.SnapshotCount > 0,
                group.TotalBytes,
                group.SnapshotCount,
                CanDelete: deleteSnapshots,
                ContainsWorldSaves: false,
                ProtectedReason: deleteSnapshots ? null : "用户快照默认保留；需显式选择删除。"));
        }

        var blockers = new List<string>();
        var warnings = new List<string>();
        var activity = await _guard.InspectAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!activity.IsSuccess)
        {
            blockers.Add(activity.Error!.Code);
        }
        else
        {
            if (activity.Value!.HasActiveSession)
            {
                blockers.Add("instance.lifecycle.instance_running");
            }

            if (activity.Value.HasPendingTransaction)
            {
                blockers.Add("instance.lifecycle.operation_conflict");
            }

            if (activity.Value.IdentityConflict)
            {
                blockers.Add("instance.lifecycle.identity_conflict");
            }
        }

        if (ownershipKind == InstanceOwnership.Unknown)
        {
            blockers.Add("instance.lifecycle.unsupported_ownership");
        }

        foreach (var entry in entries.Where(item => item.Exists && !item.CanDelete &&
                                                    item.Role is InstanceDeletionEntryRole.ManagedGameData))
        {
            warnings.Add($"不会删除：{entry.Path}");
        }

        var estimatedBytes = entries
            .Where(item => item.CanDelete)
            .Sum(item => item.Bytes);
        var estimatedFiles = entries.Where(item => item.CanDelete).Sum(item => item.Files);
        var includesWorldSaves = entries.Any(item =>
            item.Role == InstanceDeletionEntryRole.ManagedGameData && item.ContainsWorldSaves);
        var issued = _time.GetUtcNow();
        var planId = $"del-{issued:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(6).ToLowerInvariant()}";
        var planHash = ComputePlanHash(
            instanceId,
            recordDirectory,
            gameDirectory,
            ownershipKind,
            mode,
            deleteSnapshots,
            entries,
            group?.SnapshotCount ?? 0);
        var plan = new InstanceDeletionPlanV2(
            planId,
            planHash,
            issued,
            issued.AddMinutes(10),
            instanceId,
            instance.Name,
            ownershipKind,
            mode,
            entries,
            estimatedBytes,
            estimatedFiles,
            includesWorldSaves,
            deleteSnapshots,
            new InstanceSnapshotPlanSummary(
                group?.SnapshotCount ?? 0,
                group?.TotalBytes ?? 0,
                group?.IncompleteCount > 0,
                group?.SnapshotIds ?? []),
            blockers,
            warnings);
        return Result<InstanceDeletionPlanV2>.Ok(plan);
    }

    public async Task<Result<InstanceDeletionResult>> DeleteAsync(
        InstanceDeletionPlanV2 plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_time.GetUtcNow() > plan.ExpiresAt)
        {
            return Result<InstanceDeletionResult>.Fail(
                "instance.lifecycle.plan_stale",
                "删除计划已过期，请重新生成。");
        }

        var current = await PlanAsync(
            plan.InstanceId,
            plan.DeleteSnapshots,
            plan.Mode,
            cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return Result<InstanceDeletionResult>.Fail(current.Error!);
        }

        if (!string.Equals(current.Value!.PlanHash, plan.PlanHash, StringComparison.OrdinalIgnoreCase))
        {
            return Result<InstanceDeletionResult>.Fail(
                "instance.lifecycle.plan_stale",
                "实例或目标在计划后发生变化，请重新确认。");
        }

        if (plan.Blockers.Count > 0)
        {
            return Result<InstanceDeletionResult>.Fail(
                "instance.lifecycle.blocked",
                "删除计划存在阻断项。",
                string.Join("; ", plan.Blockers));
        }

        var instance = await _instances.GetAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceDeletionResult>.Fail("instance.not_found", "实例不存在。");
        }

        var lifecycleLease = await _lifecycleLock.AcquireAsync(plan.InstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (!lifecycleLease.IsSuccess)
        {
            return Result<InstanceDeletionResult>.Fail(lifecycleLease.Error!);
        }

        await using var lease = lifecycleLease.Value!;

        var recordDirectory = _paths.GetInstanceDirectory(plan.InstanceId);
        if (!Directory.Exists(recordDirectory))
        {
            return Result<InstanceDeletionResult>.Fail("instance.lifecycle.source_unavailable", "实例记录目录不存在。");
        }

        var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
        var managedData = instance.GameDirectory is null &&
                          plan.Ownership == InstanceOwnership.EclManaged &&
                          plan.Mode == InstanceDeletionMode.DeleteManagedData;
        // 隔离区必须与实例记录同卷，才能在切换时使用原子重命名（多受管理根 F-02）。
        var deleteRoot = ResolveRootPath(instance, recordDirectory);
        var trashRoot = Path.Combine(deleteRoot, ".ecl-trash");
        var tombstone = Path.Combine(trashRoot, $"{plan.InstanceId}-{plan.PlanId}");
        var journalRoot = Path.Combine(_paths.ConfigDirectory, "instance-engine", "lifecycle", plan.InstanceId);
        Directory.CreateDirectory(journalRoot);
        var journal = new InstanceDeletionJournal(
            CurrentSchemaVersion,
            plan.PlanId,
            plan.PlanHash,
            plan.InstanceId,
            plan.InstanceName,
            plan.Ownership,
            recordDirectory,
            managedData ? gameDirectory : null,
            null,
            null,
            InstanceDeletionState.Deleting,
            _time.GetUtcNow(),
            _time.GetUtcNow(),
            0,
            plan.DeleteSnapshots);
        await WriteJournalAsync(journalRoot, journal, cancellationToken).ConfigureAwait(false);

        // 随实例记录一起移动的删除摘要，用于“重命名后、journal 更新前”崩溃识别。
        await File.WriteAllTextAsync(
            Path.Combine(recordDirectory, "deletion.json"),
            JsonSerializer.Serialize(
                new { plan.PlanId, plan.PlanHash, plan.InstanceId, plan.InstanceName, Mode = managedData ? "all" : "record", CreatedAt = _time.GetUtcNow() },
                EclJson.Indented),
            cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(trashRoot);
            if (managedData)
            {
                var gameCheck = _pathPolicy.CheckDeletionEntry(gameDirectory, recordDirectory);
                if (!gameCheck.IsAllowed)
                {
                    return Result<InstanceDeletionResult>.Fail(
                        gameCheck.ErrorCode ?? "instance.path.owner_scope",
                        gameCheck.Detail ?? "游戏目录不在允许的删除范围内。");
                }

                Directory.Move(recordDirectory, tombstone);
            }
            else
            {
                Directory.CreateDirectory(tombstone);
                foreach (var fileName in new[] { "instance.json", "settings.json" })
                {
                    var source = Path.Combine(recordDirectory, fileName);
                    if (File.Exists(source))
                    {
                        File.Move(source, Path.Combine(tombstone, fileName));
                    }
                }

                var logs = Path.Combine(recordDirectory, "logs");
                if (Directory.Exists(logs))
                {
                    Directory.Move(logs, Path.Combine(tombstone, "logs"));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            journal = await UpdateAsync(journalRoot, journal with
            {
                State = InstanceDeletionState.Failed,
                ErrorCode = "instance.delete_failed",
                ErrorMessage = exception.Message,
            }, cancellationToken).ConfigureAwait(false);
            return Result<InstanceDeletionResult>.Fail(
                "instance.delete_failed",
                "无法把实例移入隔离区，源数据未删除。",
                exception.Message);
        }

        var quarantinedBytes = managedData ? plan.EstimatedBytes : 0;
        var unregistered = await _identity.UnregisterAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        if (!unregistered.IsSuccess)
        {
            _logger.LogWarning("实例 {InstanceId} 已隔离，但身份登记未移除：{Error}", plan.InstanceId, unregistered.Error);
        }

        journal = await UpdateAsync(journalRoot, journal with
        {
            State = InstanceDeletionState.Quarantined,
            TombstonePath = tombstone,
            DetachedGameDataPath = managedData ? null : gameDirectory,
            QuarantinedBytes = quarantinedBytes,
        }, cancellationToken).ConfigureAwait(false);

        var retainedSnapshots = plan.DeleteSnapshots ? [] : plan.Snapshots.SnapshotIds;
        if (plan.DeleteSnapshots && plan.Snapshots.SnapshotCount > 0)
        {
            var deletedSnapshots = await _snapshots.DeleteInstanceSnapshotsAsync(plan.InstanceId, cancellationToken)
                .ConfigureAwait(false);
            if (!deletedSnapshots.IsSuccess)
            {
                journal = await UpdateAsync(journalRoot, journal with
                {
                    State = InstanceDeletionState.NeedsRecovery,
                    ErrorCode = deletedSnapshots.Error!.Code,
                    ErrorMessage = "隔离完成，但删除快照失败。",
                }, cancellationToken).ConfigureAwait(false);
                return Result<InstanceDeletionResult>.Ok(new InstanceDeletionResult(
                    journal,
                    Quarantined: true,
                    quarantinedBytes,
                    RetainedSnapshots: plan.Snapshots.SnapshotIds,
                    DetachedGameDataPath: journal.DetachedGameDataPath));
            }
        }

        var purge = await PurgeQuarantineAsync(journal.TombstonePath, cancellationToken).ConfigureAwait(false);
        journal = await UpdateAsync(journalRoot, journal with
        {
            State = purge.IsSuccess ? InstanceDeletionState.Purged : InstanceDeletionState.NeedsRecovery,
            ErrorCode = purge.IsSuccess ? null : purge.Error!.Code,
            ErrorMessage = purge.IsSuccess ? null : purge.Error!.Message,
        }, cancellationToken).ConfigureAwait(false);

        if (purge.IsSuccess)
        {
            _paths.UnregisterInstanceRoot(plan.InstanceId);
        }
        else
        {
            _logger.LogWarning(
                "实例 {InstanceId} 的隔离区未清理完成，保留 journal 供恢复：{Error}",
                plan.InstanceId,
                purge.Error);
        }

        return Result<InstanceDeletionResult>.Ok(new InstanceDeletionResult(
            journal,
            Quarantined: true,
            quarantinedBytes,
            retainedSnapshots,
            journal.DetachedGameDataPath));
    }

    public Task<Result<InstanceDeletionJournal>> GetAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = FindJournalPath(planId);
        if (path is null)
        {
            return Task.FromResult(Result<InstanceDeletionJournal>.Fail(
                "instance.lifecycle.journal_missing",
                "未找到该删除计划的生命周期 journal。"));
        }

        return Task.FromResult(ReadJournal(path));
    }

    public Task<Result<IReadOnlyList<InstanceDeletionJournal>>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(_paths.ConfigDirectory, "instance-engine", "lifecycle", instanceId);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(Result<IReadOnlyList<InstanceDeletionJournal>>.Ok([]));
        }

        var journals = new List<InstanceDeletionJournal>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var read = ReadJournal(path);
            if (read.IsSuccess)
            {
                journals.Add(read.Value!);
            }
        }

        return Task.FromResult(Result<IReadOnlyList<InstanceDeletionJournal>>.Ok(
            journals.OrderByDescending(journal => journal.UpdatedAt).ToArray()));
    }

    public async Task<Result<InstanceDeletionJournal>> ResumeCleanupAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        var journalResult = await GetAsync(planId, cancellationToken).ConfigureAwait(false);
        if (!journalResult.IsSuccess)
        {
            return journalResult;
        }

        var journal = journalResult.Value!;
        if (journal.State == InstanceDeletionState.Purged)
        {
            // 幂等：已完成清理的计划重复恢复返回同一结果。
            return Result<InstanceDeletionJournal>.Ok(journal);
        }

        if (journal.State is not (InstanceDeletionState.Quarantined or InstanceDeletionState.Deleting
            or InstanceDeletionState.NeedsRecovery))
        {
            return Result<InstanceDeletionJournal>.Fail(
                "instance.lifecycle.resume_invalid",
                $"journal 状态 {journal.State} 不允许继续清理隔离区。");
        }

        if (string.IsNullOrWhiteSpace(journal.TombstonePath))
        {
            return Result<InstanceDeletionJournal>.Fail(
                "instance.lifecycle.needs_recovery",
                "journal 未记录隔离区路径，无法安全继续清理。");
        }

        var leaseResult = await _lifecycleLock.AcquireAsync(journal.InstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (!leaseResult.IsSuccess)
        {
            return Result<InstanceDeletionJournal>.Fail(leaseResult.Error!);
        }

        await using var lease = leaseResult.Value!;
        var journalRoot = Path.Combine(
            _paths.ConfigDirectory,
            "instance-engine",
            "lifecycle",
            journal.InstanceId);

        if (!Directory.Exists(journal.TombstonePath))
        {
            // 隔离区已不存在：说明上次清理已完成，只补齐 journal 终态。
            var completed = await UpdateAsync(journalRoot, journal with
            {
                State = InstanceDeletionState.Purged,
                ErrorCode = null,
                ErrorMessage = null,
            }, cancellationToken).ConfigureAwait(false);
            return Result<InstanceDeletionJournal>.Ok(completed);
        }

        journal = await UpdateAsync(journalRoot, journal with
        {
            State = InstanceDeletionState.Deleting,
            ErrorCode = null,
            ErrorMessage = null,
        }, cancellationToken).ConfigureAwait(false);

        var purge = await PurgeQuarantineAsync(journal.TombstonePath, cancellationToken).ConfigureAwait(false);
        journal = await UpdateAsync(journalRoot, journal with
        {
            State = purge.IsSuccess ? InstanceDeletionState.Purged : InstanceDeletionState.NeedsRecovery,
            ErrorCode = purge.IsSuccess ? null : purge.Error!.Code,
            ErrorMessage = purge.IsSuccess ? null : purge.Error!.Message,
        }, cancellationToken).ConfigureAwait(false);

        if (purge.IsSuccess)
        {
            _paths.UnregisterInstanceRoot(journal.InstanceId);
            return Result<InstanceDeletionJournal>.Ok(journal);
        }

        return Result<InstanceDeletionJournal>.Fail(purge.Error!);
    }

    /// <summary>
    /// 清理隔离区：只接受 <c>&lt;root&gt;/.ecl-trash/&lt;instanceId&gt;-&lt;planId&gt;</c> 形状的路径，
    /// 逐项拒绝重解析点，失败保留剩余项并可重试（F-03 §6.6/§7.2）。
    /// </summary>
    private Task<Result> PurgeQuarantineAsync(string? tombstone, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tombstone) || !Directory.Exists(tombstone))
        {
            return Task.FromResult(Result.Ok());
        }

        var normalized = PathIdentity.Normalize(tombstone);
        var parent = Path.GetDirectoryName(normalized);
        if (normalized is null || parent is null ||
            !string.Equals(Path.GetFileName(parent), ".ecl-trash", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Result.Fail(
                "instance.path.owner_scope",
                "隔离区路径不在 .ecl-trash 下，拒绝清理。"));
        }

        var name = Path.GetFileName(normalized);
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || name.Length <= dash + 1)
        {
            return Task.FromResult(Result.Fail(
                "instance.path.owner_scope",
                "隔离区目录名不符合 <instanceId>-<planId> 约定，拒绝清理。"));
        }

        var failures = new List<string>();
        var skipped = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(normalized))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    skipped++;
                    failures.Add($"跳过重解析点：{Path.GetFileName(entry)}");
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else if (File.Exists(entry))
                {
                    File.Delete(entry);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(entry)}：{exception.Message}");
            }
        }

        try
        {
            if (Directory.Exists(normalized) && !Directory.EnumerateFileSystemEntries(normalized).Any())
            {
                Directory.Delete(normalized);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add($"隔离区目录：{exception.Message}");
        }

        if (failures.Count > 0)
        {
            _logger.LogWarning(
                "隔离区 {Path} 仍有 {Count} 项未清理（跳过重解析点 {Skipped} 项）。",
                normalized,
                failures.Count,
                skipped);
            return Task.FromResult(Result.Fail(
                "instance.lifecycle.needs_recovery",
                "隔离区未完全清理；剩余项与失败原因已保留，可重试。",
                string.Join("; ", failures.Take(5))));
        }

        return Task.FromResult(Result.Ok());
    }

    private string? FindJournalPath(string planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            return null;
        }

        var root = Path.Combine(_paths.ConfigDirectory, "instance-engine", "lifecycle");
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateFiles(root, $"{planId}.json", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static Result<InstanceDeletionJournal> ReadJournal(string path)
    {
        try
        {
            var journal = JsonSerializer.Deserialize<InstanceDeletionJournal>(
                File.ReadAllText(path),
                EclJson.Default);
            return journal is null
                ? Result<InstanceDeletionJournal>.Fail(
                    "instance.lifecycle.journal_invalid",
                    "删除 journal 为空。")
                : Result<InstanceDeletionJournal>.Ok(journal);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result<InstanceDeletionJournal>.Fail(
                "instance.lifecycle.journal_invalid",
                "删除 journal 无法读取。",
                exception.Message);
        }
    }

    private string ResolveRootPath(GameInstance instance, string recordDirectory)
    {
        var match = _workspace.Roots.FirstOrDefault(root =>
            string.Equals(root.Kind, WorkspaceKind.Managed) &&
            PathIdentity.PathsEqual(Path.Combine(root.Path, "instances", instance.Id), recordDirectory));
        return match?.Path ?? _paths.GameRoot;
    }

    private InstanceDeletionPlanEntry Entry(
        string path,
        InstanceDeletionEntryRole role,
        InstanceOwnership ownership,
        bool canDelete,
        string? protectedReason = null)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        long bytes = 0;
        var files = 0;
        var containsWorldSaves = false;
        if (exists)
        {
            if (File.Exists(path))
            {
                bytes = new FileInfo(path).Length;
                files = 1;
            }
            else
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        bytes += new FileInfo(file).Length;
                        files++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // 不可读文件按 0 计，不伪造大小。
                    }
                }

                containsWorldSaves = Directory.Exists(Path.Combine(path, "saves"));
            }
        }

        return new InstanceDeletionPlanEntry(
            path,
            role,
            ownership,
            exists,
            bytes,
            files,
            canDelete,
            containsWorldSaves,
            protectedReason);
    }

    private static string ComputePlanHash(
        string instanceId,
        string recordDirectory,
        string gameDirectory,
        InstanceOwnership ownership,
        InstanceDeletionMode mode,
        bool deleteSnapshots,
        IReadOnlyList<InstanceDeletionPlanEntry> entries,
        int snapshotCount)
    {
        var builder = new StringBuilder();
        builder.Append(instanceId).Append('|')
            .Append(recordDirectory).Append('|')
            .Append(gameDirectory).Append('|')
            .Append(ownership).Append('|')
            .Append(mode).Append('|')
            .Append(deleteSnapshots).Append('|')
            .Append(snapshotCount);
        foreach (var entry in entries.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('|').Append(entry.Path).Append(':').Append(entry.Exists).Append(':')
                .Append(entry.Bytes).Append(':').Append(entry.CanDelete);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private async Task<InstanceDeletionJournal> UpdateAsync(
        string journalRoot,
        InstanceDeletionJournal journal,
        CancellationToken cancellationToken)
    {
        journal = journal with { UpdatedAt = _time.GetUtcNow() };
        await WriteJournalAsync(journalRoot, journal, cancellationToken).ConfigureAwait(false);
        return journal;
    }

    private static async Task WriteJournalAsync(
        string journalRoot,
        InstanceDeletionJournal journal,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(journalRoot);
        var path = Path.Combine(journalRoot, $"{journal.PlanId}.json");
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(journal, EclJson.Indented),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 残留临时文件无害。
                }
            }
        }
    }
}
