// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Engine.Lifecycle;
using Ecl.Minecraft.Engine.Snapshots;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

/// <summary>
/// F-03 S2/S4：删除计划哈希、隔离区与外部目录零删除。
/// </summary>
public sealed class InstanceDeletionLifecycleTests
{
    [Fact]
    public async Task ManagedInstanceIsQuarantinedAndPurgedThroughJournal()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var deletions = provider.GetRequiredService<IInstanceDeletionService>();
        var created = (await instances.CreateAsync("DeleteLifecycle", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var save = Path.Combine(game, "saves", "world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        await File.WriteAllTextAsync(save, "world");

        var plan = await deletions.PlanAsync(created.Id);
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        Assert.Equal(InstanceOwnership.EclManaged, plan.Value!.Ownership);
        Assert.True(plan.Value.IncludesWorldSaves);
        Assert.True(plan.Value.EstimatedBytes > 0);
        Assert.Empty(plan.Value.Blockers);

        var deleted = await deletions.DeleteAsync(plan.Value);
        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.Equal(InstanceDeletionState.Purged, deleted.Value!.Journal.State);
        Assert.False(Directory.Exists(environment.Paths.GetInstanceDirectory(created.Id)));
        Assert.Empty(await instances.GetInstancesAsync());
        var trash = Path.Combine(environment.Paths.GameRoot, ".ecl-trash");
        Assert.False(Directory.Exists(trash) && Directory.EnumerateFileSystemEntries(trash).Any());
    }

    [Fact]
    public async Task ExternalInstanceOnlyRemovesRecordAndKeepsGameDirectory()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var deletions = provider.GetRequiredService<IInstanceDeletionService>();
        var external = Path.Combine(environment.Root, "external", "delete-me");
        var save = Path.Combine(external, "saves", "world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        await File.WriteAllTextAsync(save, "user-world");
        await WriteImportedMetadataAsync(environment, "external-delete", "External Delete", external);

        var plan = await deletions.PlanAsync("external-delete");
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        Assert.Equal(InstanceOwnership.External, plan.Value!.Ownership);
        Assert.DoesNotContain(plan.Value.Entries, entry => entry.CanDelete && entry.Role == InstanceDeletionEntryRole.ExternalGameData);

        var deleted = await deletions.DeleteAsync(plan.Value);
        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.Equal(external, deleted.Value!.DetachedGameDataPath);
        Assert.True(Directory.Exists(external));
        Assert.Equal("user-world", await File.ReadAllTextAsync(save));
        Assert.Empty(await instances.GetInstancesAsync());
    }

    [Fact]
    public async Task PlanHashAndRunningSessionBlockDeletion()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var sessions = provider.GetRequiredService<IGameSessionService>();
        var deletions = provider.GetRequiredService<IInstanceDeletionService>();
        var created = (await instances.CreateAsync("DeleteBlocked", "1.21.1")).Value!;

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

        var plan = await deletions.PlanAsync(created.Id);
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        Assert.Contains("instance.lifecycle.instance_running", plan.Value!.Blockers);
        var blocked = await deletions.DeleteAsync(plan.Value);
        Assert.False(blocked.IsSuccess);

        var stale = plan.Value with { PlanHash = "deadbeef" };
        var rejected = await deletions.DeleteAsync(stale);
        Assert.False(rejected.IsSuccess);
        Assert.Equal("instance.lifecycle.plan_stale", rejected.Error?.Code);
        Assert.True(Directory.Exists(environment.Paths.GetInstanceDirectory(created.Id)));
    }

    [Fact]
    public async Task SnapshotsAreRetainedByDefaultAndCanBeDeletedExplicitly()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var snapshots = provider.GetRequiredService<IInstanceSnapshotService>();
        var maintenance = provider.GetRequiredService<ISnapshotMaintenanceService>();
        var deletions = provider.GetRequiredService<IInstanceDeletionService>();
        var created = (await instances.CreateAsync("DeleteSnapshots", "1.21.1")).Value!;
        await File.WriteAllTextAsync(
            Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "options.txt"),
            "fov:70\n");
        await snapshots.CreateAsync(created.Id, new SnapshotCreateOptions(SnapshotScope.Full));

        var plan = await deletions.PlanAsync(created.Id);
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        Assert.Equal(1, plan.Value!.Snapshots.SnapshotCount);
        Assert.False(plan.Value.DeleteSnapshots);

        var deleted = await deletions.DeleteAsync(plan.Value);
        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        var groups = await maintenance.ListGroupsAsync();
        var group = Assert.Single(groups.Value!);
        Assert.True(group.Orphaned);

        var second = (await instances.CreateAsync("DeleteSnapshots2", "1.21.1")).Value!;
        await File.WriteAllTextAsync(
            Path.Combine(environment.Paths.GetInstanceGameDirectory(second.Id), "options.txt"),
            "fov:70\n");
        await snapshots.CreateAsync(second.Id, new SnapshotCreateOptions(SnapshotScope.Full));
        var withSnapshots = await deletions.PlanAsync(second.Id, deleteSnapshots: true);
        Assert.True(withSnapshots.IsSuccess, withSnapshots.Error?.ToString());
        var removed = await deletions.DeleteAsync(withSnapshots.Value!);
        Assert.True(removed.IsSuccess, removed.Error?.ToString());
        Assert.DoesNotContain(
            (await maintenance.ListGroupsAsync()).Value!,
            item => item.InstanceId == second.Id);
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
        var json = System.Text.Json.JsonSerializer.Serialize(instance, Ecl.Core.Json.EclJson.Default);
        await File.WriteAllTextAsync(environment.Paths.GetInstanceMetadataFile(instanceId), json);
    }
}
