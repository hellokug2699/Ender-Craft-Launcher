// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core.Json;
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
        // 运行中创建的快照本身是 Incomplete，永远不能作为恢复点。
        Assert.Equal("snapshot.incomplete", restore.Error?.Code);
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

    [Fact]
    public async Task RestorePreviewHasPlanHashAndRestoreUsesDedicatedTransactionKind()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var snapshots = provider.GetRequiredService<IInstanceSnapshotService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("Preview", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        await File.WriteAllTextAsync(Path.Combine(game, "options.txt"), "fov:70\n");
        var snapshot = (await snapshots.CreateAsync(created.Id, new SnapshotCreateOptions(SnapshotScope.Full))).Value!;

        await File.WriteAllTextAsync(Path.Combine(game, "options.txt"), "fov:90\n");
        var preview = await snapshots.PreviewRestoreAsync(created.Id, snapshot.SnapshotId);
        Assert.True(preview.IsSuccess, preview.Error?.ToString());
        Assert.False(string.IsNullOrWhiteSpace(preview.Value!.PlanHash));
        Assert.Equal(1, preview.Value.FilesToWrite);
        Assert.Equal("EclManaged", preview.Value.Ownership);
        Assert.Empty(preview.Value.Blockers);

        var stale = await snapshots.RestoreAsync(
            created.Id,
            snapshot.SnapshotId,
            new SnapshotRestoreOptions(PreviewHash: "deadbeef"));
        Assert.False(stale.IsSuccess);
        Assert.Equal("snapshot.preview_stale", stale.Error?.Code);

        var restored = await snapshots.RestoreAsync(
            created.Id,
            snapshot.SnapshotId,
            new SnapshotRestoreOptions(PreviewHash: preview.Value.PlanHash));
        Assert.True(restored.IsSuccess, restored.Error?.ToString());
        Assert.Equal("fov:70\n", await File.ReadAllTextAsync(Path.Combine(game, "options.txt")));

        var journals = await transactions.ListAsync(created.Id);
        Assert.Contains(journals.Value!, record => record.Kind == InstanceTransactionKind.SnapshotRestore);
    }

    [Fact]
    public async Task ExternalAndSharedTargetsRequireExplicitConfirmation()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var snapshots = provider.GetRequiredService<IInstanceSnapshotService>();
        var external = Path.Combine(environment.Root, "external", "shared");
        var level = Path.Combine(external, "saves", "world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(level)!);
        await File.WriteAllTextAsync(level, "user-world");
        await WriteImportedMetadataAsync(environment, "import-a", "A", external);
        await WriteImportedMetadataAsync(environment, "import-b", "B", external);

        var snapshot = await snapshots.CreateAsync(
            "import-a",
            new SnapshotCreateOptions(SnapshotScope.Full, Origin: SnapshotOrigin.User));
        Assert.True(snapshot.IsSuccess, snapshot.Error?.ToString());

        var preview = await snapshots.PreviewRestoreAsync("import-a", snapshot.Value!.SnapshotId);
        Assert.True(preview.IsSuccess, preview.Error?.ToString());
        Assert.Equal("SharedExternal", preview.Value!.Ownership);
        Assert.Contains("import-b", preview.Value.SharedWith);
        Assert.Contains("snapshot.shared_target_requires_confirmation", preview.Value.Blockers);

        var blocked = await snapshots.RestoreAsync("import-a", snapshot.Value.SnapshotId);
        Assert.False(blocked.IsSuccess);
        Assert.Equal("snapshot.shared_target_requires_confirmation", blocked.Error?.Code);
    }

    [Fact]
    public async Task SingleExternalTargetRequiresExplicitConfirmation()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var snapshots = provider.GetRequiredService<IInstanceSnapshotService>();
        var external = Path.Combine(environment.Root, "external", "single");
        var level = Path.Combine(external, "saves", "world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(level)!);
        await File.WriteAllTextAsync(level, "user-world");
        await WriteImportedMetadataAsync(environment, "import-single", "Single", external);

        var snapshot = await snapshots.CreateAsync(
            "import-single",
            new SnapshotCreateOptions(SnapshotScope.Full));
        Assert.True(snapshot.IsSuccess, snapshot.Error?.ToString());
        var blocked = await snapshots.RestoreAsync("import-single", snapshot.Value!.SnapshotId);
        Assert.False(blocked.IsSuccess);
        Assert.Equal("snapshot.external_target_requires_confirmation", blocked.Error?.Code);
    }

    [Fact]
    public async Task SnapshotGroupsReportOrphansAndPruneRespectsReferences()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var snapshots = provider.GetRequiredService<InstanceSnapshotService>();
        var maintenance = provider.GetRequiredService<ISnapshotMaintenanceService>();
        var created = (await instances.CreateAsync("Groups", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        await File.WriteAllTextAsync(Path.Combine(game, "options.txt"), "fov:70\n");

        var first = (await snapshots.CreateAsync(created.Id, new SnapshotCreateOptions(SnapshotScope.Full))).Value!;
        var second = (await snapshots.CreateAsync(created.Id, new SnapshotCreateOptions(SnapshotScope.Full))).Value!;
        var groups = await maintenance.ListGroupsAsync();
        Assert.True(groups.IsSuccess, groups.Error?.ToString());
        var group = Assert.Single(groups.Value!);
        Assert.Equal(2, group.SnapshotCount);
        Assert.False(group.Orphaned);

        var dryRun = await maintenance.PruneOrphansAsync(
            new SnapshotPruneOptions(GracePeriod: TimeSpan.Zero, DryRun: true));
        Assert.True(dryRun.IsSuccess, dryRun.Error?.ToString());
        Assert.Equal(0, dryRun.Value!.DeletedObjects);

        await snapshots.DeleteAsync(created.Id, first.SnapshotId);
        var afterDelete = await maintenance.ListGroupsAsync();
        Assert.Single(afterDelete.Value!);
        Assert.Equal(1, afterDelete.Value![0].SnapshotCount);
        Assert.True(File.Exists(snapshots.Objects.GetObjectPath(
            second.Files.First(file => file.RelativePath == "options.txt").Sha256)));

        var summary = await maintenance.GetStorageSummaryAsync();
        Assert.True(summary.IsSuccess, summary.Error?.ToString());
        Assert.Equal(1, summary.Value!.Groups);
        Assert.Equal(1, summary.Value.SnapshotCount);
    }

    [Fact]
    public async Task DeletedInstanceSnapshotsBecomeOrphanedAndCanBeExplicitlyRemoved()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var snapshots = provider.GetRequiredService<IInstanceSnapshotService>();
        var maintenance = provider.GetRequiredService<ISnapshotMaintenanceService>();
        var created = (await instances.CreateAsync("OrphanGroup", "1.21.1")).Value!;
        await File.WriteAllTextAsync(
            Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "options.txt"),
            "fov:70\n");
        var snapshot = (await snapshots.CreateAsync(created.Id, new SnapshotCreateOptions(SnapshotScope.Full))).Value!;

        await instances.DeleteAsync(created.Id);
        var groups = await maintenance.ListGroupsAsync();
        var group = Assert.Single(groups.Value!);
        Assert.True(group.Orphaned);
        Assert.Equal(created.Id, group.InstanceId);

        var removed = await maintenance.DeleteInstanceSnapshotsAsync(created.Id);
        Assert.True(removed.IsSuccess, removed.Error?.ToString());
        Assert.Empty((await maintenance.ListGroupsAsync()).Value!);
        _ = snapshot;
    }

    [Fact]
    public async Task ProtectionRequiredCreatesVerifiedSnapshotAndRecommendedDoesNotBlock()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var sessions = provider.GetRequiredService<IGameSessionService>();
        var protection = provider.GetRequiredService<ISnapshotProtectionService>();
        var created = (await instances.CreateAsync("Protection", "1.21.1")).Value!;
        await File.WriteAllTextAsync(
            Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "options.txt"),
            "fov:70\n");

        var required = await protection.EnsureAsync(
            created.Id,
            SnapshotProtectionRequirement.Required,
            SnapshotOrigin.PreChange);
        Assert.True(required.IsSuccess, required.Error?.ToString());
        Assert.NotNull(required.Value!.Snapshot);

        AttachRunningSession(sessions, created, environment);
        var blocked = await protection.EnsureAsync(
            created.Id,
            SnapshotProtectionRequirement.Required,
            SnapshotOrigin.PreChange);
        Assert.False(blocked.IsSuccess);
        Assert.Equal("snapshot.protection_failed", blocked.Error?.Code);

        var recommended = await protection.EnsureAsync(
            created.Id,
            SnapshotProtectionRequirement.Recommended,
            SnapshotOrigin.PreChange);
        Assert.True(recommended.IsSuccess, recommended.Error?.ToString());
        Assert.Null(recommended.Value!.Snapshot);
        Assert.Equal("snapshot.instance_running", recommended.Value.FailureCode);
    }

    private static async Task WriteImportedMetadataAsync(
        TestEnvironment environment,
        string instanceId,
        string name,
        string gameDirectory)
    {
        var record = Path.Combine(environment.Paths.GetInstanceDirectory(instanceId));
        Directory.CreateDirectory(record);
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
