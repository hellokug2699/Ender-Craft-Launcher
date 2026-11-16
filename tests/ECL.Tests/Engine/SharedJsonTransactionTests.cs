// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core.Json;
using Ecl.Minecraft.Engine.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

/// <summary>
/// F-04 S4：共享版本 JSON 事务必须持久化 journal/备份、按 E2-1 处理外部修改，
/// 崩溃后能回滚到前态，且不覆盖外部内容。
/// </summary>
public sealed class SharedJsonTransactionTests
{
    [Fact]
    public async Task CommitReplacesSharedJsonAndRecordsOwnership()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var shared = provider.GetRequiredService<ISharedJsonTransactionService>();
        var target = CreateVersionJson(environment, "1.21.1", "{\"id\":\"old\"}");

        var result = await shared.ExecuteAsync(new SharedJsonTransactionPlan(
            [new SharedJsonWrite(SharedJsonTargetKind.VersionManifest, target, Encoding.UTF8.GetBytes("{\"id\":\"new\"}"))],
            "replace version"));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(SharedJsonTransactionState.Committed, result.Value!.State);
        Assert.Equal("{\"id\":\"new\"}", await File.ReadAllTextAsync(target));

        var ownership = await shared.GetOwnershipAsync(target);
        Assert.True(ownership.IsSuccess, ownership.Error?.ToString());
        Assert.Equal(Sha256("{\"id\":\"new\"}"), ownership.Value!.LastEclWrittenSha256);
        Assert.False(ownership.Value.IsExternallyModified);
        Assert.Contains(
            result.Value.TransactionId,
            ownership.Value.LastCommittedTransactionId);

        var loaded = await shared.GetAsync(result.Value.TransactionId);
        Assert.True(loaded.IsSuccess, loaded.Error?.ToString());
        Assert.Single(loaded.Value!.Targets);
        Assert.NotNull(loaded.Value.RecoveryDirectory);
        Assert.False(Directory.Exists(loaded.Value.RecoveryDirectory));
    }

    [Fact]
    public async Task ExternalModificationBlocksReplacementUntilAcknowledged()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var shared = provider.GetRequiredService<ISharedJsonTransactionService>();
        var target = CreateVersionJson(environment, "1.21.1", "{\"id\":\"old\"}");

        var first = await shared.ExecuteAsync(new SharedJsonTransactionPlan(
            [new SharedJsonWrite(SharedJsonTargetKind.VersionManifest, target, Encoding.UTF8.GetBytes("{\"id\":\"ecl\"}"))],
            "first"));
        Assert.True(first.IsSuccess, first.Error?.ToString());

        // 外部启动器修改了 ECL 写后的文件。
        await File.WriteAllTextAsync(target, "{\"id\":\"external\"}");
        var blocked = await shared.ExecuteAsync(new SharedJsonTransactionPlan(
            [new SharedJsonWrite(SharedJsonTargetKind.VersionManifest, target, Encoding.UTF8.GetBytes("{\"id\":\"second\"}"))],
            "second"));

        Assert.False(blocked.IsSuccess);
        Assert.Equal("shared.external_modification", blocked.Error?.Code);
        Assert.Equal("{\"id\":\"external\"}", await File.ReadAllTextAsync(target));

        var acknowledged = await shared.AcknowledgeExternalModificationAsync(
            target,
            Sha256("{\"id\":\"external\"}"),
            "user accepted external file");
        Assert.True(acknowledged.IsSuccess, acknowledged.Error?.ToString());

        var second = await shared.ExecuteAsync(new SharedJsonTransactionPlan(
            [new SharedJsonWrite(SharedJsonTargetKind.VersionManifest, target, Encoding.UTF8.GetBytes("{\"id\":\"second\"}"))],
            "second after ack"));
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal("{\"id\":\"second\"}", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task KeptRecoveryMaterialSupportsRevertAndConfirm()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var shared = provider.GetRequiredService<ISharedJsonTransactionService>();
        var target = CreateVersionJson(environment, "1.21.1", "{\"id\":\"old\"}");

        var committed = await shared.ExecuteAsync(new SharedJsonTransactionPlan(
            [new SharedJsonWrite(SharedJsonTargetKind.VersionManifest, target, Encoding.UTF8.GetBytes("{\"id\":\"new\"}"))],
            "keep material",
            KeepRecoveryOnCommit: true));
        Assert.True(committed.IsSuccess, committed.Error?.ToString());
        Assert.True(Directory.Exists(committed.Value!.RecoveryDirectory));

        var reverted = await shared.RevertAsync(committed.Value.TransactionId);
        Assert.True(reverted.IsSuccess, reverted.Error?.ToString());
        Assert.Equal(SharedJsonTransactionState.RolledBack, reverted.Value!.State);
        Assert.Equal("{\"id\":\"old\"}", await File.ReadAllTextAsync(target));
        Assert.False(Directory.Exists(committed.Value.RecoveryDirectory));

        var second = await shared.ExecuteAsync(new SharedJsonTransactionPlan(
            [new SharedJsonWrite(SharedJsonTargetKind.VersionManifest, target, Encoding.UTF8.GetBytes("{\"id\":\"second\"}"))],
            "confirm material",
            KeepRecoveryOnCommit: true));
        Assert.True(second.IsSuccess, second.Error?.ToString());
        var confirmed = await shared.ConfirmAsync(second.Value!.TransactionId);
        Assert.True(confirmed.IsSuccess, confirmed.Error?.ToString());
        Assert.False(Directory.Exists(second.Value.RecoveryDirectory));
    }

    [Fact]
    public async Task RecoverRestoresOriginalAfterCrash()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var shared = provider.GetRequiredService<ISharedJsonTransactionService>();
        var target = CreateVersionJson(environment, "1.21.1", "{\"id\":\"applied\"}");
        var transactionId = "shared-crash-1";
        var (record, recoveryDirectory) = await WriteCrashJournalAsync(
            environment,
            transactionId,
            target,
            "{\"id\":\"original\"}",
            "{\"id\":\"applied\"}");

        var recovered = await shared.RecoverAsync(transactionId);
        Assert.True(recovered.IsSuccess, recovered.Error?.ToString());
        Assert.Equal(SharedJsonTransactionState.RolledBack, recovered.Value!.State);
        Assert.Equal("{\"id\":\"original\"}", await File.ReadAllTextAsync(target));
        Assert.False(Directory.Exists(recoveryDirectory));
        _ = record;
    }

    [Fact]
    public async Task RecoverRefusesToOverwriteExternallyChangedTarget()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var shared = provider.GetRequiredService<ISharedJsonTransactionService>();
        var target = CreateVersionJson(environment, "1.21.1", "{\"id\":\"applied\"}");
        var transactionId = "shared-crash-2";
        await WriteCrashJournalAsync(
            environment,
            transactionId,
            target,
            "{\"id\":\"original\"}",
            "{\"id\":\"applied\"}");

        await File.WriteAllTextAsync(target, "{\"id\":\"user\"}");
        var recovered = await shared.RecoverAsync(transactionId);

        Assert.True(recovered.IsSuccess, recovered.Error?.ToString());
        Assert.Equal(SharedJsonTransactionState.NeedsRecovery, recovered.Value!.State);
        Assert.Equal("transaction.target_changed", recovered.Value.ErrorCode);
        Assert.Equal("{\"id\":\"user\"}", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task RejectsTargetsOutsideWhitelist()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var shared = provider.GetRequiredService<ISharedJsonTransactionService>();
        var outside = Path.Combine(environment.Root, "outside.json");
        await File.WriteAllTextAsync(outside, "{}");

        var result = await shared.ExecuteAsync(new SharedJsonTransactionPlan(
            [new SharedJsonWrite(SharedJsonTargetKind.VersionManifest, outside, Encoding.UTF8.GetBytes("{}"))],
            "invalid target"));

        Assert.False(result.IsSuccess);
        Assert.Equal("shared.target_invalid", result.Error?.Code);
        Assert.Equal("{}", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task OwnershipRebuildsFromJournalWhenStateFileIsLost()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var shared = provider.GetRequiredService<ISharedJsonTransactionService>();
        var target = CreateVersionJson(environment, "1.21.1", "{\"id\":\"old\"}");

        var committed = await shared.ExecuteAsync(new SharedJsonTransactionPlan(
            [new SharedJsonWrite(SharedJsonTargetKind.VersionManifest, target, Encoding.UTF8.GetBytes("{\"id\":\"new\"}"))],
            "rebuild"));
        Assert.True(committed.IsSuccess, committed.Error?.ToString());

        var statePath = Path.Combine(environment.Paths.ConfigDirectory, "instance-engine", "shared-json-state.json");
        File.Delete(statePath);

        var ownership = await shared.GetOwnershipAsync(target);
        Assert.True(ownership.IsSuccess, ownership.Error?.ToString());
        Assert.Equal(Sha256("{\"id\":\"new\"}"), ownership.Value!.LastEclWrittenSha256);
        Assert.False(ownership.Value.IsExternallyModified);
    }

    private static string CreateVersionJson(TestEnvironment environment, string versionId, string content)
    {
        var directory = Path.Combine(environment.Paths.VersionsDirectory, versionId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{versionId}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<(SharedJsonTransactionRecord Record, string RecoveryDirectory)> WriteCrashJournalAsync(
        TestEnvironment environment,
        string transactionId,
        string target,
        string originalContent,
        string writtenContent)
    {
        var recoveryDirectory = Path.Combine(
            environment.Paths.ConfigDirectory,
            "instance-engine",
            "recovery",
            "shared",
            transactionId);
        var blob = $"blobs/0-{Sha256(originalContent)[..16]}.bin";
        var backupPath = Path.Combine(recoveryDirectory, blob);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        await File.WriteAllTextAsync(backupPath, originalContent);

        var original = new SharedJsonFingerprint(
            true,
            Encoding.UTF8.GetByteCount(originalContent),
            Sha256(originalContent));
        var written = new SharedJsonFingerprint(
            true,
            Encoding.UTF8.GetByteCount(writtenContent),
            Sha256(writtenContent));
        var record = new SharedJsonTransactionRecord(
            transactionId,
            SharedJsonTargetKind.VersionManifest,
            "crash simulation",
            SharedJsonTransactionState.Applying,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new SharedJsonTargetRecord(
                    0,
                    SharedJsonTargetKind.VersionManifest,
                    target,
                    original,
                    written,
                    written,
                    new SharedJsonBackup(blob, original.Length, original.Sha256!),
                    Applied: true),
            ],
            RecoveryDirectory: recoveryDirectory,
            SchemaVersion: 1);

        var journalDirectory = Path.Combine(
            environment.Paths.ConfigDirectory,
            "instance-engine",
            "transactions",
            "shared");
        Directory.CreateDirectory(journalDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(journalDirectory, $"{transactionId}.json"),
            JsonSerializer.Serialize(record, EclJson.Indented));
        return (record, recoveryDirectory);
    }

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
