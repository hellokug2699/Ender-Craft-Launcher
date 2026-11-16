// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core.Json;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

/// <summary>
/// F-04 S2：v2 journal 指纹对账、tri-state 回滚与人工解决。
/// 测试直接构造非终态 journal，模拟“进程在 Apply 后崩溃”的磁盘状态。
/// </summary>
public sealed class InstanceTransactionRecoveryTests
{
    [Fact]
    public async Task CommitPersistsV2FingerprintsAndRemovesMaterials()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("V2Commit", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);

        var result = await transactions.ExecuteAsync(
            created.Id,
            new InstanceChangePlan(
                [InstanceChangeOperation.WriteText("config/new.toml", "value = 1\n")],
                "v2 commit"),
            transactionId: "tx-v2-commit");
        Assert.True(result.IsSuccess, result.Error?.ToString());

        var loaded = await transactions.GetAsync(created.Id, "tx-v2-commit");
        Assert.True(loaded.IsSuccess, loaded.Error?.ToString());
        var record = loaded.Value!;
        Assert.Equal(2, record.SchemaVersion);
        var operation = Assert.Single(record.Operations);
        Assert.NotNull(operation.Original);
        Assert.NotNull(operation.ExpectedWritten);
        Assert.NotNull(operation.Written);
        Assert.Null(operation.Backup);
        Assert.True(operation.Completed);
        Assert.NotNull(record.RecoveryDirectory);
        Assert.StartsWith(
            Path.Combine(environment.Paths.ConfigDirectory, "instance-engine", "recovery"),
            record.RecoveryDirectory!,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(record.RecoveryDirectory));
        Assert.Equal("value = 1\n", await File.ReadAllTextAsync(Path.Combine(game, "config", "new.toml")));
    }

    [Fact]
    public async Task AppliedOperationIsRolledBackFromVerifiedBackup()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("Recovery", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var target = Path.Combine(game, "data.txt");
        await File.WriteAllTextAsync(target, "applied-content");

        var txid = "tx-recover-applied";
        var (record, recoveryDirectory) = await WriteJournalAsync(
            environment,
            created.Id,
            txid,
            "applied-content",
            "original-content");

        var recovered = await transactions.RecoverAsync(created.Id, txid);
        Assert.True(recovered.IsSuccess, recovered.Error?.ToString());
        Assert.Equal(InstanceTransactionState.RolledBack, recovered.Value!.State);
        Assert.Equal("original-content", await File.ReadAllTextAsync(target));
        Assert.False(Directory.Exists(recoveryDirectory));
        _ = record;
    }

    [Fact]
    public async Task ExternallyChangedTargetEntersNeedsRecoveryWithoutOverwrite()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("ExternalChange", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var target = Path.Combine(game, "data.txt");
        await File.WriteAllTextAsync(target, "applied-content");

        var txid = "tx-recover-external";
        await WriteJournalAsync(environment, created.Id, txid, "applied-content", "original-content");

        // 恢复到前态之前，外部程序又改了一次：既不是前态也不是 ECL 写后态。
        await File.WriteAllTextAsync(target, "user-edited-content");
        var recovered = await transactions.RecoverAsync(created.Id, txid);

        Assert.True(recovered.IsSuccess, recovered.Error?.ToString());
        Assert.Equal(InstanceTransactionState.NeedsRecovery, recovered.Value!.State);
        Assert.Equal("transaction.target_changed", recovered.Value.ErrorCode);
        Assert.Equal("user-edited-content", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task CorruptBackupEntersNeedsRecovery()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("CorruptBackup", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var target = Path.Combine(game, "data.txt");
        await File.WriteAllTextAsync(target, "applied-content");

        var txid = "tx-recover-corrupt";
        var (record, _) = await WriteJournalAsync(
            environment,
            created.Id,
            txid,
            "applied-content",
            "original-content");
        var backupPath = Path.Combine(record.RecoveryDirectory!, record.Operations[0].Backup!.Blob);
        await File.WriteAllTextAsync(backupPath, "corrupted-backup");

        var recovered = await transactions.RecoverAsync(created.Id, txid);
        Assert.True(recovered.IsSuccess, recovered.Error?.ToString());
        Assert.Equal(InstanceTransactionState.NeedsRecovery, recovered.Value!.State);
        Assert.Equal("transaction.recovery_failed", recovered.Value.ErrorCode);
        Assert.Equal("applied-content", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task NotAppliedOperationMarksRolledBackWithoutBackupUse()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("NotApplied", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var target = Path.Combine(game, "data.txt");
        await File.WriteAllTextAsync(target, "original-content");

        var txid = "tx-recover-noop";
        await WriteJournalAsync(environment, created.Id, txid, "applied-content", "original-content");

        var recovered = await transactions.RecoverAsync(created.Id, txid);
        Assert.True(recovered.IsSuccess, recovered.Error?.ToString());
        Assert.Equal(InstanceTransactionState.RolledBack, recovered.Value!.State);
        Assert.Equal("original-content", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task LegacyNonTerminalJournalEntersNeedsRecovery()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("Legacy", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var target = Path.Combine(game, "data.txt");
        await File.WriteAllTextAsync(target, "current-content");

        var txid = "tx-legacy";
        var record = new InstanceTransactionRecord(
            txid,
            created.Id,
            game,
            "legacy",
            InstanceTransactionState.Applying,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new InstanceTransactionOperationRecord(
                    0,
                    InstanceChangeKind.WriteFile,
                    "data.txt",
                    Completed: true),
            ],
            [],
            1,
            SchemaVersion: 1);
        await WriteRecordAsync(environment, created.Id, record);

        var recovered = await transactions.RecoverAsync(created.Id, txid);
        Assert.True(recovered.IsSuccess, recovered.Error?.ToString());
        Assert.Equal(InstanceTransactionState.NeedsRecovery, recovered.Value!.State);
        Assert.Equal("transaction.legacy_journal", recovered.Value.ErrorCode);
        Assert.Equal("current-content", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task MarkRolledBackRejectsAppliedWrites()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("MarkRolledBack", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        await File.WriteAllTextAsync(Path.Combine(game, "data.txt"), "applied-content");

        var txid = "tx-mark-rolled-back";
        await WriteJournalAsync(environment, created.Id, txid, "applied-content", "original-content");

        var marked = await transactions.RecoverAsync(created.Id, txid, RecoveryResolution.MarkRolledBack);
        Assert.True(marked.IsSuccess, marked.Error?.ToString());
        Assert.Equal(InstanceTransactionState.NeedsRecovery, marked.Value!.State);
    }

    [Fact]
    public async Task AcceptCurrentResolvesWithoutOverwrite()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("AcceptCurrent", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var target = Path.Combine(game, "data.txt");
        await File.WriteAllTextAsync(target, "applied-content");

        var txid = "tx-accept-current";
        var (record, recoveryDirectory) = await WriteJournalAsync(
            environment,
            created.Id,
            txid,
            "applied-content",
            "original-content");

        var resolved = await transactions.RecoverAsync(created.Id, txid, RecoveryResolution.AcceptCurrent);
        Assert.True(resolved.IsSuccess, resolved.Error?.ToString());
        Assert.Equal(InstanceTransactionState.Resolved, resolved.Value!.State);
        Assert.Equal("AcceptedCurrent", resolved.Value.Resolution);
        Assert.Equal("applied-content", await File.ReadAllTextAsync(target));
        Assert.False(Directory.Exists(recoveryDirectory));
        _ = record;
    }

    [Fact]
    public async Task PreparingCrashWithoutFingerprintsIsRolledBackWithoutWrites()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("PrepareCrash", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var target = Path.Combine(game, "data.txt");
        await File.WriteAllTextAsync(target, "existing-content");

        var txid = "tx-prepare-crash";
        var recoveryDirectory = Path.Combine(
            environment.Paths.ConfigDirectory,
            "instance-engine",
            "recovery",
            "instances",
            txid);
        Directory.CreateDirectory(Path.Combine(recoveryDirectory, "blobs"));
        var record = new InstanceTransactionRecord(
            txid,
            created.Id,
            game,
            "prepare crash",
            InstanceTransactionState.Preparing,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [new InstanceTransactionOperationRecord(0, InstanceChangeKind.WriteFile, "data.txt")],
            [],
            0,
            RecoveryDirectory: recoveryDirectory,
            SchemaVersion: 2);
        await WriteRecordAsync(environment, created.Id, record);

        var recovered = await transactions.RecoverAsync(created.Id, txid);
        Assert.True(recovered.IsSuccess, recovered.Error?.ToString());
        Assert.Equal(InstanceTransactionState.RolledBack, recovered.Value!.State);
        Assert.Equal("existing-content", await File.ReadAllTextAsync(target));
        Assert.False(Directory.Exists(recoveryDirectory));
    }

    [Fact]
    public async Task ActivityStateReportsNeedsRecoveryAndWriting()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var gate = provider.GetRequiredService<IInstanceWriteGate>();
        var created = (await instances.CreateAsync("Activity", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        await File.WriteAllTextAsync(Path.Combine(game, "data.txt"), "applied-content");

        var idle = await transactions.GetActivityStateAsync(created.Id);
        Assert.True(idle.IsSuccess, idle.Error?.ToString());
        Assert.Equal(InstanceActivityState.Idle, idle.Value);

        var writeLease = await gate.AcquireAsync(created.Id);
        Assert.True(writeLease.IsSuccess, writeLease.Error?.ToString());
        var writing = await transactions.GetActivityStateAsync(created.Id);
        Assert.Equal(InstanceActivityState.Writing, writing.Value);
        await writeLease.Value!.DisposeAsync();

        await WriteJournalAsync(environment, created.Id, "tx-activity", "applied-content", "original-content");
        await File.WriteAllTextAsync(Path.Combine(game, "data.txt"), "user-edited-content");
        var recovered = await transactions.RecoverAsync(created.Id, "tx-activity");
        Assert.Equal(InstanceTransactionState.NeedsRecovery, recovered.Value!.State);
        var needsRecovery = await transactions.GetActivityStateAsync(created.Id);
        Assert.Equal(InstanceActivityState.NeedsRecovery, needsRecovery.Value);
    }

    private static async Task<(InstanceTransactionRecord Record, string RecoveryDirectory)> WriteJournalAsync(
        TestEnvironment environment,
        string instanceId,
        string transactionId,
        string currentContent,
        string originalContent)
    {
        var game = environment.Paths.GetInstanceGameDirectory(instanceId);
        var target = Path.Combine(game, "data.txt");
        var originalHash = Sha256(originalContent);
        var currentHash = Sha256(currentContent);
        var original = new InstanceFileFingerprint(
            true,
            false,
            Encoding.UTF8.GetByteCount(originalContent),
            DateTime.UtcNow.Ticks,
            originalHash);
        var written = new InstanceFileFingerprint(
            true,
            false,
            Encoding.UTF8.GetByteCount(currentContent),
            DateTime.UtcNow.Ticks,
            currentHash);
        var recoveryDirectory = Path.Combine(
            environment.Paths.ConfigDirectory,
            "instance-engine",
            "recovery",
            "instances",
            transactionId);
        var blob = $"blobs/0-{originalHash[..16]}.bin";
        var backupPath = Path.Combine(recoveryDirectory, blob);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        await File.WriteAllTextAsync(backupPath, originalContent);

        var operation = new InstanceTransactionOperationRecord(
            0,
            InstanceChangeKind.WriteFile,
            "data.txt",
            original,
            written,
            written,
            new InstanceBackupRecord(blob, original.Length, originalHash),
            Completed: true);
        var record = new InstanceTransactionRecord(
            transactionId,
            instanceId,
            game,
            "crash simulation",
            InstanceTransactionState.Applying,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [operation],
            [new InstancePrecondition("data.txt", original)],
            1,
            RecoveryDirectory: recoveryDirectory,
            SchemaVersion: 2);
        await WriteRecordAsync(environment, instanceId, record);
        _ = target;
        return (record, recoveryDirectory);
    }

    private static async Task WriteRecordAsync(
        TestEnvironment environment,
        string instanceId,
        InstanceTransactionRecord record)
    {
        var directory = Path.Combine(
            environment.Paths.ConfigDirectory,
            "instance-engine",
            "transactions",
            "instances",
            instanceId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, $"{record.TransactionId}.json"),
            JsonSerializer.Serialize(record, EclJson.Indented));
    }

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
