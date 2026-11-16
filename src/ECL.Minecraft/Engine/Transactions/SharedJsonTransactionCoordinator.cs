// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Transactions;

/// <summary>共享 JSON 只允许版本清单与资产索引两类目标（F-04 §5.4）。</summary>
public enum SharedJsonTargetKind
{
    VersionManifest,
    AssetIndex,
}

public sealed record SharedJsonWrite(
    SharedJsonTargetKind Kind,
    string AbsoluteTargetPath,
    byte[]? Content = null,
    string? SourcePath = null,
    string? ExpectedWrittenSha256 = null);

public sealed record SharedJsonTransactionPlan(
    IReadOnlyList<SharedJsonWrite> Writes,
    string Reason,
    string? InstanceId = null,
    string? TransactionId = null,
    string? CorrelationId = null,
    bool KeepRecoveryOnCommit = false);

public sealed record SharedJsonFingerprint(bool Exists, long Length, string? Sha256)
{
    public static SharedJsonFingerprint Missing { get; } = new(false, 0, null);
}

public sealed record SharedJsonBackup(string Blob, long Length, string Sha256);

public sealed record SharedJsonTargetRecord(
    int Index,
    SharedJsonTargetKind Kind,
    string TargetPath,
    SharedJsonFingerprint? Original = null,
    SharedJsonFingerprint? ExpectedWritten = null,
    SharedJsonFingerprint? Written = null,
    SharedJsonBackup? Backup = null,
    bool Applied = false);

public sealed record SharedJsonTransactionRecord(
    string TransactionId,
    SharedJsonTargetKind Kind,
    string Reason,
    SharedJsonTransactionState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SharedJsonTargetRecord> Targets,
    string? InstanceId = null,
    string? CorrelationId = null,
    string? RecoveryDirectory = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int SchemaVersion = 1);

public enum SharedJsonTransactionState
{
    Planned,
    Preparing,
    Applying,
    Verifying,
    Committed,
    RollingBack,
    RolledBack,
    NeedsRecovery,
    Failed,
}

public sealed record SharedJsonOwnershipState(
    string AbsoluteTargetPath,
    string? LastEclWrittenSha256,
    string? LastCommittedTransactionId,
    DateTimeOffset? LastCommittedAt,
    string? CurrentSha256,
    bool IsExternallyModified,
    string? AcknowledgedReason = null,
    DateTimeOffset? AcknowledgedAt = null);

public sealed record SharedJsonSweepReport(
    int Scanned,
    int RolledBack,
    int NeedsRecovery,
    int Skipped,
    IReadOnlyList<string> Problems);

public interface ISharedJsonTransactionService
{
    Task<Result<SharedJsonTransactionRecord>> ExecuteAsync(
        SharedJsonTransactionPlan plan,
        CancellationToken cancellationToken = default);

    Task<Result<SharedJsonTransactionRecord>> GetAsync(
        string transactionId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<SharedJsonTransactionRecord>>> ListAsync(
        SharedJsonTargetKind? kind = null,
        string? instanceId = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<Result<SharedJsonSweepReport>> RecoverPendingAsync(
        CancellationToken cancellationToken = default);

    Task<Result<SharedJsonTransactionRecord>> RecoverAsync(
        string transactionId,
        CancellationToken cancellationToken = default);

    /// <summary>安装/重配置失败时撤销已提交的共享 JSON 替换；目标已被外部修改时进入 NeedsRecovery。</summary>
    Task<Result<SharedJsonTransactionRecord>> RevertAsync(
        string transactionId,
        CancellationToken cancellationToken = default);

    /// <summary>安装整体提交后确认共享事务，删除为撤销预留的恢复材料。</summary>
    Task<Result> ConfirmAsync(
        string transactionId,
        CancellationToken cancellationToken = default);

    Task<Result<SharedJsonOwnershipState>> GetOwnershipAsync(
        string absoluteTargetPath,
        CancellationToken cancellationToken = default);

    Task<Result> AcknowledgeExternalModificationAsync(
        string absoluteTargetPath,
        string expectedCurrentSha256,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 共享版本 JSON / 资产索引的独立事务（F-04 S4，E2-1）。
/// 只接受白名单目标；先 journal+备份后替换；外部修改不被自动覆盖。
/// </summary>
public sealed class SharedJsonTransactionCoordinator : ISharedJsonTransactionService
{
    private const int CurrentSchemaVersion = 1;

    private readonly IAppPaths _paths;
    private readonly ILogger<SharedJsonTransactionCoordinator> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _targetLocks = new(StringComparer.Ordinal);

    public SharedJsonTransactionCoordinator(
        IAppPaths paths,
        ILogger<SharedJsonTransactionCoordinator>? logger = null,
        TimeProvider? time = null)
    {
        _paths = paths;
        _logger = logger ?? NullLogger<SharedJsonTransactionCoordinator>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string TransactionsRoot =>
        Path.Combine(_paths.ConfigDirectory, "instance-engine", "transactions", "shared");

    public string RecoveryRoot =>
        Path.Combine(_paths.ConfigDirectory, "instance-engine", "recovery", "shared");

    public string StatePath =>
        Path.Combine(_paths.ConfigDirectory, "instance-engine", "shared-json-state.json");

    public async Task<Result<SharedJsonTransactionRecord>> ExecuteAsync(
        SharedJsonTransactionPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (plan.Writes.Count == 0)
        {
            return Result<SharedJsonTransactionRecord>.Fail("shared.target_invalid", "共享 JSON 变更计划为空。");
        }

        var canonical = new List<(SharedJsonWrite Write, string Path, SharedJsonTargetKind Kind)>();
        foreach (var write in plan.Writes)
        {
            var validation = TryCanonicalize(write.Kind, write.AbsoluteTargetPath, out var path);
            if (!validation.IsSuccess)
            {
                return Result<SharedJsonTransactionRecord>.Fail(validation.Error!);
            }

            if (write.Content is null && (string.IsNullOrWhiteSpace(write.SourcePath) || !File.Exists(write.SourcePath)))
            {
                return Result<SharedJsonTransactionRecord>.Fail(
                    "shared.target_invalid",
                    "共享 JSON 来源文件不存在。",
                    write.AbsoluteTargetPath);
            }

            canonical.Add((write, path, write.Kind));
        }

        if (canonical.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != canonical.Count)
        {
            return Result<SharedJsonTransactionRecord>.Fail("shared.target_invalid", "同一共享事务不能重复写入同一目标。");
        }

        var transactionId = string.IsNullOrWhiteSpace(plan.TransactionId)
            ? $"shared-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(6).ToLowerInvariant()}"
            : plan.TransactionId.Trim();
        if (!IsValidTransactionId(transactionId))
        {
            return Result<SharedJsonTransactionRecord>.Fail("shared.invalid_id", "共享事务 ID 不合法。");
        }

        var locks = await AcquireTargetLocksAsync(
            canonical.Select(item => item.Path).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (!locks.IsSuccess)
        {
            return Result<SharedJsonTransactionRecord>.Fail(locks.Error!);
        }

        try
        {
            var journalPath = GetJournalPath(transactionId);
            if (File.Exists(journalPath))
            {
                var existing = await TryReadJournalAsync(journalPath, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    return Result<SharedJsonTransactionRecord>.Fail(
                        "transaction.journal_corrupt",
                        "已有共享事务日志损坏，拒绝覆盖。");
                }

                if (existing.State == SharedJsonTransactionState.Committed)
                {
                    foreach (var target in existing.Targets)
                    {
                        var current = ComputeFingerprint(target.TargetPath);
                        var written = target.Written ?? target.ExpectedWritten ?? SharedJsonFingerprint.Missing;
                        if (!ContentEquals(current, written))
                        {
                            return Result<SharedJsonTransactionRecord>.Fail(
                                "transaction.target_changed_after_commit",
                                "共享 JSON 事务已提交，但目标内容在提交后发生了变化。",
                                target.TargetPath);
                        }
                    }

                    return Result<SharedJsonTransactionRecord>.Ok(existing);
                }

                return Result<SharedJsonTransactionRecord>.Fail(
                    "transaction.duplicate",
                    "同一共享事务 ID 尚未完成，拒绝重复执行。",
                    existing.State.ToString());
            }

            // E2-1：最近 ECL 写后指纹存在且当前文件不匹配时，停止自动替换。
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in canonical)
            {
                var current = ComputeFingerprint(item.Path);
                if (state.TryGetValue(item.Path, out var entry) &&
                    !string.IsNullOrWhiteSpace(entry.LastEclWrittenSha256) &&
                    !string.Equals(current.Sha256, entry.LastEclWrittenSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return Result<SharedJsonTransactionRecord>.Fail(
                        "shared.external_modification",
                        "共享 JSON 在 ECL 写入后被外部修改；需人工确认后才能继续。",
                        item.Path);
                }
            }

            var now = _time.GetUtcNow();
            var recoveryDirectory = Path.Combine(RecoveryRoot, transactionId);
            var record = new SharedJsonTransactionRecord(
                transactionId,
                canonical[0].Kind,
                plan.Reason,
                SharedJsonTransactionState.Planned,
                now,
                now,
                canonical.Select((item, index) => new SharedJsonTargetRecord(
                    index,
                    item.Kind,
                    item.Path)).ToArray(),
                plan.InstanceId,
                plan.CorrelationId,
                recoveryDirectory,
                SchemaVersion: CurrentSchemaVersion);
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            record = record with { State = SharedJsonTransactionState.Preparing, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            record = await PrepareAsync(record, canonical, recoveryDirectory, cancellationToken).ConfigureAwait(false);

            record = record with { State = SharedJsonTransactionState.Applying, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            var targets = record.Targets.ToArray();
            for (var index = 0; index < targets.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = targets[index];
                var current = ComputeFingerprint(target.TargetPath);
                if (!ContentEquals(current, target.Original ?? SharedJsonFingerprint.Missing))
                {
                    throw new SharedJsonPreconditionException(
                        $"共享 JSON 在事务准备后被外部修改：{target.TargetPath}");
                }

                await ReplaceAsync(target.TargetPath, record.RecoveryDirectory!, index, cancellationToken)
                    .ConfigureAwait(false);
                targets[index] = target with
                {
                    Written = ComputeFingerprint(target.TargetPath),
                    Applied = true,
                };
                record = record with
                {
                    Targets = targets.ToArray(),
                    UpdatedAt = _time.GetUtcNow(),
                };
                await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);
            }

            record = record with { State = SharedJsonTransactionState.Verifying, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            foreach (var target in record.Targets)
            {
                var current = ComputeFingerprint(target.TargetPath);
                if (!ContentEquals(current, target.ExpectedWritten ?? SharedJsonFingerprint.Missing))
                {
                    throw new IOException($"共享 JSON 写后指纹不一致：{target.TargetPath}");
                }
            }

            record = record with { State = SharedJsonTransactionState.Committed, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);
            await UpdateStateOnCommitAsync(record, cancellationToken).ConfigureAwait(false);
            if (!plan.KeepRecoveryOnCommit)
            {
                TryDeleteDirectory(recoveryDirectory);
            }
            _logger.LogInformation(
                "共享 JSON 事务 {TransactionId} 已提交：{Reason}，目标 {Count} 项。",
                transactionId,
                plan.Reason,
                record.Targets.Count);
            return Result<SharedJsonTransactionRecord>.Ok(record);
        }
        catch (OperationCanceledException)
        {
            var cancelled = await TryRollbackJournalAsync(transactionId).ConfigureAwait(false);
            return Result<SharedJsonTransactionRecord>.Fail(
                "shared.cancelled",
                "共享 JSON 事务已取消。",
                cancelled?.State.ToString());
        }
        catch (SharedJsonPreconditionException exception)
        {
            var rolledBack = await TryRollbackJournalAsync(transactionId).ConfigureAwait(false);
            return Result<SharedJsonTransactionRecord>.Fail(
                "shared.changed_externally",
                "共享 JSON 在事务期间被外部修改，未覆盖外部内容。",
                $"{exception.Message}；回滚状态：{rolledBack?.State.ToString() ?? "未知"}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            var rolledBack = await TryRollbackJournalAsync(transactionId).ConfigureAwait(false);
            _logger.LogError(exception, "共享 JSON 事务失败。");
            return Result<SharedJsonTransactionRecord>.Fail(
                rolledBack is { State: SharedJsonTransactionState.NeedsRecovery }
                    ? "shared.needs_recovery"
                    : "shared.apply_failed",
                rolledBack is { State: SharedJsonTransactionState.NeedsRecovery }
                    ? "共享 JSON 事务失败且无法自动回滚，需要人工恢复。"
                    : "共享 JSON 事务失败，已回滚。",
                exception.Message);
        }
        finally
        {
            foreach (var lease in locks.Value!)
            {
                lease.Dispose();
            }
        }
    }

    public async Task<Result<SharedJsonTransactionRecord>> GetAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidTransactionId(transactionId))
        {
            return Result<SharedJsonTransactionRecord>.Fail("transaction.invalid_id", "共享事务 ID 不合法。");
        }

        var path = GetJournalPath(transactionId);
        if (!File.Exists(path))
        {
            return Result<SharedJsonTransactionRecord>.Fail("transaction.not_found", "共享事务不存在。");
        }

        var record = await TryReadJournalAsync(path, cancellationToken).ConfigureAwait(false);
        return record is null
            ? Result<SharedJsonTransactionRecord>.Fail("transaction.journal_corrupt", "共享事务日志损坏。")
            : Result<SharedJsonTransactionRecord>.Ok(record);
    }

    public async Task<Result<IReadOnlyList<SharedJsonTransactionRecord>>> ListAsync(
        SharedJsonTargetKind? kind = null,
        string? instanceId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(TransactionsRoot))
        {
            return Result<IReadOnlyList<SharedJsonTransactionRecord>>.Ok([]);
        }

        var records = new List<SharedJsonTransactionRecord>();
        foreach (var path in Directory.EnumerateFiles(TransactionsRoot, "*.json"))
        {
            var record = await TryReadJournalAsync(path, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                continue;
            }

            if (kind is { } value && record.Targets.All(target => target.Kind != value))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(instanceId) &&
                !string.Equals(record.InstanceId, instanceId, StringComparison.Ordinal))
            {
                continue;
            }

            records.Add(record);
        }

        return Result<IReadOnlyList<SharedJsonTransactionRecord>>.Ok(
            records.OrderByDescending(item => item.CreatedAt).Take(Math.Clamp(limit, 1, 500)).ToArray());
    }

    public async Task<Result<SharedJsonSweepReport>> RecoverPendingAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(TransactionsRoot))
        {
            return Result<SharedJsonSweepReport>.Ok(new SharedJsonSweepReport(0, 0, 0, 0, []));
        }

        var scanned = 0;
        var rolledBack = 0;
        var needsRecovery = 0;
        var skipped = 0;
        var problems = new List<string>();
        foreach (var path in Directory.EnumerateFiles(TransactionsRoot, "*.json"))
        {
            var record = await TryReadJournalAsync(path, cancellationToken).ConfigureAwait(false);
            if (record is null || IsTerminal(record.State))
            {
                continue;
            }

            scanned++;
            var recovered = await RecoverAsync(record.TransactionId, cancellationToken).ConfigureAwait(false);
            if (!recovered.IsSuccess)
            {
                skipped++;
                problems.Add($"{record.TransactionId}: {recovered.Error!.Code}");
            }
            else if (recovered.Value!.State == SharedJsonTransactionState.NeedsRecovery)
            {
                needsRecovery++;
                problems.Add($"{record.TransactionId}: needs recovery");
            }
            else
            {
                rolledBack++;
            }
        }

        return Result<SharedJsonSweepReport>.Ok(
            new SharedJsonSweepReport(scanned, rolledBack, needsRecovery, skipped, problems.Take(100).ToArray()));
    }

    public async Task<Result<SharedJsonTransactionRecord>> RecoverAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await GetAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var record = loaded.Value!;
        if (IsTerminal(record.State))
        {
            return loaded;
        }

        if (record.SchemaVersion > CurrentSchemaVersion)
        {
            return Result<SharedJsonTransactionRecord>.Ok(
                await MarkNeedsRecoveryAsync(
                    record,
                    "transaction.unsupported_schema",
                    "共享事务日志版本高于当前 ECL，禁止自动恢复。").ConfigureAwait(false));
        }

        var locks = await AcquireTargetLocksAsync(
            record.Targets.Select(target => target.TargetPath).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (!locks.IsSuccess)
        {
            return Result<SharedJsonTransactionRecord>.Fail(locks.Error!);
        }

        try
        {
            var rolledBack = await RollbackLockedAsync(record, cancellationToken).ConfigureAwait(false);
            return Result<SharedJsonTransactionRecord>.Ok(rolledBack);
        }
        finally
        {
            foreach (var lease in locks.Value!)
            {
                lease.Dispose();
            }
        }
    }

    public async Task<Result<SharedJsonTransactionRecord>> RevertAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await GetAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var record = loaded.Value!;
        if (record.State == SharedJsonTransactionState.RolledBack)
        {
            return loaded;
        }

        if (record.State != SharedJsonTransactionState.Committed)
        {
            return await RecoverAsync(transactionId, cancellationToken).ConfigureAwait(false);
        }

        var locks = await AcquireTargetLocksAsync(
            record.Targets.Select(target => target.TargetPath).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (!locks.IsSuccess)
        {
            return Result<SharedJsonTransactionRecord>.Fail(locks.Error!);
        }

        try
        {
            foreach (var target in record.Targets)
            {
                var current = ComputeFingerprint(target.TargetPath);
                var written = target.Written ?? target.ExpectedWritten ?? SharedJsonFingerprint.Missing;
                if (!ContentEquals(current, written))
                {
                    return Result<SharedJsonTransactionRecord>.Ok(
                        await MarkNeedsRecoveryAsync(
                            record,
                            "transaction.target_changed_after_commit",
                            "共享 JSON 在提交后又被外部修改，不能自动撤销。").ConfigureAwait(false));
                }
            }

            var reverted = await RollbackLockedAsync(record, cancellationToken).ConfigureAwait(false);
            return Result<SharedJsonTransactionRecord>.Ok(reverted);
        }
        finally
        {
            foreach (var lease in locks.Value!)
            {
                lease.Dispose();
            }
        }
    }

    public async Task<Result> ConfirmAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await GetAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Fail(loaded.Error!);
        }

        if (loaded.Value!.State != SharedJsonTransactionState.Committed)
        {
            return Result.Fail(
                "shared.not_committed",
                "只有已提交的共享事务可以确认并清理恢复材料。",
                loaded.Value.State.ToString());
        }

        TryDeleteDirectory(loaded.Value.RecoveryDirectory);
        return Result.Ok();
    }

    private async Task<SharedJsonTransactionRecord?> TryRollbackJournalAsync(string transactionId)
    {
        var path = GetJournalPath(transactionId);
        if (!File.Exists(path))
        {
            return null;
        }

        var record = await TryReadJournalAsync(path, CancellationToken.None).ConfigureAwait(false);
        if (record is null || IsTerminal(record.State))
        {
            return record;
        }

        return await RollbackLockedAsync(record, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<SharedJsonTransactionRecord> RollbackLockedAsync(
        SharedJsonTransactionRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            var states = record.Targets
                .Select(target => (Target: target, Current: ComputeFingerprint(target.TargetPath)))
                .Select(item => (item.Target, State: Classify(item.Target, item.Current)))
                .ToArray();
            if (states.Any(item => item.State == SharedRecoveryState.Other))
            {
                return await MarkNeedsRecoveryAsync(
                    record,
                    "transaction.target_changed",
                    "共享 JSON 既不是前态也不是 ECL 写后态，禁止自动恢复。").ConfigureAwait(false);
            }

            record = record with
            {
                State = SharedJsonTransactionState.RollingBack,
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(record, CancellationToken.None).ConfigureAwait(false);

            foreach (var item in states.Reverse())
            {
                if (item.State == SharedRecoveryState.NotApplied)
                {
                    continue;
                }

                await RestoreBackupAsync(record, item.Target, cancellationToken).ConfigureAwait(false);
            }

            var rolledBack = record with
            {
                State = SharedJsonTransactionState.RolledBack,
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(rolledBack, CancellationToken.None).ConfigureAwait(false);
            await UpdateStateAfterRollbackAsync(rolledBack, cancellationToken).ConfigureAwait(false);
            TryDeleteDirectory(record.RecoveryDirectory);
            return rolledBack;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            return await MarkNeedsRecoveryAsync(
                record,
                "shared.needs_recovery",
                $"共享 JSON 回滚失败：{exception.Message}").ConfigureAwait(false);
        }
    }

    public async Task<Result<SharedJsonOwnershipState>> GetOwnershipAsync(
        string absoluteTargetPath,
        CancellationToken cancellationToken = default)
    {
        var canonical = TryCanonicalize(null, absoluteTargetPath, out var path);
        if (!canonical.IsSuccess)
        {
            return Result<SharedJsonOwnershipState>.Fail(canonical.Error!);
        }

        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        state.TryGetValue(path, out var entry);
        var current = ComputeFingerprint(path);
        var last = entry?.LastEclWrittenSha256;
        var externallyModified = !string.IsNullOrWhiteSpace(last) &&
                                 !string.Equals(current.Sha256, last, StringComparison.OrdinalIgnoreCase);
        return Result<SharedJsonOwnershipState>.Ok(new SharedJsonOwnershipState(
            path,
            last,
            entry?.LastCommittedTransactionId,
            entry?.LastCommittedAt,
            current.Sha256,
            externallyModified,
            entry?.AcknowledgedReason,
            entry?.AcknowledgedAt));
    }

    public async Task<Result> AcknowledgeExternalModificationAsync(
        string absoluteTargetPath,
        string expectedCurrentSha256,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var canonical = TryCanonicalize(null, absoluteTargetPath, out var path);
        if (!canonical.IsSuccess)
        {
            return Result.Fail(canonical.Error!);
        }

        if (string.IsNullOrWhiteSpace(expectedCurrentSha256))
        {
            return Result.Fail("shared.target_invalid", "必须提供当前内容指纹。");
        }

        var current = ComputeFingerprint(path);
        if (!string.Equals(current.Sha256, expectedCurrentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail("shared.changed_externally", "共享 JSON 在确认期间再次变化，请重新检查。");
        }

        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        state[path] = new SharedJsonStateEntry(
            null,
            null,
            null,
            _time.GetUtcNow(),
            reason);
        await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("共享 JSON 外部修改已被人工接受：{Path}；原因：{Reason}", path, reason);
        return Result.Ok();
    }

    private Result TryCanonicalize(SharedJsonTargetKind? kind, string absoluteTargetPath, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(absoluteTargetPath))
        {
            return Result.Fail("shared.target_invalid", "共享 JSON 目标为空。");
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absoluteTargetPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result.Fail("shared.target_invalid", "共享 JSON 目标路径无效。", exception.Message);
        }

        if (Directory.Exists(full))
        {
            return Result.Fail("shared.target_invalid", "共享 JSON 目标不能是目录。", full);
        }

        if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail("shared.target_invalid", "共享 JSON 目标必须是 .json 文件。", full);
        }

        var versionsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.VersionsDirectory));
        var assetsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(_paths.AssetsDirectory, "indexes")));
        var isVersion = WorkspacePathValidator.IsUnder(full, versionsRoot);
        var isAssetIndex = WorkspacePathValidator.IsUnder(full, assetsRoot);
        if (!isVersion && !isAssetIndex)
        {
            return Result.Fail("shared.target_invalid", "共享 JSON 目标不在版本清单或资产索引白名单内。", full);
        }

        if (kind == SharedJsonTargetKind.VersionManifest && !isVersion)
        {
            return Result.Fail("shared.target_invalid", "目标不是版本清单。", full);
        }

        if (kind == SharedJsonTargetKind.AssetIndex && !isAssetIndex)
        {
            return Result.Fail("shared.target_invalid", "目标不是资产索引。", full);
        }

        if (isVersion)
        {
            var directory = Path.GetFileName(Path.GetDirectoryName(full));
            var fileName = Path.GetFileNameWithoutExtension(full);
            if (!string.Equals(directory, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Fail("shared.target_invalid", "版本清单必须位于 versions/<id>/<id>.json。", full);
            }
        }

        // 已存在的链接/重解析点拒绝，避免替换到实例外。
        try
        {
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            {
                return Result.Fail("shared.target_invalid", "共享 JSON 目标是重解析点，拒绝替换。", full);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail("shared.target_invalid", "无法读取共享 JSON 目标属性。", exception.Message);
        }

        path = full;
        return Result.Ok();
    }

    private async Task<SharedJsonTransactionRecord> PrepareAsync(
        SharedJsonTransactionRecord record,
        IReadOnlyList<(SharedJsonWrite Write, string Path, SharedJsonTargetKind Kind)> writes,
        string recoveryDirectory,
        CancellationToken cancellationToken)
    {
        var targets = new List<SharedJsonTargetRecord>(record.Targets.Count);
        Directory.CreateDirectory(Path.Combine(recoveryDirectory, "blobs"));
        for (var index = 0; index < record.Targets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = record.Targets[index];
            var original = ComputeFingerprint(target.TargetPath);
            SharedJsonBackup? backup = null;
            if (original.Exists)
            {
                var blob = Path.Combine("blobs", $"{index}-{original.Sha256![..16]}.bin");
                var destination = Path.Combine(recoveryDirectory, blob);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(target.TargetPath, destination, overwrite: true);
                var copied = ComputeFingerprint(destination);
                if (!copied.Exists ||
                    copied.Length != original.Length ||
                    !string.Equals(copied.Sha256, original.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"共享 JSON 备份复制后不一致：{target.TargetPath}");
                }

                backup = new SharedJsonBackup(blob.Replace('\\', '/'), copied.Length, copied.Sha256!);
            }

            var write = writes[index].Write;
            var expected = await ExpectedWrittenAsync(index, write, recoveryDirectory, cancellationToken)
                .ConfigureAwait(false);
            targets.Add(target with
            {
                Original = original,
                ExpectedWritten = expected,
                Backup = backup,
            });
        }

        record = record with
        {
            Targets = targets,
            UpdatedAt = _time.GetUtcNow(),
        };
        await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    private static async Task<SharedJsonFingerprint> ExpectedWrittenAsync(
        int index,
        SharedJsonWrite write,
        string recoveryDirectory,
        CancellationToken cancellationToken)
    {
        var staging = Path.Combine(recoveryDirectory, "blobs", $"{index}-source.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        if (write.Content is not null)
        {
            await File.WriteAllBytesAsync(staging, write.Content, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var input = new FileStream(
                write.SourcePath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                useAsync: true);
            await using var output = new FileStream(
                staging,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        var fingerprint = ComputeFingerprint(staging);
        if (!string.IsNullOrWhiteSpace(write.ExpectedWrittenSha256) &&
            !string.Equals(write.ExpectedWrittenSha256, fingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"共享 JSON 来源内容哈希与期望不符：{write.AbsoluteTargetPath}");
        }

        return fingerprint;
    }

    private static async Task ReplaceAsync(
        string target,
        string recoveryDirectory,
        int index,
        CancellationToken cancellationToken)
    {
        var temporary = $"{target}.{Guid.NewGuid():N}.shared.tmp";
        try
        {
            // 来源在 Prepare 阶段已冻结；Apply 只复制事务自己的 staging。
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var staging = Path.Combine(recoveryDirectory, "blobs", $"{index}-source.bin");
            if (!File.Exists(staging))
            {
                throw new InvalidDataException("共享 JSON 来源 staging 缺失。");
            }

            await using (var input = new FileStream(
                             staging,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 81920,
                             useAsync: true))
            await using (var output = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, target, overwrite: true);
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
                    // 无用残留不影响目标。
                }
            }
        }
    }

    private SharedRecoveryState Classify(SharedJsonTargetRecord target, SharedJsonFingerprint current)
    {
        if (ContentEquals(current, target.Original ?? SharedJsonFingerprint.Missing))
        {
            return SharedRecoveryState.NotApplied;
        }

        if (ContentEquals(current, target.Written ?? target.ExpectedWritten ?? SharedJsonFingerprint.Missing))
        {
            return SharedRecoveryState.Applied;
        }

        return SharedRecoveryState.Other;
    }

    private async Task RestoreBackupAsync(
        SharedJsonTransactionRecord record,
        SharedJsonTargetRecord target,
        CancellationToken cancellationToken)
    {
        var original = target.Original ?? SharedJsonFingerprint.Missing;
        if (!original.Exists)
        {
            if (File.Exists(target.TargetPath))
            {
                File.Delete(target.TargetPath);
            }

            return;
        }

        if (target.Backup is null)
        {
            throw new InvalidDataException($"缺少共享 JSON 备份：{target.TargetPath}");
        }

        var backupPath = Path.Combine(record.RecoveryDirectory!, target.Backup.Blob);
        if (!File.Exists(backupPath))
        {
            throw new InvalidDataException($"共享 JSON 恢复材料缺失：{target.Backup.Blob}");
        }

        var backup = ComputeFingerprint(backupPath);
        if (!backup.Exists ||
            backup.Length != target.Backup.Length ||
            !string.Equals(backup.Sha256, target.Backup.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"共享 JSON 恢复材料校验失败：{target.Backup.Blob}");
        }

        var temporary = $"{target.TargetPath}.{record.TransactionId}.rollback.tmp";
        File.Copy(backupPath, temporary, overwrite: true);
        File.Move(temporary, target.TargetPath, overwrite: true);
        var restored = ComputeFingerprint(target.TargetPath);
        if (!ContentEquals(restored, original))
        {
            throw new InvalidDataException($"共享 JSON 恢复后与前态不一致：{target.TargetPath}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<Result<List<IDisposable>>> AcquireTargetLocksAsync(
        IReadOnlyList<string> targets,
        CancellationToken cancellationToken)
    {
        var leases = new List<IDisposable>();
        foreach (var target in targets.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = target.ToLowerInvariant();
            var semaphore = _targetLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            var acquired = await semaphore.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                foreach (var lease in leases)
                {
                    lease.Dispose();
                }

                return Result<List<IDisposable>>.Fail("shared.busy", "共享 JSON 正被其他操作写入。");
            }

            var fileLock = TryAcquireFileLock(target);
            if (fileLock is null && OperatingSystem.IsWindows())
            {
                semaphore.Release();
                foreach (var lease in leases)
                {
                    lease.Dispose();
                }

                return Result<List<IDisposable>>.Fail("shared.busy", "另一个 ECL 进程正在写入该共享 JSON。");
            }

            leases.Add(new TargetLease(semaphore, fileLock));
        }

        return Result<List<IDisposable>>.Ok(leases);
    }

    private FileStream? TryAcquireFileLock(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var directory = Path.Combine(_paths.Root, "locks", "instance-engine");
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target)))[..16];
        var path = Path.Combine(directory, $"shared-{hash}.gate");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "无法获取共享 JSON 目标锁：{Target}", target);
            return null;
        }
    }

    private async Task<Dictionary<string, SharedJsonStateEntry>> LoadStateAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
        {
            return await RebuildStateFromJournalsAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var json = await File.ReadAllTextAsync(StatePath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<SharedJsonStateDocument>(json, EclJson.Default);
            return document?.Entries is null
                ? new Dictionary<string, SharedJsonStateEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, SharedJsonStateEntry>(document.Entries, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "共享 JSON 派生状态不可读，将从 journal 重建。");
            return await RebuildStateFromJournalsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 派生状态丢失时从最近的已提交共享事务重建“ECL 最后写入指纹”。
    /// journal 是权威；重建失败只影响自动替换，不允许静默覆盖外部内容。
    /// </summary>
    private async Task<Dictionary<string, SharedJsonStateEntry>> RebuildStateFromJournalsAsync(
        CancellationToken cancellationToken)
    {
        var state = new Dictionary<string, SharedJsonStateEntry>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(TransactionsRoot))
        {
            return state;
        }

        foreach (var path in Directory.EnumerateFiles(TransactionsRoot, "*.json"))
        {
            var record = await TryReadJournalAsync(path, cancellationToken).ConfigureAwait(false);
            if (record is null || record.State != SharedJsonTransactionState.Committed)
            {
                continue;
            }

            foreach (var target in record.Targets)
            {
                if (!state.TryGetValue(target.TargetPath, out var existing) ||
                    existing.LastCommittedAt is null ||
                    record.UpdatedAt >= existing.LastCommittedAt)
                {
                    state[target.TargetPath] = new SharedJsonStateEntry(
                        target.Written?.Sha256,
                        record.TransactionId,
                        record.UpdatedAt,
                        null,
                        null);
                }
            }
        }

        return state;
    }

    private async Task SaveStateAsync(
        IReadOnlyDictionary<string, SharedJsonStateEntry> state,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporary = $"{StatePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var document = new SharedJsonStateDocument(1, new Dictionary<string, SharedJsonStateEntry>(state));
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(document, EclJson.Indented),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, StatePath, overwrite: true);
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

    private async Task UpdateStateAfterRollbackAsync(
        SharedJsonTransactionRecord record,
        CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        foreach (var target in record.Targets)
        {
            var original = target.Original ?? SharedJsonFingerprint.Missing;
            state[target.TargetPath] = new SharedJsonStateEntry(
                original.Sha256,
                record.TransactionId,
                _time.GetUtcNow(),
                null,
                null);
        }

        await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateStateOnCommitAsync(
        SharedJsonTransactionRecord record,
        CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        foreach (var target in record.Targets)
        {
            state[target.TargetPath] = new SharedJsonStateEntry(
                target.Written?.Sha256,
                record.TransactionId,
                record.UpdatedAt,
                null,
                null);
        }

        await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SharedJsonTransactionRecord> MarkNeedsRecoveryAsync(
        SharedJsonTransactionRecord record,
        string errorCode,
        string message)
    {
        var updated = record with
        {
            State = SharedJsonTransactionState.NeedsRecovery,
            UpdatedAt = _time.GetUtcNow(),
            ErrorCode = errorCode,
            ErrorMessage = message,
        };
        await WriteJournalAsync(updated, CancellationToken.None).ConfigureAwait(false);
        return updated;
    }

    private async Task WriteJournalAsync(
        SharedJsonTransactionRecord record,
        CancellationToken cancellationToken)
    {
        var path = GetJournalPath(record.TransactionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(record, EclJson.Indented);
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.WriteThrough | FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

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

    private static async Task<SharedJsonTransactionRecord?> TryReadJournalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SharedJsonTransactionRecord>(json, EclJson.Default);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string GetJournalPath(string transactionId) =>
        Path.Combine(TransactionsRoot, $"{transactionId}.json");

    private static bool IsTerminal(SharedJsonTransactionState state) => state is
        SharedJsonTransactionState.Committed or
        SharedJsonTransactionState.RolledBack or
        SharedJsonTransactionState.Failed;

    private static bool ContentEquals(SharedJsonFingerprint left, SharedJsonFingerprint right) =>
        left.Exists == right.Exists &&
        (!left.Exists || left.Length == right.Length &&
            string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase));

    private static SharedJsonFingerprint ComputeFingerprint(string path)
    {
        if (!File.Exists(path))
        {
            return SharedJsonFingerprint.Missing;
        }

        try
        {
            var info = new FileInfo(path);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                useAsync: false);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            return new SharedJsonFingerprint(true, info.Length, hash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SharedJsonFingerprint.Missing;
        }
    }

    private static bool IsValidTransactionId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 96 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 恢复材料清理失败不影响已提交结果。
        }
    }

    private enum SharedRecoveryState
    {
        NotApplied,
        Applied,
        Other,
    }

    private sealed class SharedJsonPreconditionException(string message) : Exception(message);

    private sealed record SharedJsonStateDocument(
        int SchemaVersion,
        Dictionary<string, SharedJsonStateEntry> Entries);

    private sealed record SharedJsonStateEntry(
        string? LastEclWrittenSha256,
        string? LastCommittedTransactionId,
        DateTimeOffset? LastCommittedAt,
        DateTimeOffset? AcknowledgedAt,
        string? AcknowledgedReason);

    private sealed class TargetLease(SemaphoreSlim semaphore, FileStream? fileLock) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            fileLock?.Dispose();
            semaphore.Release();
        }
    }
}
