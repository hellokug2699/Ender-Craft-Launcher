// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Engine.Snapshots;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

public sealed class InstanceSnapshotTests
{
    [Fact]
    public async Task CreateVerifyDiffAndRestoreSnapshot()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var snapshots = provider.GetRequiredService<InstanceSnapshotService>();
        var created = (await instances.CreateAsync("Snapshot", "1.20.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);

        await File.WriteAllTextAsync(Path.Combine(game, "options.txt"), "fov:70\n");
        await File.WriteAllTextAsync(Path.Combine(game, "config", "old.toml"), "v = 1\n");

        var snapshot = await snapshots.CreateAsync(created.Id, new SnapshotCreateOptions(SnapshotScope.Full));
        Assert.True(snapshot.IsSuccess, snapshot.Error?.ToString());
        Assert.Equal(SnapshotStatus.Complete, snapshot.Value!.Status);
        Assert.True(snapshot.Value.FileCount >= 2);

        await File.WriteAllTextAsync(Path.Combine(game, "options.txt"), "fov:90\n");
        await File.WriteAllTextAsync(Path.Combine(game, "config", "new.toml"), "new = true\n");

        var diff = await snapshots.DiffAsync(created.Id, snapshot.Value.SnapshotId);
        Assert.True(diff.IsSuccess, diff.Error?.ToString());
        Assert.True(diff.Value!.Modified >= 1);
        Assert.True(diff.Value.Added >= 1);

        var verify = await snapshots.VerifyAsync(created.Id, snapshot.Value.SnapshotId);
        Assert.True(verify.IsSuccess, verify.Error?.ToString());
        Assert.True(verify.Value!.IsValid);

        var restore = await snapshots.RestoreAsync(created.Id, snapshot.Value.SnapshotId);
        Assert.True(restore.IsSuccess, restore.Error?.ToString());
        Assert.NotNull(restore.Value!.SafetySnapshotId);
        Assert.Equal("fov:70\n", await File.ReadAllTextAsync(Path.Combine(game, "options.txt")));
        Assert.False(File.Exists(Path.Combine(game, "config", "new.toml")));
        Assert.True(File.Exists(Path.Combine(game, "config", "old.toml")));
    }

    [Fact]
    public async Task VerifyReportsMissingObjectAndRestoreRefuses()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var snapshots = provider.GetRequiredService<InstanceSnapshotService>();
        var created = (await instances.CreateAsync("Missing Object", "1.20.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        await File.WriteAllTextAsync(Path.Combine(game, "options.txt"), "fov:70\n");

        var snapshot = await snapshots.CreateAsync(created.Id, new SnapshotCreateOptions(SnapshotScope.Full));
        Assert.True(snapshot.IsSuccess, snapshot.Error?.ToString());

        var objectPath = snapshots.Objects.GetObjectPath(
            snapshot.Value!.Files.First(file => file.RelativePath == "options.txt").Sha256);
        File.Delete(objectPath);

        var verify = await snapshots.VerifyAsync(created.Id, snapshot.Value.SnapshotId);
        Assert.True(verify.IsSuccess, verify.Error?.ToString());
        Assert.False(verify.Value!.IsValid);
        Assert.Equal(1, verify.Value.MissingObjects);

        var restore = await snapshots.RestoreAsync(created.Id, snapshot.Value.SnapshotId);
        Assert.False(restore.IsSuccess);
        Assert.Equal("snapshot.incomplete", restore.Error?.Code);
    }

    [Fact]
    public async Task WorldSnapshotRequiresGameToBeClosed()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var snapshots = provider.GetRequiredService<InstanceSnapshotService>();
        var created = (await instances.CreateAsync("World Snapshot", "1.20.1")).Value!;
        var world = Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "saves", "World");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "level");
        await File.WriteAllTextAsync(Path.Combine(world, "session.lock"), "locked");

        var blocked = await snapshots.CreateAsync(
            created.Id,
            new SnapshotCreateOptions(SnapshotScope.World, "World"));
        Assert.False(blocked.IsSuccess);
        Assert.Equal("snapshot.world_running", blocked.Error?.Code);

        var allowed = await snapshots.CreateAsync(
            created.Id,
            new SnapshotCreateOptions(SnapshotScope.World, "World", AllowRunningWorld: true));
        Assert.True(allowed.IsSuccess, allowed.Error?.ToString());
        Assert.Equal(SnapshotScope.World, allowed.Value!.Scope);
        Assert.NotNull(allowed.Value.ConsistencyWarning);
        Assert.DoesNotContain(allowed.Value.Files, file => file.RelativePath.EndsWith("session.lock"));
    }

    [Fact]
    public async Task DeleteSnapshotKeepsSharedObjectsUntilExplicitPrune()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var snapshots = provider.GetRequiredService<InstanceSnapshotService>();
        var created = (await instances.CreateAsync("Retention", "1.20.1")).Value!;
        await File.WriteAllTextAsync(
            Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "options.txt"),
            "fov:70\n");

        var first = await snapshots.CreateAsync(created.Id, new SnapshotCreateOptions(SnapshotScope.Full));
        var second = await snapshots.CreateAsync(created.Id, new SnapshotCreateOptions(SnapshotScope.Full));
        Assert.True(first.IsSuccess && second.IsSuccess);

        var deleted = await snapshots.DeleteAsync(created.Id, first.Value!.SnapshotId);
        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        var listed = await snapshots.ListAsync(created.Id);
        Assert.Single(listed.Value!);

        // 第二个快照仍引用同一个对象；显式 Prune 不得删除它。
        var pruned = await snapshots.PruneOrphansAsync();
        Assert.True(pruned.IsSuccess, pruned.Error?.ToString());
        Assert.True(File.Exists(snapshots.Objects.GetObjectPath(
            second.Value!.Files.First(file => file.RelativePath == "options.txt").Sha256)));
    }
}
