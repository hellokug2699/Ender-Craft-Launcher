// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Minecraft.Engine.Files;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Transactions;

public enum InstanceTransactionState
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
    Cancelled,

    /// <summary>人工确认后的终态（例如接受当前内容）。不得与 Committed/RolledBack 混用。</summary>
    Resolved,
}

public enum InstanceChangeKind
{
    WriteFile,
    DeleteFile,
    CreateDirectory,
}

public enum InstanceTransactionKind
{
    InstanceContent,
    InstanceMetadata,
}

public enum InstanceActivityState
{
    Idle,
    Writing,
    Recovering,
    NeedsRecovery,
    Changing,
    Missing,
    IdentityConflict,
}

public enum RecoveryResolution
{
    Auto,
    MarkRolledBack,
    AcceptCurrent,
}

/// <summary>
/// 一个受控变更。WriteFile 可以携带内存内容或外部来源文件（例如快照对象），
/// 但两者都不写入事务日志；日志只记录目标、阶段与前态/写后指纹。
/// </summary>
public sealed record InstanceChangeOperation(
    InstanceChangeKind Kind,
    string RelativePath,
    byte[]? Content = null,
    string? SourcePath = null,
    DateTimeOffset? LastWriteTime = null,
    string? ExpectedSha256 = null)
{
    public static InstanceChangeOperation WriteText(
        string relativePath,
        string text,
        DateTimeOffset? lastWriteTime = null) =>
        new(
            InstanceChangeKind.WriteFile,
            relativePath,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text),
            LastWriteTime: lastWriteTime);

    public static InstanceChangeOperation WriteBytes(
        string relativePath,
        byte[] content,
        DateTimeOffset? lastWriteTime = null) =>
        new(InstanceChangeKind.WriteFile, relativePath, content, LastWriteTime: lastWriteTime);

    public static InstanceChangeOperation CopyFrom(
        string relativePath,
        string sourcePath,
        string? expectedSha256 = null) =>
        new(InstanceChangeKind.WriteFile, relativePath, SourcePath: sourcePath, ExpectedSha256: expectedSha256);

    public static InstanceChangeOperation Delete(string relativePath) =>
        new(InstanceChangeKind.DeleteFile, relativePath);

    public static InstanceChangeOperation CreateDirectory(string relativePath) =>
        new(InstanceChangeKind.CreateDirectory, relativePath);
}

public sealed record InstanceChangePlan(
    IReadOnlyList<InstanceChangeOperation> Operations,
    string Reason,
    string? TransactionId = null,
    string? CorrelationId = null);

public sealed record InstanceFileFingerprint(
    bool Exists,
    bool IsDirectory,
    long Length,
    long LastWriteUtcTicks,
    string? Sha256)
{
    public static InstanceFileFingerprint Missing { get; } = new(false, false, 0, 0, null);
}

public sealed record InstancePrecondition(string RelativePath, InstanceFileFingerprint Fingerprint);

public sealed record InstanceBackupRecord(string Blob, long Length, string Sha256);

public sealed record InstanceTransactionOperationRecord(
    int Index,
    InstanceChangeKind Kind,
    string RelativePath,
    InstanceFileFingerprint? Original = null,
    InstanceFileFingerprint? ExpectedWritten = null,
    InstanceFileFingerprint? Written = null,
    InstanceBackupRecord? Backup = null,
    bool Completed = false);

public sealed record InstanceTransactionRecord(
    string TransactionId,
    string InstanceId,
    string InstanceDirectory,
    string Reason,
    InstanceTransactionState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<InstanceTransactionOperationRecord> Operations,
    IReadOnlyList<InstancePrecondition> Preconditions,
    int CompletedOperations,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? RecoveryDirectory = null,
    int SchemaVersion = 0,
    InstanceTransactionKind Kind = InstanceTransactionKind.InstanceContent,
    string? CorrelationId = null,
    string? Resolution = null,
    DateTimeOffset? ResolvedAt = null);

public sealed record InstanceTransactionListFilter(
    InstanceTransactionState? State = null,
    bool IncludeTerminal = true,
    int Limit = 200);

public sealed record RecoverySweepReport(
    int Scanned,
    int RolledBack,
    int NeedsRecovery,
    int Skipped,
    IReadOnlyList<string> Problems);

public sealed record InstanceWriteLeaseState(string InstanceId, bool WriteHeld, int Readers);

/// <summary>
/// 实例级写事务协调器（ADR 0047 / F-04）。首期采用实例级串行化：一个实例同时只允许一个事务进入 Apply。
/// 事务日志、前态备份与阶段状态持久化；失败时按指纹对账回滚；无法证明恢复成功时进入 NeedsRecovery。
/// </summary>
public sealed class InstanceTransactionCoordinator : IInstanceTransactionService
{
    private const int JournalFlushInterval = 25;
    private const int CurrentSchemaVersion = 2;

    private readonly IInstanceService _instances;
    private readonly InstanceIdentityService _identity;
    private readonly IInstanceWriteGate _gate;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceTransactionCoordinator> _logger;
    private readonly TimeProvider _time;

    public InstanceTransactionCoordinator(
        IInstanceService instances,
        InstanceIdentityService identity,
        IInstanceWriteGate gate,
        IAppPaths paths,
        ILogger<InstanceTransactionCoordinator>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _identity = identity;
        _gate = gate;
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceTransactionCoordinator>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string TransactionsRoot => Path.Combine(_paths.ConfigDirectory, "instance-engine", "transactions", "instances");

    public string RecoveryRoot => Path.Combine(_paths.ConfigDirectory, "instance-engine", "recovery", "instances");

    /// <summary>旧版恢复材料位置，仅用于继续读取未完成事务。</summary>
    public string LegacyRecoveryRoot => Path.Combine(_paths.CacheDirectory, "instance-engine", "recovery");

    public Task<Result<InstanceTransactionRecord>> ExecuteAsync(
        string instanceId,
        InstanceChangePlan plan,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(instanceId, plan, plan.TransactionId, cancellationToken);

    public async Task<Result<InstanceTransactionRecord>> ExecuteAsync(
        string instanceId,
        InstanceChangePlan plan,
        string? transactionId,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceTransactionRecord>.Fail("transaction.invalid_instance", "实例 ID 不合法。");
        }

        if (plan.Operations.Count == 0)
        {
            return Result<InstanceTransactionRecord>.Fail("transaction.empty", "变更计划为空。");
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceTransactionRecord>.Fail("instance.not_found", "实例不存在。");
        }

        var identity = await _identity.VerifyAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (identity.IsSuccess && identity.Value!.Status == InstanceRegistrationStatus.Conflict)
        {
            return Result<InstanceTransactionRecord>.Fail(
                "transaction.identity_conflict",
                "实例身份存在冲突，写操作已暂停。",
                identity.Value.Message);
        }

        var root = new InstanceFileWorkspace(InstancePaths.GetGameDirectory(_paths, instance));
        if (!Directory.Exists(root.RootPath))
        {
            return Result<InstanceTransactionRecord>.Fail("transaction.root_missing", "实例游戏目录不存在。");
        }

        var validation = ValidatePlan(root, plan);
        if (!validation.IsSuccess)
        {
            return Result<InstanceTransactionRecord>.Fail(validation.Error!);
        }

        var txid = string.IsNullOrWhiteSpace(transactionId)
            ? $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(8).ToLowerInvariant()}"
            : transactionId.Trim();
        if (!IsValidTransactionId(txid))
        {
            return Result<InstanceTransactionRecord>.Fail("transaction.invalid_id", "事务 ID 不合法。");
        }

        var leaseResult = await _gate.AcquireAsync(instanceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!leaseResult.IsSuccess)
        {
            return Result<InstanceTransactionRecord>.Fail(leaseResult.Error!);
        }

        await using var lease = leaseResult.Value!;
        var journalPath = GetJournalPath(instanceId, txid);
        var existing = await TryReadOrFailAsync(journalPath, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return Result<InstanceTransactionRecord>.Fail(existing.Error!);
        }

        if (existing.Value is { } existingRecord)
        {
            return await ReplayAsync(instanceId, existingRecord, root, cancellationToken).ConfigureAwait(false);
        }

        var now = _time.GetUtcNow();
        var recoveryDirectory = Path.Combine(RecoveryRoot, txid);
        var operations = BuildOperationRecords(root, plan.Operations);
        var record = new InstanceTransactionRecord(
            txid,
            instanceId,
            root.RootPath,
            plan.Reason,
            InstanceTransactionState.Planned,
            now,
            now,
            operations,
            [],
            0,
            RecoveryDirectory: recoveryDirectory,
            SchemaVersion: CurrentSchemaVersion,
            CorrelationId: plan.CorrelationId);

        try
        {
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            // Prepare: 记录前态/写后指纹，冻结来源文件，准备并校验恢复材料。
            record = record with { State = InstanceTransactionState.Preparing, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            record = await PrepareAsync(record, root, plan, recoveryDirectory, cancellationToken).ConfigureAwait(false);

            record = record with { State = InstanceTransactionState.Applying, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < record.Operations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = record.Operations[index];
                var full = root.Resolve(operation.RelativePath);
                var current = ComputeFingerprint(full);
                if (!ContentEquals(current, operation.Original ?? InstanceFileFingerprint.Missing))
                {
                    throw new InstanceTransactionPreconditionException(
                        $"目标在事务准备后被外部修改：{operation.RelativePath}");
                }

                ApplyOperation(operation, full, record.RecoveryDirectory!);
                var written = ComputeFingerprint(full);
                record = record with
                {
                    Operations = ReplaceOperation(record.Operations, index, operation with
                    {
                        Written = written,
                        Completed = true,
                    }),
                    CompletedOperations = index + 1,
                    UpdatedAt = _time.GetUtcNow(),
                };

                if ((index + 1) % JournalFlushInterval == 0)
                {
                    await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);
                }
            }

            record = record with { State = InstanceTransactionState.Verifying, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            foreach (var operation in record.Operations)
            {
                var full = root.Resolve(operation.RelativePath);
                var current = ComputeFingerprint(full);
                if (!ContentEquals(current, operation.ExpectedWritten ?? InstanceFileFingerprint.Missing))
                {
                    throw new IOException($"事务结果与预期写后指纹不一致：{operation.RelativePath}");
                }
            }

            record = record with { State = InstanceTransactionState.Committed, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);
            TryDeleteDirectory(recoveryDirectory);
            InvalidateInventoryCache(instanceId);
            _logger.LogInformation(
                "实例 {InstanceId} 事务 {TransactionId} 已提交：{Reason}，变更 {Count} 项。",
                instanceId,
                txid,
                plan.Reason,
                plan.Operations.Count);
            return Result<InstanceTransactionRecord>.Ok(record);
        }
        catch (OperationCanceledException)
        {
            var cancelled = await RollbackAsync(record, root, "用户取消", RecoveryResolution.Auto, CancellationToken.None)
                .ConfigureAwait(false);
            return Result<InstanceTransactionRecord>.Fail(
                "transaction.cancelled",
                "事务已取消。",
                cancelled.State.ToString());
        }
        catch (InstanceTransactionBackupException exception)
        {
            return Result<InstanceTransactionRecord>.Fail(
                "transaction.backup_failed",
                "准备恢复材料失败，未修改任何文件。",
                exception.InnerException?.Message ?? exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InstancePathException or InstanceTransactionPreconditionException
                                              or InvalidDataException)
        {
            var rolledBack = await RollbackAsync(record, root, exception.Message, RecoveryResolution.Auto, CancellationToken.None)
                .ConfigureAwait(false);
            return Result<InstanceTransactionRecord>.Fail(
                rolledBack.State == InstanceTransactionState.NeedsRecovery
                    ? "transaction.needs_recovery"
                    : exception is InstanceTransactionPreconditionException
                        ? "transaction.target_changed"
                        : "transaction.apply_failed",
                rolledBack.State == InstanceTransactionState.NeedsRecovery
                    ? "事务失败且无法自动回滚，需要人工恢复。"
                    : "事务执行失败，已回滚。",
                exception.Message);
        }
    }

    public async Task<Result<IReadOnlyList<InstanceTransactionRecord>>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        await ListAsync(instanceId, filter: null, cancellationToken).ConfigureAwait(false);

    public async Task<Result<IReadOnlyList<InstanceTransactionRecord>>> ListAsync(
        string instanceId,
        InstanceTransactionListFilter? filter,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<IReadOnlyList<InstanceTransactionRecord>>.Fail("transaction.invalid_instance", "实例 ID 不合法。");
        }

        var directory = Path.Combine(TransactionsRoot, instanceId);
        if (!Directory.Exists(directory))
        {
            return Result<IReadOnlyList<InstanceTransactionRecord>>.Ok([]);
        }

        var records = new List<InstanceTransactionRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var record = await TryReadJournalAsync(path, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                continue;
            }

            if (filter is { State: { } state } && record.State != state)
            {
                continue;
            }

            if (filter is { IncludeTerminal: false } && IsTerminal(record.State))
            {
                continue;
            }

            records.Add(record);
        }

        var limit = filter?.Limit is > 0 ? filter.Limit : 200;
        return Result<IReadOnlyList<InstanceTransactionRecord>>.Ok(
            records.OrderByDescending(item => item.CreatedAt).Take(limit).ToArray());
    }

    public async Task<Result<InstanceTransactionRecord>> GetAsync(
        string instanceId,
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId) || !IsValidTransactionId(transactionId))
        {
            return Result<InstanceTransactionRecord>.Fail("transaction.invalid_id", "实例或事务 ID 不合法。");
        }

        var path = GetJournalPath(instanceId, transactionId);
        if (!File.Exists(path))
        {
            return Result<InstanceTransactionRecord>.Fail("transaction.not_found", "事务不存在。");
        }

        var record = await TryReadJournalAsync(path, cancellationToken).ConfigureAwait(false);
        return record is null
            ? Result<InstanceTransactionRecord>.Fail("transaction.journal_corrupt", "事务日志损坏。")
            : Result<InstanceTransactionRecord>.Ok(record);
    }

    /// <summary>显式恢复一个未完成事务。默认只做回滚；无法完整恢复时进入 NeedsRecovery，不伪称成功。</summary>
    public Task<Result<InstanceTransactionRecord>> RecoverAsync(
        string instanceId,
        string transactionId,
        CancellationToken cancellationToken = default) =>
        RecoverAsync(instanceId, transactionId, RecoveryResolution.Auto, cancellationToken);

    public async Task<Result<InstanceTransactionRecord>> RecoverAsync(
        string instanceId,
        string transactionId,
        RecoveryResolution resolution,
        CancellationToken cancellationToken = default)
    {
        var loaded = await GetAsync(instanceId, transactionId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var record = loaded.Value!;
        if (IsTerminal(record.State))
        {
            return loaded;
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceTransactionRecord>.Fail("instance.not_found", "实例不存在。");
        }

        var root = new InstanceFileWorkspace(InstancePaths.GetGameDirectory(_paths, instance));
        if (!Directory.Exists(root.RootPath))
        {
            return Result<InstanceTransactionRecord>.Fail("transaction.root_missing", "实例游戏目录不存在。");
        }

        if (record.SchemaVersion is > CurrentSchemaVersion or < CurrentSchemaVersion)
        {
            var schemaLeaseResult = await _gate.AcquireAsync(instanceId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!schemaLeaseResult.IsSuccess)
            {
                return Result<InstanceTransactionRecord>.Fail(schemaLeaseResult.Error!);
            }

            await using var schemaLease = schemaLeaseResult.Value!;
            var legacy = await MarkNeedsRecoveryAsync(
                record,
                record.SchemaVersion > CurrentSchemaVersion
                    ? "transaction.unsupported_schema"
                    : "transaction.legacy_journal",
                record.SchemaVersion > CurrentSchemaVersion
                    ? "事务日志版本高于当前 ECL，禁止自动恢复。"
                    : "旧版事务日志缺少写后指纹，无法安全对账，需人工恢复。",
                CancellationToken.None).ConfigureAwait(false);
            return Result<InstanceTransactionRecord>.Ok(legacy);
        }

        var leaseResult = await _gate.AcquireAsync(instanceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!leaseResult.IsSuccess)
        {
            return Result<InstanceTransactionRecord>.Fail(leaseResult.Error!);
        }

        await using var lease = leaseResult.Value!;
        var refreshed = await TryReadJournalAsync(GetJournalPath(instanceId, transactionId), cancellationToken)
            .ConfigureAwait(false);
        if (refreshed is null)
        {
            return Result<InstanceTransactionRecord>.Fail("transaction.journal_corrupt", "事务日志损坏。");
        }

        if (IsTerminal(refreshed.State))
        {
            return Result<InstanceTransactionRecord>.Ok(refreshed);
        }

        var recovered = await RecoverCoreAsync(refreshed, root, resolution, cancellationToken).ConfigureAwait(false);
        return Result<InstanceTransactionRecord>.Ok(recovered);
    }

    public async Task<Result<RecoverySweepReport>> RecoverPendingAsync(
        string? instanceId = null,
        CancellationToken cancellationToken = default)
    {
        var instanceIds = instanceId is null
            ? (Directory.Exists(TransactionsRoot)
                ? Directory.EnumerateDirectories(TransactionsRoot)
                    .Select(Path.GetFileName)
                    .Where(id => id is not null && AppPaths.IsValidInstanceId(id))
                    .Cast<string>()
                    .ToArray()
                : [])
            : [instanceId];

        var scanned = 0;
        var rolledBack = 0;
        var needsRecovery = 0;
        var skipped = 0;
        var problems = new List<string>();
        foreach (var id in instanceIds)
        {
            var list = await ListAsync(id, new InstanceTransactionListFilter(IncludeTerminal: false), cancellationToken)
                .ConfigureAwait(false);
            if (!list.IsSuccess)
            {
                problems.Add($"{id}: {list.Error!.Code}");
                continue;
            }

            foreach (var record in list.Value!)
            {
                scanned++;
                try
                {
                    var result = await RecoverAsync(id, record.TransactionId, RecoveryResolution.Auto, cancellationToken)
                        .ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        skipped++;
                        problems.Add($"{id}/{record.TransactionId}: {result.Error!.Code}");
                        continue;
                    }

                    if (result.Value!.State == InstanceTransactionState.NeedsRecovery)
                    {
                        needsRecovery++;
                        problems.Add($"{id}/{record.TransactionId}: needs recovery");
                    }
                    else
                    {
                        rolledBack++;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                      or OperationCanceledException)
                {
                    skipped++;
                    problems.Add($"{id}/{record.TransactionId}: {exception.Message}");
                }
            }
        }

        return Result<RecoverySweepReport>.Ok(
            new RecoverySweepReport(scanned, rolledBack, needsRecovery, skipped, problems.Take(100).ToArray()));
    }

    public async Task<Result<InstanceActivityState>> GetActivityStateAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceActivityState>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceActivityState>.Ok(InstanceActivityState.Missing);
        }

        var identity = await _identity.VerifyAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (identity.IsSuccess && identity.Value!.Status == InstanceRegistrationStatus.Conflict)
        {
            return Result<InstanceActivityState>.Ok(InstanceActivityState.IdentityConflict);
        }

        var list = await ListAsync(instanceId, new InstanceTransactionListFilter(IncludeTerminal: false), cancellationToken)
            .ConfigureAwait(false);
        if (!list.IsSuccess)
        {
            return Result<InstanceActivityState>.Fail(list.Error!);
        }

        if (list.Value!.Any(item => item.State == InstanceTransactionState.NeedsRecovery))
        {
            return Result<InstanceActivityState>.Ok(InstanceActivityState.NeedsRecovery);
        }

        if (list.Value!.Any(item => item.State == InstanceTransactionState.RollingBack))
        {
            return Result<InstanceActivityState>.Ok(InstanceActivityState.Recovering);
        }

        if (list.Value!.Count > 0 || _gate.IsWriteHeld(instanceId))
        {
            return Result<InstanceActivityState>.Ok(InstanceActivityState.Writing);
        }

        return Result<InstanceActivityState>.Ok(InstanceActivityState.Idle);
    }

    private Result ValidatePlan(InstanceFileWorkspace root, InstanceChangePlan plan)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in plan.Operations)
        {
            string target;
            string full;
            try
            {
                target = root.NormalizeRelative(operation.RelativePath);
                full = root.Resolve(operation.RelativePath);
            }
            catch (InstancePathException exception)
            {
                return Result.Fail("transaction.invalid_path", "变更目标超出实例边界。", exception.Message);
            }

            if (IsProtectedTarget(target))
            {
                return Result.Fail(
                    "transaction.protected_target",
                    "目标属于受保护的引擎元数据，不能通过通用事务修改。",
                    target);
            }

            if (!seen.Add(target))
            {
                return Result.Fail("transaction.duplicate_target", "同一事务不能重复写入同一目标。", target);
            }

            if (Directory.Exists(full) && !File.Exists(full))
            {
                if (operation.Kind == InstanceChangeKind.WriteFile)
                {
                    return Result.Fail("transaction.target_directory", "写入目标已经是目录。", target);
                }

                if (operation.Kind == InstanceChangeKind.DeleteFile)
                {
                    return Result.Fail(
                        "transaction.directory_delete_unsupported",
                        "事务不直接删除目录；请先枚举并删除目录内文件。",
                        target);
                }
            }

            if (operation.Kind == InstanceChangeKind.WriteFile && operation.Content is null)
            {
                if (string.IsNullOrWhiteSpace(operation.SourcePath) || !File.Exists(operation.SourcePath))
                {
                    return Result.Fail("transaction.source_missing", "写入操作的来源文件不存在。", target);
                }
            }
        }

        return Result.Ok();
    }

    private IReadOnlyList<InstanceTransactionOperationRecord> BuildOperationRecords(
        InstanceFileWorkspace root,
        IReadOnlyList<InstanceChangeOperation> operations)
    {
        var records = new List<InstanceTransactionOperationRecord>(operations.Count);
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            records.Add(new InstanceTransactionOperationRecord(
                index,
                operation.Kind,
                root.NormalizeRelative(operation.RelativePath)));
        }

        return records;
    }

    private async Task<InstanceTransactionRecord> PrepareAsync(
        InstanceTransactionRecord record,
        InstanceFileWorkspace root,
        InstanceChangePlan plan,
        string recoveryDirectory,
        CancellationToken cancellationToken)
    {
        var operations = new List<InstanceTransactionOperationRecord>(record.Operations.Count);
        var preconditions = new List<InstancePrecondition>();
        Directory.CreateDirectory(Path.Combine(recoveryDirectory, "blobs"));
        try
        {
            for (var index = 0; index < record.Operations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operationRecord = record.Operations[index];
                var operation = plan.Operations[index];
                var full = root.Resolve(operationRecord.RelativePath);
                var original = ComputeFingerprint(full);
                InstanceBackupRecord? backup = null;
                if (original.Exists && !original.IsDirectory)
                {
                    backup = await CreateBackupAsync(index, full, original, recoveryDirectory, cancellationToken)
                        .ConfigureAwait(false);
                }

                var expected = await ExpectedWrittenAsync(
                    index,
                    operation,
                    recoveryDirectory,
                    cancellationToken).ConfigureAwait(false);
                operations.Add(operationRecord with
                {
                    Original = original,
                    ExpectedWritten = expected,
                    Backup = backup,
                });
                preconditions.Add(new InstancePrecondition(operationRecord.RelativePath, original));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or InstancePathException)
        {
            var failed = await MarkFailedAsync(
                record with { State = InstanceTransactionState.Preparing },
                "transaction.backup_failed",
                $"准备恢复材料失败：{exception.Message}",
                CancellationToken.None).ConfigureAwait(false);
            throw new InstanceTransactionBackupException(failed.ErrorMessage ?? "准备恢复材料失败。", exception);
        }

        record = record with
        {
            Operations = operations,
            Preconditions = preconditions,
            UpdatedAt = _time.GetUtcNow(),
        };
        await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    private async Task<InstanceBackupRecord> CreateBackupAsync(
        int index,
        string source,
        InstanceFileFingerprint original,
        string recoveryDirectory,
        CancellationToken cancellationToken)
    {
        var hash = original.Sha256 ?? throw new InvalidDataException($"无法读取前态哈希：{source}");
        var blob = Path.Combine("blobs", $"{index}-{hash[..16]}.bin");
        var destination = Path.Combine(recoveryDirectory, blob);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        var copied = ComputeFingerprint(destination);
        if (!copied.Exists || copied.IsDirectory ||
            copied.Length != original.Length ||
            !string.Equals(copied.Sha256, hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"恢复材料复制后不一致：{source}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return new InstanceBackupRecord(blob.Replace('\\', '/'), copied.Length, copied.Sha256!);
    }

    private static async Task<InstanceFileFingerprint> ExpectedWrittenAsync(
        int index,
        InstanceChangeOperation operation,
        string recoveryDirectory,
        CancellationToken cancellationToken)
    {
        switch (operation.Kind)
        {
            case InstanceChangeKind.DeleteFile:
                return InstanceFileFingerprint.Missing;
            case InstanceChangeKind.CreateDirectory:
                return new InstanceFileFingerprint(true, true, 0, 0, null);
            default:
                {
                    // 冻结来源（内存内容或外部来源文件）：Apply 只从 staging 读取。
                    var staging = Path.Combine(recoveryDirectory, "blobs", $"{index}-source.bin");
                    Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                    if (operation.Content is not null)
                    {
                        await File.WriteAllBytesAsync(staging, operation.Content, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await using var input = new FileStream(
                            operation.SourcePath!,
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

                    var copied = ComputeFingerprint(staging);
                    if (!copied.Exists || copied.IsDirectory)
                    {
                        throw new InvalidDataException("事务来源文件无法暂存。");
                    }

                    EnsureExpectedHash(operation, copied.Sha256!);
                    return copied;
                }
        }
    }

    private static void EnsureExpectedHash(InstanceChangeOperation operation, string actual)
    {
        if (!string.IsNullOrWhiteSpace(operation.ExpectedSha256) &&
            !string.Equals(operation.ExpectedSha256, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"来源内容哈希与期望不符：{operation.RelativePath}");
        }
    }

    private async Task<Result<InstanceTransactionRecord>> ReplayAsync(
        string instanceId,
        InstanceTransactionRecord record,
        InstanceFileWorkspace root,
        CancellationToken cancellationToken)
    {
        if (record.InstanceId != instanceId)
        {
            return Result<InstanceTransactionRecord>.Fail("transaction.id_conflict", "事务 ID 已被其他实例使用。");
        }

        if (record.SchemaVersion < CurrentSchemaVersion && !IsTerminal(record.State))
        {
            var legacy = await MarkNeedsRecoveryAsync(
                record,
                "transaction.legacy_journal",
                "旧版事务日志缺少写后指纹，无法安全对账。",
                cancellationToken).ConfigureAwait(false);
            return Result<InstanceTransactionRecord>.Ok(legacy);
        }

        switch (record.State)
        {
            case InstanceTransactionState.Committed:
                foreach (var operation in record.Operations)
                {
                    var current = ComputeFingerprint(root.Resolve(operation.RelativePath));
                    var written = operation.Written ?? operation.ExpectedWritten ?? InstanceFileFingerprint.Missing;
                    if (!ContentEquals(current, written))
                    {
                        return Result<InstanceTransactionRecord>.Fail(
                            "transaction.target_changed_after_commit",
                            "事务已提交，但目标内容在提交后发生了变化。",
                            operation.RelativePath);
                    }
                }

                return Result<InstanceTransactionRecord>.Ok(record);
            case InstanceTransactionState.RolledBack:
            case InstanceTransactionState.Resolved:
                return Result<InstanceTransactionRecord>.Ok(record);
            case InstanceTransactionState.Failed:
            case InstanceTransactionState.Cancelled:
                return Result<InstanceTransactionRecord>.Fail(
                    "transaction.duplicate",
                    "该事务已终止，重试必须使用新的事务 ID。",
                    record.State.ToString());
            default:
                return Result<InstanceTransactionRecord>.Fail(
                    "transaction.duplicate",
                    "同一事务 ID 尚未完成，拒绝重复执行。",
                    record.State.ToString());
        }
    }

    private async Task<InstanceTransactionRecord> RecoverCoreAsync(
        InstanceTransactionRecord record,
        InstanceFileWorkspace root,
        RecoveryResolution resolution,
        CancellationToken cancellationToken)
    {
        // 在 Prepare 写出指纹之前崩溃：没有记录任何前态/写后指纹，也没有 Apply 写入。
        if ((record.State is InstanceTransactionState.Planned or InstanceTransactionState.Preparing) &&
            record.Operations.All(operation => operation.Original is null))
        {
            var noopPrepare = record with
            {
                State = InstanceTransactionState.RolledBack,
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(noopPrepare, CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(record.RecoveryDirectory);
            return noopPrepare;
        }

        var classifications = record.Operations
            .Select(operation => (Operation: operation, Current: ComputeFingerprint(root.Resolve(operation.RelativePath))))
            .ToArray();

        if (classifications.Any(item => item.Current is null))
        {
            return await MarkNeedsRecoveryAsync(record, "transaction.recovery_failed", "无法读取目标指纹。", cancellationToken)
                .ConfigureAwait(false);
        }

        var states = classifications
            .Select(item => Classify(item.Operation, item.Current!))
            .ToArray();
        if (states.Any(state => state == OperationRecoveryState.Other))
        {
            return await MarkNeedsRecoveryAsync(
                record,
                "transaction.target_changed",
                "目标既不是前态也不是 ECL 写后态，禁止自动恢复。",
                cancellationToken).ConfigureAwait(false);
        }

        if (resolution == RecoveryResolution.AcceptCurrent)
        {
            var resolved = record with
            {
                State = InstanceTransactionState.Resolved,
                Resolution = "AcceptedCurrent",
                ResolvedAt = _time.GetUtcNow(),
                UpdatedAt = _time.GetUtcNow(),
                Operations = record.Operations
                    .Select((operation, index) => operation with { Written = classifications[index].Current })
                    .ToArray(),
            };
            await WriteJournalAsync(resolved, CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(record.RecoveryDirectory);
            InvalidateInventoryCache(record.InstanceId);
            return resolved;
        }

        if (resolution == RecoveryResolution.MarkRolledBack)
        {
            if (states.Any(state => state == OperationRecoveryState.Applied))
            {
                return await MarkNeedsRecoveryAsync(
                    record,
                    "transaction.recovery_failed",
                    "目标仍包含 ECL 写入，不能标记为已回滚。",
                    cancellationToken).ConfigureAwait(false);
            }

            var marked = record with
            {
                State = InstanceTransactionState.RolledBack,
                Resolution = "MarkedRolledBack",
                ResolvedAt = _time.GetUtcNow(),
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(marked, CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(record.RecoveryDirectory);
            InvalidateInventoryCache(record.InstanceId);
            return marked;
        }

        if (!states.Any(state => state == OperationRecoveryState.Applied))
        {
            var noop = record with
            {
                State = InstanceTransactionState.RolledBack,
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(noop, CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(record.RecoveryDirectory);
            InvalidateInventoryCache(record.InstanceId);
            return noop;
        }

        return await RollbackAsync(record, root, "自动恢复", RecoveryResolution.Auto, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<InstanceTransactionRecord> RollbackAsync(
        InstanceTransactionRecord record,
        InstanceFileWorkspace root,
        string reason,
        RecoveryResolution resolution,
        CancellationToken cancellationToken)
    {
        record = record with
        {
            State = InstanceTransactionState.RollingBack,
            UpdatedAt = _time.GetUtcNow(),
            ErrorMessage = reason,
        };
        await WriteJournalAsync(record, CancellationToken.None).ConfigureAwait(false);

        // Prepare 尚未写出任何前态指纹：说明还没有执行任何 Apply 写入。
        if (record.Operations.All(operation => operation.Original is null))
        {
            var unprepared = record with
            {
                State = InstanceTransactionState.RolledBack,
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(unprepared, CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(record.RecoveryDirectory);
            return unprepared;
        }

        try
        {
            for (var index = record.Operations.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = record.Operations[index];
                var target = root.Resolve(operation.RelativePath);
                var current = ComputeFingerprint(target);
                var state = Classify(operation, current);
                if (state == OperationRecoveryState.Other)
                {
                    return await MarkNeedsRecoveryAsync(
                        record,
                        "transaction.target_changed",
                        $"目标已不是 ECL 写后内容：{operation.RelativePath}",
                        cancellationToken).ConfigureAwait(false);
                }

                if (state == OperationRecoveryState.NotApplied)
                {
                    continue;
                }

                switch (operation.Kind)
                {
                    case InstanceChangeKind.WriteFile:
                        await RestoreBackupAsync(record, operation, target, cancellationToken).ConfigureAwait(false);
                        break;
                    case InstanceChangeKind.DeleteFile:
                        await RestoreBackupAsync(record, operation, target, cancellationToken).ConfigureAwait(false);
                        break;
                    case InstanceChangeKind.CreateDirectory:
                        if (Directory.Exists(target) && !File.Exists(target) &&
                            !Directory.EnumerateFileSystemEntries(target).Any())
                        {
                            Directory.Delete(target);
                        }

                        break;
                }
            }

            var rolledBack = record with
            {
                State = InstanceTransactionState.RolledBack,
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(rolledBack, CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(record.RecoveryDirectory);
            InvalidateInventoryCache(record.InstanceId);
            return rolledBack;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InstancePathException or InvalidDataException)
        {
            _logger.LogError(
                exception,
                "实例 {InstanceId} 事务 {TransactionId} 回滚失败。",
                record.InstanceId,
                record.TransactionId);
            return await MarkNeedsRecoveryAsync(
                record,
                "transaction.recovery_failed",
                $"回滚失败，需要人工恢复：{exception.Message}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RestoreBackupAsync(
        InstanceTransactionRecord record,
        InstanceTransactionOperationRecord operation,
        string target,
        CancellationToken cancellationToken)
    {
        var original = operation.Original ?? InstanceFileFingerprint.Missing;
        if (!original.Exists)
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            return;
        }

        if (operation.Backup is null)
        {
            throw new InvalidDataException($"缺少备份记录：{operation.RelativePath}");
        }

        var backupPath = Path.Combine(record.RecoveryDirectory!, operation.Backup.Blob);
        if (!File.Exists(backupPath))
        {
            throw new InvalidDataException($"恢复材料缺失：{operation.Backup.Blob}");
        }

        var backup = ComputeFingerprint(backupPath);
        if (!backup.Exists || backup.IsDirectory ||
            backup.Length != operation.Backup.Length ||
            !string.Equals(backup.Sha256, operation.Backup.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"恢复材料校验失败：{operation.Backup.Blob}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = $"{target}.{record.TransactionId}.rollback.tmp";
        File.Copy(backupPath, temporary, overwrite: true);
        File.Move(temporary, target, overwrite: true);
        if (original.LastWriteUtcTicks > 0)
        {
            try
            {
                File.SetLastWriteTimeUtc(target, new DateTime(original.LastWriteUtcTicks, DateTimeKind.Utc));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 时间戳不影响内容恢复。
            }
        }

        var restored = ComputeFingerprint(target);
        if (!ContentEquals(restored, original))
        {
            throw new InvalidDataException($"恢复后内容与前态不一致：{operation.RelativePath}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private enum OperationRecoveryState
    {
        NotApplied,
        Applied,
        Other,
    }

    private static OperationRecoveryState Classify(
        InstanceTransactionOperationRecord operation,
        InstanceFileFingerprint current)
    {
        var original = operation.Original ?? InstanceFileFingerprint.Missing;
        if (ContentEquals(current, original))
        {
            return OperationRecoveryState.NotApplied;
        }

        var written = operation.Written ?? operation.ExpectedWritten ?? InstanceFileFingerprint.Missing;
        if (ContentEquals(current, written))
        {
            return OperationRecoveryState.Applied;
        }

        return OperationRecoveryState.Other;
    }

    private static void ApplyOperation(
        InstanceTransactionOperationRecord operation,
        string fullPath,
        string recoveryDirectory)
    {
        switch (operation.Kind)
        {
            case InstanceChangeKind.CreateDirectory:
                Directory.CreateDirectory(fullPath);
                break;
            case InstanceChangeKind.DeleteFile:
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }

                break;
            case InstanceChangeKind.WriteFile:
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                var temporary = $"{fullPath}.{Guid.NewGuid():N}.txn.tmp";
                try
                {
                    var staged = Path.Combine(recoveryDirectory, "blobs", $"{operation.Index}-source.bin");
                    if (!File.Exists(staged))
                    {
                        throw new InvalidDataException("事务来源 staging 缺失。");
                    }

                    File.Copy(staged, temporary, overwrite: true);
                    File.Move(temporary, fullPath, overwrite: true);
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
                            // 临时文件残留不影响已提交内容。
                        }
                    }
                }

                break;
        }
    }

    private async Task<InstanceTransactionRecord> MarkFailedAsync(
        InstanceTransactionRecord record,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        var state = record.State == InstanceTransactionState.NeedsRecovery
            ? InstanceTransactionState.NeedsRecovery
            : InstanceTransactionState.Failed;
        var updated = record with
        {
            State = state,
            UpdatedAt = _time.GetUtcNow(),
            ErrorCode = errorCode,
            ErrorMessage = message,
        };
        await WriteJournalAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<InstanceTransactionRecord> MarkNeedsRecoveryAsync(
        InstanceTransactionRecord record,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        var updated = record with
        {
            State = InstanceTransactionState.NeedsRecovery,
            UpdatedAt = _time.GetUtcNow(),
            ErrorCode = errorCode,
            ErrorMessage = message,
        };
        await WriteJournalAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static IReadOnlyList<InstanceTransactionOperationRecord> ReplaceOperation(
        IReadOnlyList<InstanceTransactionOperationRecord> operations,
        int index,
        InstanceTransactionOperationRecord replacement)
    {
        var copy = operations.ToArray();
        copy[index] = replacement;
        return copy;
    }

    private static InstanceFileFingerprint ComputeFingerprint(string fullPath)
    {
        if (Directory.Exists(fullPath))
        {
            var info = new DirectoryInfo(fullPath);
            return new InstanceFileFingerprint(true, true, 0, info.LastWriteTimeUtc.Ticks, null);
        }

        if (!File.Exists(fullPath))
        {
            return InstanceFileFingerprint.Missing;
        }

        var file = new FileInfo(fullPath);
        return new InstanceFileFingerprint(
            true,
            false,
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            ComputeSha256(fullPath));
    }

    /// <summary>内容一致性：存在性、类型、长度与哈希；不比较修改时间（外部 touch 不应导致误判）。</summary>
    private static bool ContentEquals(InstanceFileFingerprint left, InstanceFileFingerprint right)
    {
        if (left.Exists != right.Exists || left.IsDirectory != right.IsDirectory)
        {
            return false;
        }

        if (!left.Exists)
        {
            return true;
        }

        if (left.IsDirectory)
        {
            return true;
        }

        return left.Length == right.Length &&
               string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            useAsync: false);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private async Task<Result<InstanceTransactionRecord?>> TryReadOrFailAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Result<InstanceTransactionRecord?>.Ok(null);
        }

        var record = await TryReadJournalAsync(path, cancellationToken).ConfigureAwait(false);
        return record is null
            ? Result<InstanceTransactionRecord?>.Fail("transaction.journal_corrupt", "已有事务日志损坏，拒绝覆盖。")
            : Result<InstanceTransactionRecord?>.Ok(record);
    }

    private async Task WriteJournalAsync(InstanceTransactionRecord record, CancellationToken cancellationToken)
    {
        var path = GetJournalPath(record.InstanceId, record.TransactionId);
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

    private static async Task<InstanceTransactionRecord?> TryReadJournalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var record = JsonSerializer.Deserialize<InstanceTransactionRecord>(json, EclJson.Default);
            if (record is null)
            {
                return null;
            }

            // v1 journal 没有索引/指纹字段；保留读取，由恢复流程保守处理。
            if (record.SchemaVersion == 0)
            {
                record = record with { SchemaVersion = 1 };
            }

            return record;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string GetJournalPath(string instanceId, string transactionId) =>
        Path.Combine(TransactionsRoot, instanceId, $"{transactionId}.json");

    private static bool IsTerminal(InstanceTransactionState state) => state is
        InstanceTransactionState.Committed or
        InstanceTransactionState.RolledBack or
        InstanceTransactionState.Failed or
        InstanceTransactionState.Cancelled or
        InstanceTransactionState.Resolved;

    private void InvalidateInventoryCache(string instanceId)
    {
        try
        {
            var path = Path.Combine(_paths.CacheDirectory, "instance-engine", "inventory", $"{instanceId}.json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "删除实例 {InstanceId} 的 Inventory 缓存失败。", instanceId);
        }
    }

    private static bool IsProtectedTarget(string relative) =>
        relative.Equals(Path.Combine(".ecl", "instance.json"), StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith(Path.Combine(".ecl", "instance.json") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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

    private sealed class InstanceTransactionPreconditionException(string message) : Exception(message);

    private sealed class InstanceTransactionBackupException(string message, Exception innerException)
        : Exception(message, innerException);
}
