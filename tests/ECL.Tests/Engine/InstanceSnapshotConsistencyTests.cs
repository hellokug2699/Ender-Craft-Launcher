// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Ecl.Minecraft.Engine.Snapshots;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

/// <summary>
/// F-05 S1/S2：运行中快照只能是 Incomplete，清单携带类别/身份，对象写入校验复制后哈希。
/// </summary>
public sealed class InstanceSnapshotConsistencyTests
{
    [Fact]
    public async Task RunningInstanceBlocksFullSnapshotByDefault()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var sessions = provider.GetRequiredService<IGameSessionService>();
        var snapshots = provider.GetRequiredService<IInstanceSnapshotService>();
        var created = (await instances.CreateAsync("RunningSnapshot", "1.21.1")).Value!;
        await File.WriteAllTextAsync(
            Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "options.txt"),
            "fov:70\n");
        AttachRunningSession(sessions, created, environment);

        var blocked = await snapshots.CreateAsync(
            created.Id,
            new SnapshotCreateOptions(SnapshotScope.Full));
        Assert.False(blocked.IsSuccess);
        Assert.Equal("snapshot.instance_running", blocked.Error?.Code);

        var allowed = await snapshots.CreateAsync(
            created.Id,
            new SnapshotCreateOptions(SnapshotScope.Full, AllowRunningInstance: true));
        Assert.True(allowed.IsSuccess, allowed.Error?.ToString());
        Assert.Equal(SnapshotStatus.Incomplete, allowed.Value!.Status);
        Assert.NotNull(allowed.Value.ConsistencyWarning);
        Assert.False(allowed.Value.IsRestoreEligible);

        var restore = await snapshots.RestoreAsync(created.Id, allowed.Value.SnapshotId);
        Assert.False(restore.IsSuccess);
        Assert.Equal("snapshot.instance_running", restore.Error?.Code);
    }

    [Fact]
    public async Task ManifestCarriesOriginRetentionAndIdentity()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var snapshots = provider.GetRequiredService<IInstanceSnapshotService>();
        var created = (await instances.CreateAsync("ManifestV2", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        await File.WriteAllTextAsync(Path.Combine(game, "options.txt"), "fov:80\n");
        await File.WriteAllTextAsync(Path.Combine(game, "eula.txt"), "eula=true\n");
        await File.WriteAllTextAsync(Path.Combine(game, ".ecl-modpack.json"), "{\"name\":\"pack\"}");
        Directory.CreateDirectory(Path.Combine(game, "config"));
        await File.WriteAllTextAsync(Path.Combine(game, "config", "a.toml"), "a = 1\n");

        var snapshot = await snapshots.CreateAsync(
            created.Id,
            new SnapshotCreateOptions(
                SnapshotScope.Full,
                Origin: SnapshotOrigin.PreChange,
                RetentionClass: SnapshotRetentionClass.Automatic,
                Name: "before change"));
        Assert.True(snapshot.IsSuccess, snapshot.Error?.ToString());
        var manifest = snapshot.Value!;
        Assert.Equal(SnapshotOrigin.PreChange, manifest.Origin);
        Assert.Equal(SnapshotRetentionClass.Automatic, manifest.RetentionClass);
        Assert.Equal("before change", manifest.Name);
        Assert.False(string.IsNullOrWhiteSpace(manifest.InstancePathIdentity));
        Assert.Equal("EclManaged", manifest.InstanceOwnership);
        Assert.False(string.IsNullOrWhiteSpace(manifest.ToolVersion));
        Assert.True(manifest.IsRestoreEligible);
        Assert.Contains(manifest.Files, file => file.RelativePath == "options.txt");
        Assert.DoesNotContain(manifest.Files, file => file.RelativePath == "eula.txt");
        Assert.DoesNotContain(manifest.Files, file => file.RelativePath == ".ecl-modpack.json");
    }

    [Fact]
    public async Task SnapshotCreationHonorsReadLease()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var gate = provider.GetRequiredService<IInstanceWriteGate>();
        var snapshots = provider.GetRequiredService<IInstanceSnapshotService>();
        var created = (await instances.CreateAsync("ReadLeaseSnapshot", "1.21.1")).Value!;

        var write = await gate.AcquireAsync(created.Id);
        Assert.True(write.IsSuccess, write.Error?.ToString());
        var blocked = await snapshots.CreateAsync(created.Id, new SnapshotCreateOptions(SnapshotScope.Full));
        Assert.False(blocked.IsSuccess);
        Assert.Equal("instance.read_busy", blocked.Error?.Code);
        await write.Value!.DisposeAsync();

        var allowed = await snapshots.CreateAsync(created.Id, new SnapshotCreateOptions(SnapshotScope.Full));
        Assert.True(allowed.IsSuccess, allowed.Error?.ToString());
    }

    [Fact]
    public async Task ObjectStoreRejectsContentThatDoesNotMatchHash()
    {
        using var environment = new TestEnvironment();
        var objects = new SnapshotObjectStore(Path.Combine(environment.Root, "objects"));
        var source = Path.Combine(environment.Root, "source.bin");
        await File.WriteAllTextAsync(source, "actual content");
        var wrongHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("other content")));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            objects.PutAsync(source, wrongHash, new FileInfo(source).Length));
        Assert.False(File.Exists(objects.GetObjectPath(wrongHash)));
    }

    private static void AttachRunningSession(
        IGameSessionService sessions,
        GameInstance instance,
        TestEnvironment environment)
    {
        var session = sessions.Create(new SessionCreateRequest
        {
            InstanceId = instance.Id,
            InstanceName = instance.Name,
            GameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id),
            JavaPath = "java",
            JavaVersion = "21",
            MinecraftVersion = instance.GameVersion,
            Loader = instance.Loader,
        });
        sessions.AttachProcess(
            session.SessionId,
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            Environment.ProcessPath ?? "dotnet",
            environment.Paths.GetInstanceGameDirectory(instance.Id));
    }
}
