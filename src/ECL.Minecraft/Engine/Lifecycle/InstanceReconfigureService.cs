// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Ecl.Core;
using Ecl.Core.Storage;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Engine.Inventory;
using Ecl.Minecraft.Engine.Snapshots;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Lifecycle;

/// <summary>重配置计划（F-03 §3.4/§5.6）。只读，执行前必须重算并校验哈希。</summary>
public sealed record InstanceReconfigurePlan(
    string PlanId,
    string PlanHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string InstanceId,
    string InstanceName,
    InstanceOwnership Ownership,
    string CurrentGameVersion,
    string? CurrentLoaderVersion,
    string TargetGameVersion,
    string TargetLoader,
    string? TargetLoaderVersion,
    string? TargetFabricApiVersion,
    string? TargetOptiFineVersion,
    string TargetVersionId,
    bool RequiresSnapshot,
    bool RequiresGameClosed,
    bool ExternalWriteConfirmed,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Evidence,
    string PolicyVersion = "f03-reconfigure-1");

public sealed record InstanceReconfigureProgress(string Stage, string? Detail = null);

public sealed record InstanceReconfigureResult(
    GameInstance Instance,
    string PlanId,
    string PlanHash,
    string? SnapshotId,
    IReadOnlyList<string> Verification,
    InstanceReconfigureJournal Journal);

/// <summary>重配置生命周期 journal（v1）。MetadataRestorePoint 是完整旧记录，用于对账而非猜测。</summary>
public sealed record InstanceReconfigureJournal(
    int SchemaVersion,
    string Kind,
    string PlanId,
    string PlanHash,
    string InstanceId,
    string InstanceName,
    InstanceOwnership Ownership,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? RecordDirectory,
    string? GameDirectory,
    string? SnapshotId,
    GameInstance? MetadataRestorePoint,
    GameInstance? TargetMetadata,
    IReadOnlyList<string> Verification,
    string? ErrorCode,
    string? ErrorDetail,
    string? PolicyVersion = null,
    InstanceReconfigurePlan? Plan = null);

public enum InstanceReconfigureState
{
    Planned,
    Reconfiguring,
    MetadataCommitted,
    Completed,
    RolledBack,
    Interrupted,
    NeedsRecovery,
    Failed,
}

public interface IInstanceReconfigureService
{
    Task<Result<InstanceReconfigurePlan>> PlanAsync(
        string instanceId,
        string gameVersion,
        string loader,
        string? loaderVersion = null,
        string? fabricApiVersion = null,
        string? optiFineVersion = null,
        bool externalWriteConfirmed = false,
        CancellationToken cancellationToken = default);

    Task<Result<InstanceReconfigureResult>> ExecuteAsync(
        InstanceReconfigurePlan plan,
        IProgress<InstanceReconfigureProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Result<InstanceReconfigureJournal>> GetAsync(
        string planId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<InstanceReconfigureJournal>>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>生命周期 journal 的绝对路径；不存在时返回 null。供 UI 打开证据目录，不暴露写入入口。</summary>
    string? GetJournalPath(string planId);
}

/// <summary>
/// F-03 S7 重配置编排：计划哈希 → 生命周期锁 → 变更前快照 → journal Reconfiguring（含元数据恢复点）
/// → 文件/共享 JSON 事务 → 元数据提交（唯一提交点）→ 复验 → journal Completed。
/// 失败时按“记录是否已改变”区分 RolledBack 与 NeedsRecovery，绝不把“下载完成”当成变更成功。
/// </summary>
public sealed class InstanceReconfigureService : IInstanceReconfigureService
{
    private const int SchemaVersion = 1;

    private readonly IInstanceService _instances;
    private readonly IInstanceStore _store;
    private readonly IInstanceOwnershipClassifier _ownership;
    private readonly IInstanceActivityGuard _guard;
    private readonly IInstanceLifecycleLock _lifecycleLock;
    private readonly ISnapshotProtectionService _snapshots;
    private readonly IInstanceConfigurationInstaller _installer;
    private readonly InstanceIdentityService _identity;
    private readonly InstanceDataGeneration _generation;
    private readonly InstanceLifecycleJournalStore _journals;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceReconfigureService> _logger;
    private readonly TimeProvider _time;

    public InstanceReconfigureService(
        IInstanceService instances,
        IInstanceStore store,
        IInstanceOwnershipClassifier ownership,
        IInstanceActivityGuard guard,
        IInstanceLifecycleLock lifecycleLock,
        ISnapshotProtectionService snapshots,
        IInstanceConfigurationInstaller installer,
        InstanceIdentityService identity,
        InstanceDataGeneration generation,
        InstanceLifecycleJournalStore journals,
        IAppPaths paths,
        ILogger<InstanceReconfigureService>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _store = store;
        _ownership = ownership;
        _guard = guard;
        _lifecycleLock = lifecycleLock;
        _snapshots = snapshots;
        _installer = installer;
        _identity = identity;
        _generation = generation;
        _journals = journals;
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceReconfigureService>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<InstanceReconfigurePlan>> PlanAsync(
        string instanceId,
        string gameVersion,
        string loader,
        string? loaderVersion = null,
        string? fabricApiVersion = null,
        string? optiFineVersion = null,
        bool externalWriteConfirmed = false,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceReconfigurePlan>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceReconfigurePlan>.Fail("instance.not_found", "实例不存在。");
        }

        var target = InstanceService.PreviewConfiguration(
            instance,
            gameVersion,
            loader,
            loaderVersion,
            fabricApiVersion,
            optiFineVersion);
        if (!target.IsSuccess)
        {
            return Result<InstanceReconfigurePlan>.Fail(target.Error!);
        }

        var targetInstance = target.Value!;
        var blockers = new List<string>();
        var warnings = new List<string>();
        var evidence = new List<string>();

        var ownership = await _ownership.ClassifyAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var ownershipKind = ownership.IsSuccess ? ownership.Value!.Ownership : InstanceOwnership.Unknown;
        if (!ownership.IsSuccess)
        {
            blockers.Add(ownership.Error!.Code);
        }
        else
        {
            evidence.AddRange(ownership.Value!.Evidence);
            switch (ownershipKind)
            {
                case InstanceOwnership.Unknown:
                    blockers.Add("instance.lifecycle.unsupported_ownership");
                    warnings.Add("所有权证据不足，拒绝重配置。");
                    break;
                case InstanceOwnership.SharedExternal:
                    // F-03 §3.8/§5.6：共享外部目录默认阻断，任何确认都不改变底层共享风险。
                    blockers.Add("instance.lifecycle.shared_write_requires_confirmation");
                    warnings.Add("该目录被多个实例共享；重配置会影响其他实例，当前不提供共享写入。");
                    break;
                case InstanceOwnership.External when !externalWriteConfirmed:
                    blockers.Add("instance.lifecycle.external_write_requires_confirmation");
                    warnings.Add("目标是外部实例目录；继续会修改外部数据，需要显式确认。");
                    break;
            }
        }

        var guard = await _guard.InspectAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!guard.IsSuccess)
        {
            blockers.Add("instance.lifecycle.activity_unknown");
        }
        else
        {
            if (guard.Value!.HasActiveSession)
            {
                blockers.Add("instance.lifecycle.instance_running");
            }

            if (guard.Value.HasPendingTransaction)
            {
                blockers.Add("instance.lifecycle.operation_conflict");
            }

            if (guard.Value.IdentityConflict)
            {
                blockers.Add("instance.lifecycle.identity_conflict");
            }
        }

        if (string.Equals(instance.GameVersion, targetInstance.GameVersion, StringComparison.Ordinal) &&
            string.Equals(instance.Loader, targetInstance.Loader, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(instance.LoaderVersion, targetInstance.LoaderVersion, StringComparison.Ordinal) &&
            string.Equals(instance.FabricApiVersion, targetInstance.FabricApiVersion, StringComparison.Ordinal) &&
            string.Equals(instance.OptiFineVersion, targetInstance.OptiFineVersion, StringComparison.Ordinal))
        {
            blockers.Add("instance.lifecycle.no_change");
            warnings.Add("目标配置与当前配置相同，没有需要执行的变更。");
        }

        var issued = _time.GetUtcNow();
        var planId = $"reconf-{issued:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(6).ToLowerInvariant()}";
        var planHash = ComputePlanHash(instance, targetInstance, ownershipKind);
        evidence.Add($"current={instance.GameVersion}/{instance.Loader}/{instance.LoaderVersion}");
        evidence.Add($"target={targetInstance.GameVersion}/{targetInstance.Loader}/{targetInstance.LoaderVersion}");
        evidence.Add($"record={_paths.GetInstanceDirectory(instanceId)}");

        return Result<InstanceReconfigurePlan>.Ok(new InstanceReconfigurePlan(
            planId,
            planHash,
            issued,
            issued.AddMinutes(10),
            instanceId,
            instance.Name,
            ownershipKind,
            instance.GameVersion,
            instance.LoaderVersion,
            targetInstance.GameVersion,
            targetInstance.Loader,
            targetInstance.LoaderVersion,
            targetInstance.FabricApiVersion,
            targetInstance.OptiFineVersion,
            targetInstance.VersionId,
            RequiresSnapshot: true,
            RequiresGameClosed: true,
            externalWriteConfirmed,
            blockers,
            warnings,
            evidence));
    }

    public async Task<Result<InstanceReconfigureResult>> ExecuteAsync(
        InstanceReconfigurePlan plan,
        IProgress<InstanceReconfigureProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_time.GetUtcNow() > plan.ExpiresAt)
        {
            return Result<InstanceReconfigureResult>.Fail(
                "instance.lifecycle.plan_stale",
                "重配置计划已过期，请重新检查。");
        }

        var current = await PlanAsync(
            plan.InstanceId,
            plan.TargetGameVersion,
            plan.TargetLoader,
            plan.TargetLoaderVersion,
            plan.TargetFabricApiVersion,
            plan.TargetOptiFineVersion,
            plan.ExternalWriteConfirmed,
            cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return Result<InstanceReconfigureResult>.Fail(current.Error!);
        }

        if (!string.Equals(current.Value!.PlanHash, plan.PlanHash, StringComparison.OrdinalIgnoreCase))
        {
            return Result<InstanceReconfigureResult>.Fail(
                "instance.lifecycle.plan_stale",
                "实例记录或目标配置在计划后发生变化，请重新确认。");
        }

        if (plan.Blockers.Count > 0)
        {
            return Result<InstanceReconfigureResult>.Fail(
                "instance.lifecycle.blocked",
                "重配置计划存在阻断项，未执行任何操作。",
                string.Join("; ", plan.Blockers));
        }

        var leaseResult = await _lifecycleLock.AcquireAsync(plan.InstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (!leaseResult.IsSuccess)
        {
            return Result<InstanceReconfigureResult>.Fail(leaseResult.Error!);
        }

        await using var lease = leaseResult.Value!;

        // 锁内重新确认活动状态，避免计划后启动的游戏被中断。
        var guard = await _guard.InspectAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        if (!guard.IsSuccess || guard.Value!.HasActiveSession || guard.Value.HasPendingTransaction ||
            guard.Value.IdentityConflict)
        {
            return Result<InstanceReconfigureResult>.Fail(
                "instance.lifecycle.operation_conflict",
                "实例状态在计划后发生变化（运行中/有写入任务/身份冲突），已中止。");
        }

        var restorePoint = await _instances.GetAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        if (restorePoint is null)
        {
            return Result<InstanceReconfigureResult>.Fail("instance.not_found", "实例不存在。");
        }

        progress?.Report(new InstanceReconfigureProgress("snapshot"));

        // 快照在写门之前创建，避免快照读租约与写门自锁（F-03 §8.1）。
        var protection = await _snapshots.EnsureAsync(
            plan.InstanceId,
            SnapshotProtectionRequirement.Required,
            SnapshotOrigin.PreChange,
            cancellationToken).ConfigureAwait(false);
        if (!protection.IsSuccess)
        {
            return Result<InstanceReconfigureResult>.Fail(protection.Error!);
        }

        var snapshotId = protection.Value!.Snapshot?.SnapshotId;
        var journal = await CreateJournalAsync(plan, restorePoint, snapshotId, cancellationToken)
            .ConfigureAwait(false);
        if (journal is null)
        {
            return Result<InstanceReconfigureResult>.Fail(
                "instance.lifecycle.journal_write_failed",
                "无法写入生命周期 journal，已中止重配置。");
        }

        journal = journal with
        {
            State = InstanceReconfigureState.Reconfiguring.ToString(),
            UpdatedAt = _time.GetUtcNow(),
        };
        await WriteJournalAsync(journal, cancellationToken).ConfigureAwait(false);

        progress?.Report(new InstanceReconfigureProgress("files"));

        var target = current.Value!;
        var targetInstance = restorePoint with
        {
            GameVersion = target.TargetGameVersion,
            Loader = target.TargetLoader,
            LoaderVersion = target.TargetLoaderVersion,
            FabricApiVersion = target.TargetFabricApiVersion,
            OptiFineVersion = target.TargetOptiFineVersion,
            VersionId = target.TargetVersionId,
        };

        InstallOutcome outcome;
        try
        {
            outcome = await _installer.ApplyConfigurationAsync(
                plan.InstanceId,
                targetInstance,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            journal = await UpdateAsync(journal, j =>
            {
                var latest = j with
                {
                    State = InstanceReconfigureState.Interrupted.ToString(),
                    ErrorCode = "source.cancelled",
                    ErrorDetail = "重配置已取消；元数据是否提交以 journal 与实例记录为准。",
                };
                return latest;
            }, cancellationToken).ConfigureAwait(false);
            return Result<InstanceReconfigureResult>.Fail(
                "source.cancelled",
                "重配置已取消；请检查生命周期 journal 与变更前快照。");
        }

        var installed = outcome.IsSuccess;
        var verification = new List<string>();
        if (installed)
        {
            journal = await UpdateAsync(journal, j => j with
            {
                State = InstanceReconfigureState.MetadataCommitted.ToString(),
                TargetMetadata = targetInstance,
            }, cancellationToken).ConfigureAwait(false);

            var verify = await VerifyAsync(plan, targetInstance, cancellationToken).ConfigureAwait(false);
            verification.AddRange(verify);
            var verified = verify.All(item => !item.StartsWith("fail:", StringComparison.Ordinal));

            journal = await UpdateAsync(journal, j => j with
            {
                State = verified
                    ? InstanceReconfigureState.Completed.ToString()
                    : InstanceReconfigureState.NeedsRecovery.ToString(),
                Verification = verification.ToArray(),
                ErrorCode = verified ? null : "instance.lifecycle.verification_failed",
                ErrorDetail = verified ? null : "重配置已提交但复验未通过。",
            }, cancellationToken).ConfigureAwait(false);

            _generation.Increment(plan.InstanceId);
            var latest = await _store.GetAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);

            if (!verified)
            {
                return Result<InstanceReconfigureResult>.Fail(
                    "instance.lifecycle.needs_recovery",
                    "重配置已提交但复验未通过；请按 journal 与快照检查。",
                    string.Join("; ", verification));
            }

            _logger.LogInformation(
                "实例 {InstanceId} 已重配置为 {GameVersion}/{Loader}/{LoaderVersion}（计划 {PlanId}，快照 {SnapshotId}）。",
                plan.InstanceId,
                targetInstance.GameVersion,
                targetInstance.Loader,
                targetInstance.LoaderVersion,
                plan.PlanId,
                snapshotId);
            return Result<InstanceReconfigureResult>.Ok(new InstanceReconfigureResult(
                latest ?? targetInstance,
                plan.PlanId,
                plan.PlanHash,
                snapshotId,
                verification,
                journal));
        }

        // 失败：以实例记录是否等于恢复点判定“已回滚”还是“需要恢复”。
        var afterFailure = await _store.GetAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        var rolledBack = afterFailure is not null && Matches(afterFailure, restorePoint);
        journal = await UpdateAsync(journal, j => j with
        {
            State = rolledBack
                ? InstanceReconfigureState.RolledBack.ToString()
                : InstanceReconfigureState.NeedsRecovery.ToString(),
            ErrorCode = outcome.Error?.Code,
            ErrorDetail = outcome.Error?.Message ?? outcome.Failure?.Error.Message,
            Verification = verification.ToArray(),
        }, cancellationToken).ConfigureAwait(false);

        _generation.Increment(plan.InstanceId);
        return Result<InstanceReconfigureResult>.Fail(
            rolledBack ? outcome.Error!.Code : "instance.lifecycle.needs_recovery",
            rolledBack
                ? "重配置失败，实例记录仍为旧配置。"
                : "重配置失败且无法证明实例已回到旧配置，需要人工恢复。",
            outcome.Error?.Detail ?? outcome.Error?.Message);
    }

    public async Task<Result<InstanceReconfigureJournal>> GetAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        var path = _journals.FindJournalPath(planId);
        if (path is null)
        {
            return Result<InstanceReconfigureJournal>.Fail(
                "instance.lifecycle.journal_missing",
                "未找到该重配置计划的生命周期 journal。");
        }

        try
        {
            var journal = System.Text.Json.JsonSerializer.Deserialize<InstanceReconfigureJournal>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                Ecl.Core.Json.EclJson.Default);
            return journal is null
                ? Result<InstanceReconfigureJournal>.Fail(
                    "instance.lifecycle.journal_invalid",
                    "重配置 journal 为空。")
                : Result<InstanceReconfigureJournal>.Ok(journal);
        }
        catch (Exception exception) when (exception is IOException or
                                              System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return Result<InstanceReconfigureJournal>.Fail(
                "instance.lifecycle.journal_invalid",
                "重配置 journal 无法读取。",
                exception.Message);
        }
    }

    public async Task<Result<IReadOnlyList<InstanceReconfigureJournal>>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var journals = new List<InstanceReconfigureJournal>();
        foreach (var path in _journals.EnumerateJournalFiles(instanceId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var journal = System.Text.Json.JsonSerializer.Deserialize<InstanceReconfigureJournal>(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                    Ecl.Core.Json.EclJson.Default);
                if (journal is not null && string.Equals(journal.Kind, "Reconfigure", StringComparison.OrdinalIgnoreCase))
                {
                    journals.Add(journal);
                }
            }
            catch (Exception exception) when (exception is IOException or
                                                  System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "忽略无法读取的重配置 journal：{Path}", path);
            }
        }

        return Result<IReadOnlyList<InstanceReconfigureJournal>>.Ok(
            journals.OrderByDescending(journal => journal.UpdatedAt).ToArray());
    }

    public string? GetJournalPath(string planId) => _journals.FindJournalPath(planId);

    private async Task<InstanceReconfigureJournal?> CreateJournalAsync(
        InstanceReconfigurePlan plan,
        GameInstance restorePoint,
        string? snapshotId,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var journal = new InstanceReconfigureJournal(
            SchemaVersion,
            "Reconfigure",
            plan.PlanId,
            plan.PlanHash,
            plan.InstanceId,
            plan.InstanceName,
            plan.Ownership,
            InstanceReconfigureState.Planned.ToString(),
            now,
            now,
            _paths.GetInstanceDirectory(plan.InstanceId),
            InstancePaths.GetGameDirectory(_paths, restorePoint),
            snapshotId,
            restorePoint,
            null,
            [],
            null,
            null,
            plan.PolicyVersion,
            plan);
        var written = await _journals.WriteAsync(plan.InstanceId, plan.PlanId, journal, cancellationToken)
            .ConfigureAwait(false);
        if (!written.IsSuccess)
        {
            return null;
        }

        return journal;
    }

    private async Task<InstanceReconfigureJournal> UpdateAsync(
        InstanceReconfigureJournal journal,
        Func<InstanceReconfigureJournal, InstanceReconfigureJournal> update,
        CancellationToken cancellationToken)
    {
        var updated = update(journal) with { UpdatedAt = _time.GetUtcNow() };
        await WriteJournalAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task WriteJournalAsync(InstanceReconfigureJournal journal, CancellationToken cancellationToken)
    {
        var written = await _journals.WriteAsync(
            journal.InstanceId,
            journal.PlanId,
            journal,
            cancellationToken).ConfigureAwait(false);
        if (!written.IsSuccess)
        {
            _logger.LogError("重配置 journal 写入失败：{Error}", written.Error);
        }
    }

    /// <summary>提交后复验：记录、版本目录/JSON、身份登记。返回 "ok:"/"fail:" 前缀的证据行。</summary>
    private async Task<IReadOnlyList<string>> VerifyAsync(
        InstanceReconfigurePlan plan,
        GameInstance target,
        CancellationToken cancellationToken)
    {
        var verification = new List<string>();
        var reloaded = await _store.GetAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        if (reloaded is null)
        {
            verification.Add("fail:instance record missing");
            return verification;
        }

        verification.Add(Matches(reloaded, target)
            ? "ok:metadata matches target"
            : $"fail:metadata={reloaded.GameVersion}/{reloaded.Loader}/{reloaded.LoaderVersion}");

        var versionDirectory = Path.Combine(_paths.VersionsDirectory, target.VersionId);
        var versionJson = Path.Combine(versionDirectory, $"{target.VersionId}.json");
        verification.Add(File.Exists(versionJson) ? $"ok:version json {target.VersionId}" : $"fail:version json {target.VersionId}");

        var registration = await _identity.VerifyAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        verification.Add(registration.IsSuccess && registration.Value!.Status != InstanceRegistrationStatus.Conflict
            ? "ok:identity registration"
            : "fail:identity registration conflict");
        return verification;
    }

    private static bool Matches(GameInstance left, GameInstance right) =>
        string.Equals(left.GameVersion, right.GameVersion, StringComparison.Ordinal) &&
        string.Equals(left.Loader, right.Loader, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.LoaderVersion, right.LoaderVersion, StringComparison.Ordinal) &&
        string.Equals(left.FabricApiVersion, right.FabricApiVersion, StringComparison.Ordinal) &&
        string.Equals(left.OptiFineVersion, right.OptiFineVersion, StringComparison.Ordinal) &&
        string.Equals(left.VersionId, right.VersionId, StringComparison.Ordinal);

    private static string ComputePlanHash(
        GameInstance current,
        GameInstance target,
        InstanceOwnership ownership)
    {
        var text = string.Join(
            '|',
            current.Id,
            current.GameVersion,
            current.Loader,
            current.LoaderVersion ?? string.Empty,
            current.FabricApiVersion ?? string.Empty,
            current.OptiFineVersion ?? string.Empty,
            current.VersionId,
            current.Name,
            current.GameDirectory ?? "(managed)",
            ownership,
            target.GameVersion,
            target.Loader,
            target.LoaderVersion ?? string.Empty,
            target.FabricApiVersion ?? string.Empty,
            target.OptiFineVersion ?? string.Empty,
            target.VersionId,
            "f03-reconfigure-1");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
