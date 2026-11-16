// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Storage;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Engine.Snapshots;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Lifecycle;

/// <summary>跨受管理根移动计划（F-03 §3.3/§5.5）。计划只读，执行时必须重新校验哈希。</summary>
public sealed record InstanceMovePlan(
    string PlanId,
    string PlanHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string InstanceId,
    string InstanceName,
    InstanceOwnership Ownership,
    string SourceRootId,
    string TargetRootId,
    string SourceRootPath,
    string TargetRootPath,
    string SourceRecordDirectory,
    string TargetRecordDirectory,
    string SourceGameDirectory,
    string TargetGameDirectory,
    int FileCount,
    long ByteCount,
    long TargetFreeBytes,
    bool RequiresSnapshot,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Evidence);

public sealed record InstanceMoveProgress(string Stage, int Completed, int Total, string? Detail);

public sealed record InstanceMoveResult(
    GameInstance Instance,
    string PlanId,
    string PlanHash,
    string TargetRecordDirectory,
    string TargetGameDirectory,
    string? RetiredSourcePath,
    string? SnapshotId,
    IReadOnlyList<string> Evidence);

/// <summary>生命周期 journal 中的移动记录（v1）。</summary>
public sealed record InstanceMoveJournal(
    int SchemaVersion,
    string PlanId,
    string PlanHash,
    string InstanceId,
    string InstanceName,
    string SourceRootId,
    string TargetRootId,
    string SourceRecordDirectory,
    string TargetRecordDirectory,
    string TargetGameDirectory,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? StagingPath,
    string? RetiredSourcePath,
    string? SnapshotId,
    IReadOnlyList<string> Verification,
    string? ErrorCode,
    string? ErrorDetail,
    InstanceMovePlan? Plan);

public interface IInstanceMoveService
{
    Task<Result<InstanceMovePlan>> PlanAsync(
        string instanceId,
        string targetRootId,
        CancellationToken cancellationToken = default);

    Task<Result<InstanceMoveResult>> ExecuteAsync(
        InstanceMovePlan plan,
        IProgress<InstanceMoveProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Result<InstanceMoveJournal>> GetAsync(string planId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<InstanceMoveJournal>>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>中断后重做复制/校验；源在切换前永不修改。</summary>
    Task<Result<InstanceMoveResult>> ResumeAsync(
        string planId,
        IProgress<InstanceMoveProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>只丢弃 staging；已切换后的计划不允许丢弃。</summary>
    Task<Result> DiscardAsync(string planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记源退休区已清理（F-03 §8.4）。只更新 journal，不删除任何数据；
    /// 目录删除由调用方（生命周期恢复服务）按 containment/重解析点校验后执行。
    /// </summary>
    Task<Result<InstanceMoveJournal>> MarkSourceCleanedAsync(
        string planId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// F-03 移动：目标必须是已登记的受管理根且与源根不同。执行顺序：
/// 计划 → 变更前快照 → 写门 → staging 复制 → 全量哈希校验 → 原子切换 →
/// 记录/登记提交 → 复验 → 源退休区保留。源在切换前不修改；任何失败保留可诊断状态。
/// </summary>
public sealed class InstanceMoveService : IInstanceMoveService
{
    private const int PolicyVersion = 1;
    private const long SafetyMarginBytes = 256L * 1024 * 1024;

    private static readonly string[] ExcludedDirectoryNames =
    [
        "logs",
        "crash-reports",
        "natives",
        ".ecl-trash",
        ".ecl-retired",
    ];

    private readonly IInstanceService _instances;
    private readonly IInstanceStore _store;
    private readonly IWorkspaceService _workspace;
    private readonly IInstanceActivityGuard _guard;
    private readonly IInstanceOwnershipClassifier _ownership;
    private readonly IInstancePathPolicy _pathPolicy;
    private readonly IInstanceWriteGate _gate;
    private readonly IInstanceLifecycleLock _lifecycleLock;
    private readonly InstanceIdentityService _identity;
    private readonly ISnapshotProtectionService _snapshots;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceMoveService> _logger;
    private readonly TimeProvider _time;

    public InstanceMoveService(
        IInstanceService instances,
        IInstanceStore store,
        IWorkspaceService workspace,
        IInstanceActivityGuard guard,
        IInstanceOwnershipClassifier ownership,
        IInstancePathPolicy pathPolicy,
        IInstanceWriteGate gate,
        InstanceIdentityService identity,
        ISnapshotProtectionService snapshots,
        IAppPaths paths,
        ILogger<InstanceMoveService>? logger = null,
        TimeProvider? time = null,
        IInstanceLifecycleLock? lifecycleLock = null)
    {
        _instances = instances;
        _store = store;
        _workspace = workspace;
        _guard = guard;
        _ownership = ownership;
        _pathPolicy = pathPolicy;
        _gate = gate;
        _lifecycleLock = lifecycleLock ?? new InstanceLifecycleLock(paths);
        _identity = identity;
        _snapshots = snapshots;
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceMoveService>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<InstanceMovePlan>> PlanAsync(
        string instanceId,
        string targetRootId,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceMovePlan>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceMovePlan>.Fail("instance.not_found", "实例不存在。");
        }

        var targetRoot = _workspace.Roots.FirstOrDefault(root =>
            string.Equals(root.Id, targetRootId, StringComparison.Ordinal));
        if (targetRoot is null)
        {
            return Result<InstanceMovePlan>.Fail(
                "instance.lifecycle.target_root_invalid",
                "目标不是已登记的受管理根，移动不可用。");
        }

        if (targetRoot.Kind != WorkspaceKind.Managed || !targetRoot.Enabled)
        {
            return Result<InstanceMovePlan>.Fail(
                "instance.lifecycle.target_root_invalid",
                "目标来源不是受管理根或已停用，不能作为移动目标。");
        }

        if (!Directory.Exists(targetRoot.Path))
        {
            return Result<InstanceMovePlan>.Fail(
                "instance.lifecycle.target_unavailable",
                "目标受管理根当前不可访问。");
        }

        var blockers = new List<string>();
        var warnings = new List<string>();
        var evidence = new List<string>();

        var ownership = await _ownership.ClassifyAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var ownershipValue = ownership.IsSuccess ? ownership.Value!.Ownership : InstanceOwnership.Unknown;
        if (!ownership.IsSuccess)
        {
            blockers.Add(ownership.Error!.Code);
        }
        else
        {
            evidence.AddRange(ownership.Value!.Evidence);
            if (ownershipValue != InstanceOwnership.EclManaged)
            {
                blockers.Add("instance.lifecycle.unsupported_ownership");
                warnings.Add("外部/共享实例只允许重定位，不支持跨根移动。");
            }
        }

        var sourceRecord = PathIdentity.Normalize(_paths.GetInstanceDirectory(instanceId))!;
        var sourceRoot = FindSourceRoot(instance, sourceRecord);
        string sourceRootId;
        string sourceRootPath;
        if (sourceRoot is null)
        {
            blockers.Add("instance.lifecycle.source_unavailable");
            warnings.Add("无法确定实例记录所在的受管理根。");
            sourceRootId = _workspace.Primary?.Id ?? string.Empty;
            sourceRootPath = _workspace.Primary?.Path ?? string.Empty;
        }
        else
        {
            sourceRootId = sourceRoot.Value.Id;
            sourceRootPath = sourceRoot.Value.Path;
        }

        if (string.Equals(sourceRootId, targetRootId, StringComparison.Ordinal))
        {
            return Result<InstanceMovePlan>.Fail(
                "instance.lifecycle.target_root_invalid",
                "目标根与源根相同；同一根内的目录改名不属于移动，请使用重定位。");
        }

        var targetRecord = Path.Combine(targetRoot.Path, "instances", instanceId);
        var targetGameDirectory = Path.Combine(targetRecord, "minecraft");
        var sourceGameDirectory = string.IsNullOrWhiteSpace(instance.GameDirectory)
            ? Path.Combine(sourceRecord, "minecraft")
            : instance.GameDirectory;

        if (!Directory.Exists(sourceRecord))
        {
            blockers.Add("instance.lifecycle.source_unavailable");
            warnings.Add("源实例记录目录不存在。");
        }

        if (Directory.Exists(targetRecord))
        {
            blockers.Add("instance.lifecycle.target_exists");
            warnings.Add("目标记录目录已存在。");
        }

        var targetCheck = _pathPolicy.CheckTarget(targetRoot.Path, allowMissing: false);
        if (!targetCheck.IsAllowed)
        {
            blockers.Add(targetCheck.ErrorCode ?? "instance.path.invalid");
            warnings.Add($"目标路径被路径策略拒绝：{targetCheck.Detail}");
        }

        var overlap = _pathPolicy.CheckMove(sourceRecord, targetRecord);
        if (!overlap.IsAllowed)
        {
            blockers.Add(overlap.ErrorCode ?? "instance.path.invalid");
            warnings.Add($"源/目标路径重叠：{overlap.Detail}");
        }

        var activity = await _guard.InspectAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!activity.IsSuccess)
        {
            blockers.Add("instance.lifecycle.activity_unknown");
        }
        else if (activity.Value!.HasActiveSession)
        {
            blockers.Add("instance.lifecycle.instance_running");
        }
        else if (activity.Value.HasPendingTransaction)
        {
            blockers.Add("instance.lifecycle.operation_conflict");
        }

        if (activity.IsSuccess && activity.Value!.IdentityConflict)
        {
            blockers.Add("instance.lifecycle.identity_conflict");
        }

        var measure = MeasureSource(sourceRecord, warnings);
        if (measure.ReparsePoints > 0)
        {
            blockers.Add("instance.lifecycle.reparse_point_present");
            warnings.Add("源目录包含重解析点/符号链接；默认不允许移动，请先处理。");
        }

        if (measure.ErrorCode is not null)
        {
            blockers.Add(measure.ErrorCode);
        }

        long targetFree = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(targetRoot.Path)!);
            targetFree = drive.AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            blockers.Add("instance.lifecycle.target_unavailable");
            warnings.Add($"无法读取目标磁盘剩余空间：{exception.Message}");
        }

        var required = measure.Bytes + SafetyMarginBytes + (long)(measure.Bytes * 0.01);
        if (targetFree > 0 && targetFree < required)
        {
            blockers.Add("instance.lifecycle.insufficient_space");
            warnings.Add($"目标空间不足：需要约 {required / (1024 * 1024)} MiB，可用 {targetFree / (1024 * 1024)} MiB。");
        }

        var issued = _time.GetUtcNow();
        var planId = $"move-{issued:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(6).ToLowerInvariant()}";
        var planHash = ComputePlanHash(
            instanceId,
            sourceRecord,
            targetRecord,
            sourceRootId,
            targetRoot.Id,
            measure.Files,
            measure.Bytes,
            RecordFingerprint(sourceRecord));

        evidence.Add($"source_files={measure.Files};source_bytes={measure.Bytes}");
        evidence.Add($"target_free_bytes={targetFree};required_bytes={required}");
        return Result<InstanceMovePlan>.Ok(new InstanceMovePlan(
            planId,
            planHash,
            issued,
            issued.AddMinutes(10),
            instanceId,
            instance.Name,
            ownershipValue,
            sourceRootId,
            targetRoot.Id,
            sourceRootPath,
            targetRoot.Path,
            sourceRecord,
            targetRecord,
            sourceGameDirectory,
            targetGameDirectory,
            measure.Files,
            measure.Bytes,
            targetFree,
            RequiresSnapshot: true,
            blockers,
            warnings,
            evidence));
    }

    public async Task<Result<InstanceMoveResult>> ExecuteAsync(
        InstanceMovePlan plan,
        IProgress<InstanceMoveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_time.GetUtcNow() > plan.ExpiresAt)
        {
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.plan_stale",
                "移动计划已过期，请重新检查。");
        }

        var current = await PlanAsync(plan.InstanceId, plan.TargetRootId, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return Result<InstanceMoveResult>.Fail(current.Error!);
        }

        if (!string.Equals(current.Value!.PlanHash, plan.PlanHash, StringComparison.OrdinalIgnoreCase))
        {
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.plan_stale",
                "源/目标内容在计划后发生变化，请重新检查。");
        }

        if (plan.Blockers.Count > 0)
        {
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.blocked",
                "移动计划存在阻断项，未执行任何操作。",
                string.Join("; ", plan.Blockers));
        }

        // F-05：变更前快照在获取写门之前创建，避免快照读租约与写门自锁。
        string? snapshotId = null;
        if (plan.RequiresSnapshot)
        {
            var protection = await _snapshots.EnsureAsync(
                plan.InstanceId,
                SnapshotProtectionRequirement.Required,
                SnapshotOrigin.PreChange,
                cancellationToken).ConfigureAwait(false);
            if (!protection.IsSuccess)
            {
                return Result<InstanceMoveResult>.Fail(protection.Error!);
            }

            snapshotId = protection.Value!.Snapshot?.SnapshotId;
        }

        InstanceMoveJournal journal;
        try
        {
            journal = await CreateJournalAsync(plan, snapshotId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.journal_write_failed",
                "无法写入生命周期 journal，已中止移动。",
                exception.Message);
        }

        return await ExecuteCoreSafelyAsync(plan, journal, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<InstanceMoveResult>> ExecuteCoreSafelyAsync(
        InstanceMovePlan plan,
        InstanceMoveJournal journal,
        IProgress<InstanceMoveProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteCoreAsync(plan, journal, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<InstanceMoveResult>.Fail(
                "source.cancelled",
                "移动已取消；staging 与 journal 保留，可重试或丢弃。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "移动实例 {InstanceId} 时发生 I/O 失败。", plan.InstanceId);
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.needs_recovery",
                "移动过程中发生 I/O 失败，请检查生命周期 journal 与 staging。",
                exception.Message);
        }
    }

    public async Task<Result<InstanceMoveResult>> ResumeAsync(
        string planId,
        IProgress<InstanceMoveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var journalResult = await GetAsync(planId, cancellationToken).ConfigureAwait(false);
        if (!journalResult.IsSuccess)
        {
            return Result<InstanceMoveResult>.Fail(journalResult.Error!);
        }

        var journal = journalResult.Value!;
        if (journal.Plan is null)
        {
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.needs_recovery",
                "journal 缺少计划内容，无法自动恢复。");
        }

        if (journal.State is not ("Interrupted" or "Copying" or "Verifying" or "Planned"))
        {
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.resume_invalid",
                $"journal 状态 {journal.State} 不允许重做复制。");
        }

        if (Directory.Exists(journal.TargetRecordDirectory))
        {
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.needs_recovery",
                "目标记录目录已存在，请人工检查后再处理。");
        }

        // 重新计划以验证源仍未被外部修改；哈希不同则拒绝静默继续。
        var current = await PlanAsync(journal.InstanceId, journal.TargetRootId, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess || !string.Equals(
                current.Value!.PlanHash,
                journal.PlanHash,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.plan_stale",
                "源内容已变化，不能按旧计划恢复；请重新生成计划。");
        }

        await DeleteStagingAsync(journal.StagingPath).ConfigureAwait(false);
        return await ExecuteCoreSafelyAsync(
            journal.Plan with { PlanId = journal.PlanId },
            journal,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<InstanceMoveJournal>> GetAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = FindJournalPath(planId);
        if (path is null)
        {
            return Task.FromResult(Result<InstanceMoveJournal>.Fail(
                "instance.lifecycle.journal_missing",
                "未找到该移动计划的生命周期 journal。"));
        }

        try
        {
            var journal = JsonSerializer.Deserialize<InstanceMoveJournal>(
                File.ReadAllText(path),
                EclJson.Default);
            return Task.FromResult(journal is null
                ? Result<InstanceMoveJournal>.Fail("instance.lifecycle.journal_invalid", "生命周期 journal 为空。")
                : Result<InstanceMoveJournal>.Ok(journal));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result<InstanceMoveJournal>.Fail(
                "instance.lifecycle.journal_invalid",
                "生命周期 journal 无法读取。",
                exception.Message));
        }
    }

    public Task<Result<IReadOnlyList<InstanceMoveJournal>>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(_paths.ConfigDirectory, "instance-engine", "lifecycle", instanceId);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(Result<IReadOnlyList<InstanceMoveJournal>>.Ok([]));
        }

        var journals = new List<InstanceMoveJournal>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<InstanceMoveJournal>(
                    File.ReadAllText(path),
                    EclJson.Default);
                if (journal is not null)
                {
                    journals.Add(journal);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "忽略无法读取的生命周期 journal：{Path}", path);
            }
        }

        return Task.FromResult(Result<IReadOnlyList<InstanceMoveJournal>>.Ok(
            journals.OrderByDescending(journal => journal.UpdatedAt).ToArray()));
    }

    public async Task<Result> DiscardAsync(string planId, CancellationToken cancellationToken = default)
    {
        var journalResult = await GetAsync(planId, cancellationToken).ConfigureAwait(false);
        if (!journalResult.IsSuccess)
        {
            return Result.Fail(journalResult.Error!);
        }

        var journal = journalResult.Value!;
        if (journal.State is "Committed" or "Completed" || Directory.Exists(journal.TargetRecordDirectory))
        {
            return Result.Fail(
                "instance.lifecycle.discard_invalid",
                "计划已切换/提交，不能丢弃；请检查目标记录。");
        }

        await DeleteStagingAsync(journal.StagingPath).ConfigureAwait(false);
        await WriteJournalAsync(journal with
        {
            State = "Discarded",
            UpdatedAt = _time.GetUtcNow(),
            StagingPath = null,
        }, CancellationToken.None).ConfigureAwait(false);
        return Result.Ok();
    }

    public async Task<Result<InstanceMoveJournal>> MarkSourceCleanedAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        var journalResult = await GetAsync(planId, cancellationToken).ConfigureAwait(false);
        if (!journalResult.IsSuccess)
        {
            return Result<InstanceMoveJournal>.Fail(journalResult.Error!);
        }

        var journal = journalResult.Value!;
        if (journal.State == "SourceCleaned")
        {
            // 幂等：重复清理只返回当前 journal。
            return Result<InstanceMoveJournal>.Ok(journal);
        }

        if (journal.State is not ("Committed" or "Completed"))
        {
            return Result<InstanceMoveJournal>.Fail(
                "instance.lifecycle.source_cleanup_invalid",
                $"journal 状态 {journal.State} 不允许清理退休源。");
        }

        try
        {
            var updated = journal with
            {
                State = "SourceCleaned",
                RetiredSourcePath = null,
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(updated, CancellationToken.None).ConfigureAwait(false);
            return Result<InstanceMoveJournal>.Ok(updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<InstanceMoveJournal>.Fail(
                "instance.lifecycle.journal_write_failed",
                "无法更新生命周期 journal；退休源清理结果未提交。",
                exception.Message);
        }
    }

    private async Task<Result<InstanceMoveResult>> ExecuteCoreAsync(
        InstanceMovePlan plan,
        InstanceMoveJournal journal,
        IProgress<InstanceMoveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var leaseResult = await _gate.AcquireAsync(plan.InstanceId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!leaseResult.IsSuccess)
        {
            return Result<InstanceMoveResult>.Fail(leaseResult.Error!);
        }

        await using var lease = leaseResult.Value!;
        var lifecycleLease = await _lifecycleLock.AcquireAsync(plan.InstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (!lifecycleLease.IsSuccess)
        {
            return Result<InstanceMoveResult>.Fail(lifecycleLease.Error!);
        }

        await using var lifecycleGuard = lifecycleLease.Value!;
        var instance = await _instances.GetAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceMoveResult>.Fail("instance.not_found", "实例不存在。");
        }

        if (!PathIdentity.PathsEqual(_paths.GetInstanceDirectory(instance.Id), plan.SourceRecordDirectory))
        {
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.plan_stale",
                "实例记录位置已变化，请重新计划。");
        }

        var staging = Path.Combine(
            plan.TargetRootPath,
            "instances",
            $".ecl-migrate-{plan.PlanId}");
        journal = journal with
        {
            State = "Copying",
            UpdatedAt = _time.GetUtcNow(),
            StagingPath = staging,
        };
        await WriteJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);

        Directory.CreateDirectory(Path.Combine(plan.TargetRootPath, "instances"));
        await DeleteStagingAsync(staging).ConfigureAwait(false);
        var copy = await CopyTreeAsync(
            plan.SourceRecordDirectory,
            staging,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (!copy.IsSuccess)
        {
            journal = journal with
            {
                State = copy.Error!.Code == "source.cancelled" ? "Interrupted" : "Failed",
                ErrorCode = copy.Error.Code,
                ErrorDetail = copy.Error.Message,
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
            return Result<InstanceMoveResult>.Fail(copy.Error!);
        }

        journal = journal with
        {
            State = "Verifying",
            Verification = copy.Value!.Select(item => $"{item.RelativePath}|{item.Bytes}|{item.Sha256}").ToArray(),
            UpdatedAt = _time.GetUtcNow(),
        };
        await WriteJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);

        var verify = await VerifyCopyAsync(copy.Value!, staging, progress, cancellationToken).ConfigureAwait(false);
        if (!verify.IsSuccess)
        {
            journal = journal with
            {
                State = "Failed",
                ErrorCode = verify.Error!.Code,
                ErrorDetail = verify.Error.Message,
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
            return Result<InstanceMoveResult>.Fail(verify.Error!);
        }

        journal = journal with { State = "Switching", UpdatedAt = _time.GetUtcNow() };
        await WriteJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        try
        {
            Directory.Move(staging, plan.TargetRecordDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            journal = journal with
            {
                State = "Failed",
                ErrorCode = "instance.lifecycle.verification_failed",
                ErrorDetail = $"无法切换到目标记录目录：{exception.Message}",
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.verification_failed",
                "无法把 staging 切换到目标记录目录，源保持不变。",
                exception.Message);
        }

        _paths.RegisterInstanceRoot(plan.InstanceId, plan.TargetRootPath);
        var updated = instance with
        {
            GameDirectory = null,
            SourceRootId = plan.TargetRootId,
        };

        var saveError = await TryCommitAsync(updated, plan, cancellationToken).ConfigureAwait(false);
        if (saveError is not null)
        {
            var rolledBack = await TryRollbackSwitchAsync(plan, staging).ConfigureAwait(false);
            journal = journal with
            {
                State = rolledBack ? "Failed" : "NeedsRecovery",
                ErrorCode = saveError!.Code,
                ErrorDetail = saveError.Message,
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
            return Result<InstanceMoveResult>.Fail(
                rolledBack ? saveError!.Code : "instance.lifecycle.needs_recovery",
                rolledBack
                    ? "写入目标实例记录失败，已回滚切换，源保持不变。"
                    : "写入目标实例记录失败且无法回滚，需要人工恢复。",
                saveError.Message);
        }

        // 源记录退休：源根 .ecl-retired/ 保留，绝不在切换前删除。
        string? retired = null;
        try
        {
            var retiredRoot = Path.Combine(plan.SourceRootPath, ".ecl-retired");
            Directory.CreateDirectory(retiredRoot);
            retired = Path.Combine(retiredRoot, $"{plan.InstanceId}-{plan.PlanId}");
            Directory.Move(plan.SourceRecordDirectory, retired);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "源记录目录退休失败：{Path}", plan.SourceRecordDirectory);
            try
            {
                var tombstone = Path.Combine(plan.SourceRecordDirectory, $"instance.json.retired-{plan.PlanId}");
                if (File.Exists(Path.Combine(plan.SourceRecordDirectory, "instance.json")))
                {
                    File.Move(Path.Combine(plan.SourceRecordDirectory, "instance.json"), tombstone);
                }
            }
            catch (Exception tombstoneException) when (tombstoneException is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(tombstoneException, "源记录退休与墓碑写入均失败。");
            }
        }

        var verification = await _identity.VerifyAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        var state = verification.IsSuccess && verification.Value!.Status != InstanceRegistrationStatus.Conflict
            ? "Committed"
            : "NeedsRecovery";
        journal = journal with
        {
            State = state,
            RetiredSourcePath = retired,
            UpdatedAt = _time.GetUtcNow(),
            ErrorCode = state == "NeedsRecovery" ? "instance.lifecycle.verification_failed" : null,
            ErrorDetail = state == "NeedsRecovery" ? "移动后的身份校验失败。" : null,
        };
        await WriteJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);

        if (state == "NeedsRecovery")
        {
            return Result<InstanceMoveResult>.Fail(
                "instance.lifecycle.needs_recovery",
                "实例已切换到目标根，但身份校验失败；请检查生命周期 journal。");
        }

        _logger.LogInformation(
            "实例 {InstanceId} 已从 {SourceRoot} 移动到 {TargetRoot}（计划 {PlanId}）。",
            plan.InstanceId,
            plan.SourceRootPath,
            plan.TargetRootPath,
            plan.PlanId);
        return Result<InstanceMoveResult>.Ok(new InstanceMoveResult(
            updated,
            plan.PlanId,
            plan.PlanHash,
            plan.TargetRecordDirectory,
            plan.TargetGameDirectory,
            retired,
            journal.SnapshotId,
            [$"files={copy.Value!.Count}", $"retired={retired ?? "(pending)"}"]));
    }

    /// <summary>提交记录与身份登记；返回错误码时由调用方决定回滚。</summary>
    private async Task<EclError?> TryCommitAsync(
        GameInstance updated,
        InstanceMovePlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new EclError("instance.lifecycle.verification_failed", "写入目标实例记录失败。", exception.Message);
        }

        var relocated = await _identity.RelocateAsync(
            plan.InstanceId,
            plan.TargetGameDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!relocated.IsSuccess)
        {
            return relocated.Error;
        }

        return null;
    }

    private async Task<bool> TryRollbackSwitchAsync(InstanceMovePlan plan, string staging)
    {
        try
        {
            if (Directory.Exists(plan.TargetRecordDirectory))
            {
                Directory.Move(plan.TargetRecordDirectory, staging);
            }

            _paths.RegisterInstanceRoot(plan.InstanceId, plan.SourceRootPath);
            var relocated = await _identity.RelocateAsync(
                plan.InstanceId,
                plan.SourceGameDirectory,
                CancellationToken.None).ConfigureAwait(false);
            if (!relocated.IsSuccess)
            {
                _logger.LogWarning("回滚身份登记失败：{Error}", relocated.Error);
                return false;
            }

            // 源记录目录必须存在；切换前从未修改它。
            return Directory.Exists(plan.SourceRecordDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "回滚移动切换失败。");
            return false;
        }
    }

    private async Task<Result<List<CopiedFile>>> CopyTreeAsync(
        string sourceDirectory,
        string stagingDirectory,
        IProgress<InstanceMoveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<(string Source, string Relative)>();
        try
        {
            Walk(sourceDirectory, string.Empty, files);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<List<CopiedFile>>.Fail(
                "instance.lifecycle.source_unavailable",
                "无法枚举源实例目录。",
                exception.Message);
        }

        var copied = new List<CopiedFile>(files.Count);
        var index = 0;
        foreach (var (source, relative) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(stagingDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                var hash = await CopyAndHashAsync(source, destination).ConfigureAwait(false);
                copied.Add(new CopiedFile(relative, new FileInfo(destination).Length, hash));
                progress?.Report(new InstanceMoveProgress("copying", ++index, files.Count, relative));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Result<List<CopiedFile>>.Fail(
                    "instance.lifecycle.verification_failed",
                    $"复制文件失败：{relative}",
                    exception.Message);
            }
        }

        return Result<List<CopiedFile>>.Ok(copied);
    }

    private void Walk(string sourceDirectory, string relative, List<(string Source, string Relative)> files)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     Path.Combine(sourceDirectory, relative)))
        {
            var name = Path.GetFileName(directory);
            if (ExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                name.StartsWith(".ecl-migrate-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsReparsePoint(directory))
            {
                continue;
            }

            Walk(sourceDirectory, Path.Combine(relative, name), files);
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(sourceDirectory, relative)))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add((file, relative.Length == 0 ? name : Path.Combine(relative, name)));
        }
    }

    private static async Task<string> CopyAndHashAsync(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            var before = new FileInfo(source);
            var expectedLength = before.Length;
            var expectedWrite = before.LastWriteTimeUtc;
            var expectedCreated = before.CreationTimeUtc;
            var attributes = before.Attributes & ~FileAttributes.ReparsePoint;
            using var sourceStream = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 128,
                FileOptions.SequentialScan);
            using var destinationStream = new FileStream(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 128];
            int read;
            long total = 0;
            while ((read = await sourceStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await destinationStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                total += read;
            }

            destinationStream.Flush(flushToDisk: true);
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());

            var after = new FileInfo(source);
            var unchanged = after.Length == expectedLength && after.LastWriteTimeUtc == expectedWrite && total == expectedLength;
            if (unchanged || attempt >= 1)
            {
                if (!unchanged)
                {
                    throw new IOException($"复制期间源文件持续变化：{source}");
                }

                File.SetLastWriteTimeUtc(destination, expectedWrite);
                File.SetCreationTimeUtc(destination, expectedCreated);
                File.SetAttributes(destination, attributes);
                return digest;
            }
        }
    }

    private static async Task<Result> VerifyCopyAsync(
        IReadOnlyList<CopiedFile> copied,
        string stagingDirectory,
        IProgress<InstanceMoveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var index = 0;
        foreach (var file in copied)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(stagingDirectory, file.RelativePath);
            if (!File.Exists(path))
            {
                return Result.Fail("instance.lifecycle.verification_failed", $"目标缺少文件：{file.RelativePath}");
            }

            var info = new FileInfo(path);
            if (info.Length != file.Bytes)
            {
                return Result.Fail(
                    "instance.lifecycle.verification_failed",
                    $"目标文件大小不符：{file.RelativePath}");
            }

            string hash;
            try
            {
                await using var stream = File.OpenRead(path);
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Result.Fail(
                    "instance.lifecycle.verification_failed",
                    $"无法校验目标文件：{file.RelativePath}",
                    exception.Message);
            }

            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Fail(
                    "instance.lifecycle.verification_failed",
                    $"目标文件哈希不符：{file.RelativePath}");
            }

            progress?.Report(new InstanceMoveProgress("verifying", ++index, copied.Count, file.RelativePath));
        }

        return Result.Ok();
    }

    private (string Id, string Path)? FindSourceRoot(GameInstance instance, string recordDirectory)
    {
        var roots = new List<(string Id, string Path)>();
        if (_workspace.Primary is { } primary)
        {
            roots.Add((primary.Id, primary.Path));
        }

        roots.AddRange(_workspace.Roots
            .Where(root => root.Kind == WorkspaceKind.Managed)
            .Select(root => (root.Id, root.Path)));

        foreach (var root in roots)
        {
            var candidate = Path.Combine(root.Path, "instances", instance.Id);
            if (PathIdentity.PathsEqual(candidate, recordDirectory))
            {
                return root;
            }
        }

        // 记录目录解析已使用实例→根映射；无法反查时按 SourceRootId 兜底。
        if (instance.SourceRootId is { } sourceRootId &&
            _workspace.Roots.FirstOrDefault(root => string.Equals(root.Id, sourceRootId, StringComparison.Ordinal)) is { } match)
        {
            return (match.Id, match.Path);
        }

        return null;
    }

    private static (int Files, long Bytes, int ReparsePoints, string? ErrorCode) MeasureSource(
        string sourceDirectory,
        List<string> warnings)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return (0, 0, 0, "instance.lifecycle.source_unavailable");
        }

        var files = 0;
        long bytes = 0;
        var reparsePoints = 0;
        try
        {
            WalkMeasure(sourceDirectory, string.Empty, ref files, ref bytes, ref reparsePoints);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"读取源目录失败：{exception.Message}");
            return (files, bytes, reparsePoints, "instance.lifecycle.source_unavailable");
        }

        return (files, bytes, reparsePoints, null);

        void WalkMeasure(string root, string relative, ref int count, ref long size, ref int links)
        {
            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, relative)))
            {
                var name = Path.GetFileName(directory);
                if (ExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                    name.StartsWith(".ecl-migrate-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsReparsePoint(directory))
                {
                    links++;
                    continue;
                }

                WalkMeasure(root, Path.Combine(relative, name), ref count, ref size, ref links);
            }

            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, relative)))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                count++;
                size += new FileInfo(file).Length;
            }
        }
    }

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

    private static string RecordFingerprint(string recordDirectory)
    {
        var path = Path.Combine(recordDirectory, "instance.json");
        if (!File.Exists(path))
        {
            return "missing";
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private static string ComputePlanHash(
        string instanceId,
        string sourceRecord,
        string targetRecord,
        string sourceRootId,
        string targetRootId,
        int files,
        long bytes,
        string recordFingerprint)
    {
        var text = string.Join(
            '|',
            instanceId,
            sourceRootId,
            targetRootId,
            sourceRecord,
            targetRecord,
            files,
            bytes,
            recordFingerprint,
            PolicyVersion);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private async Task<InstanceMoveJournal> CreateJournalAsync(InstanceMovePlan plan, string? snapshotId)
    {
        var journal = new InstanceMoveJournal(
            SchemaVersion: 1,
            PlanId: plan.PlanId,
            PlanHash: plan.PlanHash,
            InstanceId: plan.InstanceId,
            InstanceName: plan.InstanceName,
            SourceRootId: plan.SourceRootId,
            TargetRootId: plan.TargetRootId,
            SourceRecordDirectory: plan.SourceRecordDirectory,
            TargetRecordDirectory: plan.TargetRecordDirectory,
            TargetGameDirectory: plan.TargetGameDirectory,
            State: "Planned",
            CreatedAt: _time.GetUtcNow(),
            UpdatedAt: _time.GetUtcNow(),
            StagingPath: null,
            RetiredSourcePath: null,
            SnapshotId: snapshotId,
            Verification: [],
            ErrorCode: null,
            ErrorDetail: null,
            Plan: plan);
        await WriteJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        return journal;
    }

    private Task WriteJournalAsync(InstanceMoveJournal journal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(
            _paths.ConfigDirectory,
            "instance-engine",
            "lifecycle",
            journal.InstanceId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{journal.PlanId}.json");
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal, EclJson.Indented));
        File.Move(temporary, path, overwrite: true);
        return Task.CompletedTask;
    }

    private string? FindJournalPath(string planId)
    {
        var root = Path.Combine(_paths.ConfigDirectory, "instance-engine", "lifecycle");
        if (!Directory.Exists(root) || string.IsNullOrWhiteSpace(planId))
        {
            return null;
        }

        return Directory.EnumerateFiles(root, $"{planId}.json", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static async Task DeleteStagingAsync(string? staging)
    {
        if (string.IsNullOrWhiteSpace(staging) || !Directory.Exists(staging))
        {
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 保留 staging 供诊断，不阻断计划生成。
            }
        }).ConfigureAwait(false);
    }

    private sealed record CopiedFile(string RelativePath, long Bytes, string Sha256);
}
