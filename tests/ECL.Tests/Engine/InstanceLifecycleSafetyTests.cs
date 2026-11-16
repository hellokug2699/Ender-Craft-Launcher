// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Json;
using Ecl.Minecraft.Engine.Lifecycle;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

/// <summary>
/// F-03 S1/S3：路径硬拒绝、所有权分类与活动门禁。
/// </summary>
public sealed class InstanceLifecycleSafetyTests
{
    [Fact]
    public void PathPolicyRejectsProtectedTargetsAndOverlaps()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var policy = provider.GetRequiredService<IInstancePathPolicy>();

        Assert.Equal("instance.path.volume_root", policy.CheckTarget(@"C:\", allowMissing: true).ErrorCode);
        Assert.Equal(
            "instance.path.protected_root",
            policy.CheckTarget(Path.Combine(environment.Root, "instances", "x"), allowMissing: true).ErrorCode);
        Assert.Equal(
            "instance.path.protected_root",
            policy.CheckTarget(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), allowMissing: true).ErrorCode);
        Assert.Equal(
            "instance.path.reserved_name",
            policy.CheckTarget(Path.Combine(environment.Root, "CON"), allowMissing: true).ErrorCode);

        var outside = Path.Combine(Path.GetTempPath(), "ecl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Assert.True(policy.CheckTarget(outside, allowMissing: true).IsAllowed);
            var overlap = policy.CheckMove(outside, Path.Combine(outside, "nested"));
            Assert.False(overlap.IsAllowed);
            Assert.Equal("instance.path.overlap", overlap.ErrorCode);
            Directory.Delete(outside);
        }
        finally
        {
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OwnershipClassifiesManagedExternalAndShared()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var classifier = provider.GetRequiredService<IInstanceOwnershipClassifier>();
        var managed = (await instances.CreateAsync("ManagedOwnership", "1.21.1")).Value!;
        var managedEvidence = await classifier.ClassifyAsync(managed.Id);
        Assert.True(managedEvidence.IsSuccess, managedEvidence.Error?.ToString());
        Assert.Equal(InstanceOwnership.EclManaged, managedEvidence.Value!.Ownership);

        var external = Path.Combine(environment.Root, "external", "one");
        Directory.CreateDirectory(external);
        await WriteImportedMetadataAsync(environment, "external-one", "External", external);
        var externalEvidence = await classifier.ClassifyAsync("external-one");
        Assert.Equal(InstanceOwnership.External, externalEvidence.Value!.Ownership);

        await WriteImportedMetadataAsync(environment, "external-two", "External Two", external);
        var shared = await classifier.ClassifyAsync("external-one");
        Assert.Equal(InstanceOwnership.SharedExternal, shared.Value!.Ownership);
        Assert.Contains("external-two", shared.Value.SharedWith);
    }

    [Fact]
    public async Task ActivityGuardReportsRunningSessionAndPendingTransaction()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var sessions = provider.GetRequiredService<IGameSessionService>();
        var guard = provider.GetRequiredService<IInstanceActivityGuard>();
        var created = (await instances.CreateAsync("ActivityGuard", "1.21.1")).Value!;

        var idle = await guard.InspectAsync(created.Id);
        Assert.True(idle.IsSuccess, idle.Error?.ToString());
        Assert.False(idle.Value!.HasActiveSession);
        Assert.False(idle.Value.HasPendingTransaction);

        var session = sessions.Create(new SessionCreateRequest
        {
            InstanceId = created.Id,
            InstanceName = created.Name,
            GameDirectory = environment.Paths.GetInstanceGameDirectory(created.Id),
            JavaPath = "java",
            JavaVersion = "21",
            MinecraftVersion = created.GameVersion,
            Loader = created.Loader,
        });
        sessions.AttachProcess(
            session.SessionId,
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            Environment.ProcessPath ?? "dotnet",
            environment.Paths.GetInstanceGameDirectory(created.Id));

        var running = await guard.InspectAsync(created.Id);
        Assert.True(running.Value!.HasActiveSession);
        Assert.True(running.Value.ProcessAlive);
        Assert.NotNull(running.Value.SessionId);

        await WriteNeedsRecoveryJournalAsync(environment, created.Id);
        var recovery = await guard.InspectAsync(created.Id);
        Assert.True(recovery.Value!.HasPendingTransaction);
        Assert.Equal(InstanceActivityState.NeedsRecovery, recovery.Value.TransactionState);
    }

    private static async Task WriteImportedMetadataAsync(
        TestEnvironment environment,
        string instanceId,
        string name,
        string gameDirectory)
    {
        Directory.CreateDirectory(environment.Paths.GetInstanceDirectory(instanceId));
        var instance = new GameInstance
        {
            Id = instanceId,
            Name = name,
            GameVersion = "1.21.1",
            VersionId = "1.21.1",
            GameDirectory = gameDirectory,
        };
        await File.WriteAllTextAsync(
            environment.Paths.GetInstanceMetadataFile(instanceId),
            JsonSerializer.Serialize(instance, EclJson.Default));
    }

    private static async Task WriteNeedsRecoveryJournalAsync(TestEnvironment environment, string instanceId)
    {
        var directory = Path.Combine(
            environment.Paths.ConfigDirectory,
            "instance-engine",
            "transactions",
            "instances",
            instanceId);
        Directory.CreateDirectory(directory);
        var record = new InstanceTransactionRecord(
            "tx-guard-recovery",
            instanceId,
            environment.Paths.GetInstanceGameDirectory(instanceId),
            "guard",
            InstanceTransactionState.NeedsRecovery,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [],
            [],
            0,
            SchemaVersion: 2);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "tx-guard-recovery.json"),
            JsonSerializer.Serialize(record, EclJson.Indented));
    }
}
