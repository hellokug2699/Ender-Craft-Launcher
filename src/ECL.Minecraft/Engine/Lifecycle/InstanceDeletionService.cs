// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
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
        CancellationToken cancellationToken = default);

    Task<Result<InstanceDeletionResult>> DeleteAsync(
        InstanceDeletionPlanV2 plan,
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
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceDeletionService> _logger;
    private readonly TimeProvider _time;

    public InstanceDeletionService(
        IInstanceService instances,
        InstanceIdentityService identity,
        IInstanceOwnershipClassifier ownership,
        IInstanceActivityGuard guard,
        IInstancePathPolicy pathPolicy,
        ISnapshotMaintenanceService snapshots,
        IAppPaths paths,
        ILogger<InstanceDeletionService>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _identity = identity;
        _ownership = ownership;
        _guard = guard;
        _pathPolicy = pathPolicy;
        _snapshots = snapshots;
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceDeletionService>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<InstanceDeletionPlanV2>> PlanAsync(
        string instanceId,
        bool deleteSnapshots = false,
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
        entries.Add(Entry(
            gameDirectory,
            managedData ? InstanceDeletionEntryRole.ManagedGameData : InstanceDeletionEntryRole.ExternalGameData,
            ownershipKind,
            canDelete: managedData && ownershipKind == InstanceOwnership.EclManaged,
            protectedReason: managedData ? null : "外部/共享实例的游戏目录不受 ECL 删除。"));

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

        var current = await PlanAsync(plan.InstanceId, plan.DeleteSnapshots, cancellationToken).ConfigureAwait(false);
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

        var recordDirectory = _paths.GetInstanceDirectory(plan.InstanceId);
        if (!Directory.Exists(recordDirectory))
        {
            return Result<InstanceDeletionResult>.Fail("instance.lifecycle.source_unavailable", "实例记录目录不存在。");
        }

        var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
        var managedData = instance.GameDirectory is null && plan.Ownership == InstanceOwnership.EclManaged;
        var trashRoot = Path.Combine(_paths.GameRoot, ".ecl-trash");
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

        try
        {
            if (Directory.Exists(tombstone))
            {
                Directory.Delete(tombstone, recursive: true);
            }

            journal = await UpdateAsync(journalRoot, journal with
            {
                State = InstanceDeletionState.Purged,
                UpdatedAt = _time.GetUtcNow(),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            journal = await UpdateAsync(journalRoot, journal with
            {
                State = InstanceDeletionState.NeedsRecovery,
                ErrorCode = "instance.lifecycle.needs_recovery",
                ErrorMessage = $"隔离区清理失败：{exception.Message}",
            }, cancellationToken).ConfigureAwait(false);
        }

        return Result<InstanceDeletionResult>.Ok(new InstanceDeletionResult(
            journal,
            Quarantined: true,
            quarantinedBytes,
            retainedSnapshots,
            journal.DetachedGameDataPath));
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
        bool deleteSnapshots,
        IReadOnlyList<InstanceDeletionPlanEntry> entries,
        int snapshotCount)
    {
        var builder = new StringBuilder();
        builder.Append(instanceId).Append('|')
            .Append(recordDirectory).Append('|')
            .Append(gameDirectory).Append('|')
            .Append(ownership).Append('|')
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
