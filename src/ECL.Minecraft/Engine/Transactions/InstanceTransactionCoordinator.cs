// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
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
}

public enum InstanceChangeKind
{
    WriteFile,
    DeleteFile,
    CreateDirectory,
}

/// <summary>
/// 一个受控变更。WriteFile 可以携带内存内容或外部来源文件（例如快照对象），
/// 但两者都不写入事务日志；日志只记录目标、阶段与前态指纹。
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
    string Reason);

public sealed record InstanceFileFingerprint(
    bool Exists,
    bool IsDirectory,
    long Length,
    long LastWriteUtcTicks,
    string? Sha256);

public sealed record InstancePrecondition(string RelativePath, InstanceFileFingerprint Fingerprint);

public sealed record InstanceTransactionOperationRecord(
    InstanceChangeKind Kind,
    string RelativePath,
    bool Completed);

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
    string? RecoveryDirectory = null);

/// <summary>
/// 实例级写事务协调器（ADR 0047）。首期采用实例级串行化：一个实例同时只允许一个事务进入 Apply。
/// 事务日志、前态备份与阶段状态持久化，失败时尝试回滚；无法证明恢复成功时进入 NeedsRecovery。
/// </summary>
public sealed class InstanceTransactionCoordinator
{
    private const int JournalFlushInterval = 25;

    private readonly IInstanceService _instances;
    private readonly InstanceIdentityService _identity;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceTransactionCoordinator> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public InstanceTransactionCoordinator(
        IInstanceService instances,
        InstanceIdentityService identity,
        IAppPaths paths,
        ILogger<InstanceTransactionCoordinator>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _identity = identity;
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceTransactionCoordinator>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string TransactionsRoot => Path.Combine(_paths.ConfigDirectory, "instance-engine", "transactions");

    public string RecoveryRoot => Path.Combine(_paths.CacheDirectory, "instance-engine", "recovery");

    public async Task<Result<InstanceTransactionRecord>> ExecuteAsync(
        string instanceId,
        InstanceChangePlan plan,
        string? transactionId = null,
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

        // 先验证所有目标，避免执行到一半才发现越界路径。
        foreach (var operation in plan.Operations)
        {
            try
            {
                var target = root.NormalizeRelative(operation.RelativePath);
                var full = root.Resolve(operation.RelativePath);
                if (IsProtectedTarget(target))
                {
                    return Result<InstanceTransactionRecord>.Fail(
                        "transaction.protected_target",
                        "目标属于受保护的引擎元数据，不能通过通用事务修改。",
                        target);
                }

                if (operation.Kind == InstanceChangeKind.WriteFile &&
                    operation.Content is null &&
                    (string.IsNullOrWhiteSpace(operation.SourcePath) || !File.Exists(operation.SourcePath)))
                {
                    return Result<InstanceTransactionRecord>.Fail(
                        "transaction.source_missing",
                        "写入操作的来源文件不存在。",
                        target);
                }

                if (operation.Kind == InstanceChangeKind.DeleteFile &&
                    Directory.Exists(full) &&
                    !File.Exists(full))
                {
                    return Result<InstanceTransactionRecord>.Fail(
                        "transaction.directory_delete_unsupported",
                        "事务不直接删除目录；请先枚举并删除目录内文件。",
                        target);
                }
            }
            catch (InstancePathException exception)
            {
                return Result<InstanceTransactionRecord>.Fail(
                    "transaction.invalid_path",
                    "变更目标超出实例边界。",
                    exception.Message);
            }
        }

        var txid = string.IsNullOrWhiteSpace(transactionId)
            ? $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(8).ToLowerInvariant()}"
            : transactionId.Trim();
        if (!IsValidTransactionId(txid))
        {
            return Result<InstanceTransactionRecord>.Fail("transaction.invalid_id", "事务 ID 不合法。");
        }

        var journalPath = GetJournalPath(instanceId, txid);
        if (File.Exists(journalPath))
        {
            var existing = await TryReadJournalAsync(journalPath, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return Result<InstanceTransactionRecord>.Fail("transaction.journal_corrupt", "已有事务日志损坏，拒绝覆盖。");
            }

            // 幂等：同一事务 ID 的重复提交返回已有结果，不重复执行。
            if (existing.InstanceId != instanceId)
            {
                return Result<InstanceTransactionRecord>.Fail("transaction.id_conflict", "事务 ID 已被其他实例使用。");
            }

            return existing.State is InstanceTransactionState.Committed or InstanceTransactionState.RolledBack
                ? Result<InstanceTransactionRecord>.Ok(existing)
                : Result<InstanceTransactionRecord>.Fail(
                    "transaction.duplicate",
                    "同一事务 ID 尚未完成，拒绝重复执行。",
                    existing.State.ToString());
        }

        var semaphore = _locks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? processLock = null;
        try
        {
            processLock = TryAcquireProcessLock(instanceId);
            if (processLock is null)
            {
                return Result<InstanceTransactionRecord>.Fail(
                    "transaction.busy",
                    "另一个 ECL 进程正在修改该实例。");
            }

            var now = _time.GetUtcNow();
            var recoveryDirectory = Path.Combine(RecoveryRoot, txid);
            var record = new InstanceTransactionRecord(
                txid,
                instanceId,
                root.RootPath,
                plan.Reason,
                InstanceTransactionState.Planned,
                now,
                now,
                plan.Operations
                    .Select(operation => new InstanceTransactionOperationRecord(
                        operation.Kind,
                        root.NormalizeRelative(operation.RelativePath),
                        Completed: false))
                    .ToArray(),
                [],
                0,
                RecoveryDirectory: recoveryDirectory);

            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            // Prepare: 记录前态指纹并准备恢复材料。
            record = record with { State = InstanceTransactionState.Preparing, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            var preconditions = new List<InstancePrecondition>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var operation in plan.Operations)
            {
                var relative = root.NormalizeRelative(operation.RelativePath);
                if (!seen.Add(relative))
                {
                    continue;
                }

                var full = root.Resolve(relative);
                preconditions.Add(new InstancePrecondition(relative, ComputeFingerprint(full)));
            }

            record = record with { Preconditions = preconditions, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            try
            {
                Directory.CreateDirectory(recoveryDirectory);
                foreach (var precondition in preconditions.Where(item => item.Fingerprint.Exists && !item.Fingerprint.IsDirectory))
                {
                    var source = root.Resolve(precondition.RelativePath);
                    var destination = Path.Combine(
                        recoveryDirectory,
                        precondition.RelativePath.Replace(Path.DirectorySeparatorChar, '_'));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await MarkFailedAsync(
                    record,
                    "transaction.backup_failed",
                    $"准备恢复材料失败：{exception.Message}",
                    cancellationToken).ConfigureAwait(false);
                return Result<InstanceTransactionRecord>.Fail(
                    "transaction.backup_failed",
                    "准备恢复材料失败，未修改任何文件。",
                    exception.Message);
            }

            // Apply
            record = record with { State = InstanceTransactionState.Applying, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);

            try
            {
                for (var index = 0; index < plan.Operations.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var operation = plan.Operations[index];
                    var relative = root.NormalizeRelative(operation.RelativePath);
                    var full = root.Resolve(relative);

                    var expected = preconditions.First(item =>
                        string.Equals(item.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
                    var current = ComputeFingerprint(full);
                    if (!FingerprintsEqual(expected.Fingerprint, current))
                    {
                        throw new InstanceTransactionPreconditionException(
                            $"目标在事务准备后被外部修改：{relative}");
                    }

                    ApplyOperation(root, operation, full);
                    record = record with
                    {
                        Operations = record.Operations
                            .Select((item, operationIndex) => operationIndex == index
                                ? item with { Completed = true }
                                : item)
                            .ToArray(),
                        CompletedOperations = index + 1,
                        UpdatedAt = _time.GetUtcNow(),
                    };

                    if ((index + 1) % JournalFlushInterval == 0)
                    {
                        await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                var cancelled = await RollbackAsync(record, root, "用户取消", cancellationToken)
                    .ConfigureAwait(false);
                return Result<InstanceTransactionRecord>.Fail(
                    "transaction.cancelled",
                    "事务已取消。",
                    cancelled.State.ToString());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or InstancePathException or InstanceTransactionPreconditionException)
            {
                var rolledBack = await RollbackAsync(record, root, exception.Message, cancellationToken)
                    .ConfigureAwait(false);
                return Result<InstanceTransactionRecord>.Fail(
                    rolledBack.State == InstanceTransactionState.NeedsRecovery
                        ? "transaction.needs_recovery"
                        : "transaction.apply_failed",
                    rolledBack.State == InstanceTransactionState.NeedsRecovery
                        ? "事务失败且无法自动回滚，需要人工恢复。"
                        : "事务执行失败，已回滚。",
                    exception.Message);
            }

            // Verify
            record = record with { State = InstanceTransactionState.Verifying, UpdatedAt = _time.GetUtcNow() };
            await WriteJournalAsync(record, cancellationToken).ConfigureAwait(false);
            try
            {
                for (var index = 0; index < plan.Operations.Count; index++)
                {
                    var operation = plan.Operations[index];
                    var relative = root.NormalizeRelative(operation.RelativePath);
                    VerifyOperation(root, operation, relative);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or InstancePathException)
            {
                var rolledBack = await RollbackAsync(record, root, exception.Message, cancellationToken)
                    .ConfigureAwait(false);
                return Result<InstanceTransactionRecord>.Fail(
                    rolledBack.State == InstanceTransactionState.NeedsRecovery
                        ? "transaction.needs_recovery"
                        : "transaction.verify_failed",
                    "事务结果验证失败。",
                    exception.Message);
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
        finally
        {
            processLock?.Dispose();
            semaphore.Release();
        }
    }

    public async Task<Result<IReadOnlyList<InstanceTransactionRecord>>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(TransactionsRoot, instanceId);
        if (!Directory.Exists(directory))
        {
            return Result<IReadOnlyList<InstanceTransactionRecord>>.Ok([]);
        }

        var records = new List<InstanceTransactionRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var record = await TryReadJournalAsync(path, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return Result<IReadOnlyList<InstanceTransactionRecord>>.Ok(
            records.OrderByDescending(item => item.CreatedAt).ToArray());
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

    /// <summary>
    /// 显式恢复一个未完成事务。默认只做回滚；无法完整恢复时进入 NeedsRecovery，不伪称成功。
    /// </summary>
    public async Task<Result<InstanceTransactionRecord>> RecoverAsync(
        string instanceId,
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await GetAsync(instanceId, transactionId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var record = loaded.Value!;
        if (record.State is InstanceTransactionState.Committed or InstanceTransactionState.RolledBack)
        {
            return loaded;
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceTransactionRecord>.Fail("instance.not_found", "实例不存在。");
        }

        var root = new InstanceFileWorkspace(InstancePaths.GetGameDirectory(_paths, instance));
        var semaphore = _locks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? processLock = null;
        try
        {
            processLock = TryAcquireProcessLock(instanceId);
            if (processLock is null)
            {
                return Result<InstanceTransactionRecord>.Fail(
                    "transaction.busy",
                    "另一个 ECL 进程正在修改该实例，无法执行恢复。");
            }

            var rolledBack = await RollbackAsync(record, root, "用户请求恢复", cancellationToken).ConfigureAwait(false);
            return Result<InstanceTransactionRecord>.Ok(rolledBack);
        }
        finally
        {
            processLock?.Dispose();
            semaphore.Release();
        }
    }

    private async Task<InstanceTransactionRecord> RollbackAsync(
        InstanceTransactionRecord record,
        InstanceFileWorkspace root,
        string reason,
        CancellationToken cancellationToken)
    {
        record = record with
        {
            State = InstanceTransactionState.RollingBack,
            UpdatedAt = _time.GetUtcNow(),
            ErrorMessage = reason,
        };
        await WriteJournalAsync(record, CancellationToken.None).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(record.RecoveryDirectory) || !Directory.Exists(record.RecoveryDirectory))
        {
            return await MarkFailedAsync(record, "transaction.recovery_missing", "缺少恢复材料，无法自动回滚。", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            for (var index = record.Operations.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = record.Operations[index];
                if (!operation.Completed)
                {
                    continue;
                }

                var precondition = record.Preconditions.FirstOrDefault(item =>
                    string.Equals(item.RelativePath, operation.RelativePath, StringComparison.OrdinalIgnoreCase));
                if (precondition is null)
                {
                    continue;
                }

                var target = root.Resolve(operation.RelativePath);
                var backup = Path.Combine(
                    record.RecoveryDirectory,
                    operation.RelativePath.Replace(Path.DirectorySeparatorChar, '_'));
                if (precondition.Fingerprint.Exists && !precondition.Fingerprint.IsDirectory)
                {
                    if (!File.Exists(backup))
                    {
                        return await MarkFailedAsync(
                            record,
                            "transaction.recovery_missing",
                            $"恢复材料缺失：{operation.RelativePath}",
                            cancellationToken).ConfigureAwait(false);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temporary = $"{target}.{record.TransactionId}.rollback.tmp";
                    File.Copy(backup, temporary, overwrite: true);
                    File.Move(temporary, target, overwrite: true);
                }
                else if (operation.Kind == InstanceChangeKind.WriteFile && File.Exists(target))
                {
                    // 前态不存在：撤销新写入的文件。
                    File.Delete(target);
                }
            }

            record = record with
            {
                State = InstanceTransactionState.RolledBack,
                UpdatedAt = _time.GetUtcNow(),
            };
            await WriteJournalAsync(record, CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(record.RecoveryDirectory);
            InvalidateInventoryCache(record.InstanceId);
            return record;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InstancePathException)
        {
            _logger.LogError(
                exception,
                "实例 {InstanceId} 事务 {TransactionId} 回滚失败。",
                record.InstanceId,
                record.TransactionId);
            return await MarkFailedAsync(
                record with { State = InstanceTransactionState.NeedsRecovery },
                "transaction.recovery_failed",
                $"回滚失败，需要人工恢复：{exception.Message}",
                cancellationToken).ConfigureAwait(false);
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

    private static void ApplyOperation(InstanceFileWorkspace root, InstanceChangeOperation operation, string fullPath)
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
                    if (operation.Content is not null)
                    {
                        File.WriteAllBytes(temporary, operation.Content);
                    }
                    else
                    {
                        File.Copy(operation.SourcePath!, temporary, overwrite: true);
                    }

                    File.Move(temporary, fullPath, overwrite: true);
                    if (operation.LastWriteTime is { } lastWrite)
                    {
                        File.SetLastWriteTimeUtc(fullPath, lastWrite.UtcDateTime);
                    }
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

    private static void VerifyOperation(
        InstanceFileWorkspace root,
        InstanceChangeOperation operation,
        string relative)
    {
        var full = root.Resolve(relative);
        switch (operation.Kind)
        {
            case InstanceChangeKind.CreateDirectory when !Directory.Exists(full):
                throw new IOException($"目录未创建：{relative}");
            case InstanceChangeKind.DeleteFile when File.Exists(full):
                throw new IOException($"文件未被删除：{relative}");
            case InstanceChangeKind.WriteFile:
                if (!File.Exists(full))
                {
                    throw new IOException($"文件未写入：{relative}");
                }

                var expectedLength = operation.Content?.LongLength ??
                                     (operation.SourcePath is not null ? new FileInfo(operation.SourcePath).Length : (long?)null);
                if (expectedLength is not null && new FileInfo(full).Length != expectedLength)
                {
                    throw new IOException($"文件长度不符：{relative}");
                }

                if (!string.IsNullOrWhiteSpace(operation.ExpectedSha256) &&
                    !string.Equals(ComputeSha256(full), operation.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"文件内容哈希不符：{relative}");
                }

                break;
        }
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
            return new InstanceFileFingerprint(false, false, 0, 0, null);
        }

        var file = new FileInfo(fullPath);
        return new InstanceFileFingerprint(
            true,
            false,
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            ComputeSha256(fullPath));
    }

    private static bool FingerprintsEqual(InstanceFileFingerprint left, InstanceFileFingerprint right) =>
        left.Exists == right.Exists &&
        left.IsDirectory == right.IsDirectory &&
        // 目录时间会因同目录内其它目标变化而改变；文件使用内容哈希 + 长度判断。
        (left.IsDirectory || left.Length == right.Length &&
            left.LastWriteUtcTicks == right.LastWriteUtcTicks &&
            string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase));

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

    private FileStream? TryAcquireProcessLock(string instanceId)
    {
        var directory = Path.Combine(_paths.Root, "locks", "instance-engine");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{instanceId}.lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "无法获取实例 {InstanceId} 的跨进程写锁。", instanceId);
            return null;
        }
    }

    private async Task WriteJournalAsync(InstanceTransactionRecord record, CancellationToken cancellationToken)
    {
        var path = GetJournalPath(record.InstanceId, record.TransactionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(record, EclJson.Indented),
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

    private static async Task<InstanceTransactionRecord?> TryReadJournalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<InstanceTransactionRecord>(json, EclJson.Default);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string GetJournalPath(string instanceId, string transactionId) =>
        Path.Combine(TransactionsRoot, instanceId, $"{transactionId}.json");

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

    private static void TryDeleteDirectory(string path)
    {
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
}
