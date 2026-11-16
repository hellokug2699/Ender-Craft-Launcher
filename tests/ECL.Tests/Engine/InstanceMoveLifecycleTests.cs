// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Storage;
using Ecl.Minecraft.Engine.Lifecycle;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

/// <summary>F-03 S6：跨受管理根移动、多根记录解析、源退休与删除隔离区同卷。</summary>
public sealed class InstanceMoveLifecycleTests : IDisposable
{
    private readonly List<string> _directories = [];

    [Fact]
    public async Task MoveAcrossManagedRootsMovesRecordAndDataAndRetiresSource()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var instances = provider.GetRequiredService<IInstanceService>();
        var moves = provider.GetRequiredService<IInstanceMoveService>();
        var paths = environment.Paths;

        var primary = NewDirectory("primary");
        var target = NewDirectory("target");
        Assert.True((await workspace.InitializeAsync(primary)).IsSuccess);
        var targetRoot = await workspace.AddManagedRootAsync(target);
        Assert.True(targetRoot.IsSuccess, targetRoot.Error?.ToString());

        var created = await instances.CreateAsync("Move Me", "1.20.1");
        Assert.True(created.IsSuccess, created.Error?.ToString());
        var instance = created.Value!;
        var sourceRecord = paths.GetInstanceDirectory(instance.Id);
        var sourceGameDirectory = paths.GetInstanceGameDirectory(instance.Id);
        var worldFile = Path.Combine(sourceGameDirectory, "saves", "World", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(worldFile)!);
        await File.WriteAllTextAsync(worldFile, "world-data");
        await File.WriteAllTextAsync(Path.Combine(sourceGameDirectory, "options.txt"), "lang:zh_cn");

        var plan = await moves.PlanAsync(instance.Id, targetRoot.Value!.Id);
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        Assert.Empty(plan.Value!.Blockers);

        var result = await moves.ExecuteAsync(plan.Value!);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var moved = result.Value!;
        Assert.Equal(targetRoot.Value.Id, moved.Instance.SourceRootId);
        Assert.Null(moved.Instance.GameDirectory);
        var expectedRecord = Path.Combine(target, "instances", instance.Id);
        Assert.Equal(expectedRecord, paths.GetInstanceDirectory(instance.Id));
        Assert.True(File.Exists(Path.Combine(expectedRecord, "instance.json")));
        Assert.True(File.Exists(Path.Combine(expectedRecord, "minecraft", "saves", "World", "level.dat")));
        Assert.False(File.Exists(Path.Combine(expectedRecord, "minecraft", "logs", "latest.log")));
        Assert.False(Directory.Exists(sourceRecord));
        Assert.True(Directory.Exists(Path.Combine(primary, ".ecl-retired")));
        Assert.NotNull(moved.RetiredSourcePath);
        Assert.NotNull(moved.SnapshotId);
        var reloaded = await instances.GetAsync(instance.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(targetRoot.Value.Id, reloaded!.SourceRootId);
    }

    [Fact]
    public async Task MoveRejectsSameRootAndUnregisteredTarget()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var instances = provider.GetRequiredService<IInstanceService>();
        var moves = provider.GetRequiredService<IInstanceMoveService>();

        var primary = NewDirectory("primary");
        Assert.True((await workspace.InitializeAsync(primary)).IsSuccess);
        var created = await instances.CreateAsync("Stay", "1.20.1");
        Assert.True(created.IsSuccess);

        var sameRoot = await moves.PlanAsync(created.Value!.Id, workspace.Primary!.Id);
        Assert.False(sameRoot.IsSuccess);
        Assert.Equal("instance.lifecycle.target_root_invalid", sameRoot.Error!.Code);

        var unknownRoot = await moves.PlanAsync(created.Value.Id, "ws-not-registered");
        Assert.False(unknownRoot.IsSuccess);
        Assert.Equal("instance.lifecycle.target_root_invalid", unknownRoot.Error!.Code);
    }

    [Fact]
    public async Task MoveRejectsExternalOwnership()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var instances = provider.GetRequiredService<IInstanceService>();
        var store = provider.GetRequiredService<IInstanceStore>();
        var moves = provider.GetRequiredService<IInstanceMoveService>();

        var primary = NewDirectory("primary");
        var target = NewDirectory("target");
        var external = NewDirectory("external-game");
        Assert.True((await workspace.InitializeAsync(primary)).IsSuccess);
        var root = await workspace.AddManagedRootAsync(target);
        Assert.True(root.IsSuccess);

        var instance = new GameInstance
        {
            Id = "external-demo",
            Name = "External",
            GameVersion = "1.20.1",
            VersionId = "1.20.1",
            GameDirectory = external,
        };
        await store.SaveAsync(instance);
        Assert.NotNull(await instances.GetAsync(instance.Id));

        var plan = await moves.PlanAsync(instance.Id, root.Value!.Id);

        Assert.True(plan.IsSuccess);
        Assert.Contains("instance.lifecycle.unsupported_ownership", plan.Value!.Blockers);
        var executed = await moves.ExecuteAsync(plan.Value!);
        Assert.False(executed.IsSuccess);
        Assert.Equal("instance.lifecycle.blocked", executed.Error!.Code);
    }

    [Fact]
    public async Task MoveRejectsExistingTargetRecord()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var instances = provider.GetRequiredService<IInstanceService>();
        var moves = provider.GetRequiredService<IInstanceMoveService>();

        var primary = NewDirectory("primary");
        var target = NewDirectory("target");
        Assert.True((await workspace.InitializeAsync(primary)).IsSuccess);
        var root = await workspace.AddManagedRootAsync(target);
        Assert.True(root.IsSuccess);
        var created = await instances.CreateAsync("Conflict", "1.20.1");
        Assert.True(created.IsSuccess);
        Directory.CreateDirectory(Path.Combine(target, "instances", created.Value!.Id));

        var plan = await moves.PlanAsync(created.Value.Id, root.Value!.Id);

        Assert.True(plan.IsSuccess);
        Assert.Contains("instance.lifecycle.target_exists", plan.Value!.Blockers);
        var executed = await moves.ExecuteAsync(plan.Value!);
        Assert.False(executed.IsSuccess);
        Assert.Equal("instance.lifecycle.blocked", executed.Error!.Code);
    }

    [Fact]
    public async Task PlanIsStaleWhenSourceChangesAfterPlanning()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var instances = provider.GetRequiredService<IInstanceService>();
        var moves = provider.GetRequiredService<IInstanceMoveService>();
        var paths = environment.Paths;

        var primary = NewDirectory("primary");
        var target = NewDirectory("target");
        Assert.True((await workspace.InitializeAsync(primary)).IsSuccess);
        var root = await workspace.AddManagedRootAsync(target);
        Assert.True(root.IsSuccess);
        var created = await instances.CreateAsync("Changing", "1.20.1");
        Assert.True(created.IsSuccess);

        var plan = await moves.PlanAsync(created.Value!.Id, root.Value!.Id);
        Assert.True(plan.IsSuccess);
        Assert.Empty(plan.Value!.Blockers);

        // 计划后改变记录指纹/文件数 → 旧计划必须失效。
        await File.WriteAllTextAsync(
            Path.Combine(paths.GetInstanceGameDirectory(created.Value.Id), "options.txt"),
            "changed-after-plan");

        var executed = await moves.ExecuteAsync(plan.Value!);

        Assert.False(executed.IsSuccess);
        Assert.Equal("instance.lifecycle.plan_stale", executed.Error!.Code);
        Assert.True(Directory.Exists(paths.GetInstanceDirectory(created.Value.Id)));
    }

    [Fact]
    public async Task MovedInstanceIsResolvedAfterRestartLikeBootstrap()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var instances = provider.GetRequiredService<IInstanceService>();
        var moves = provider.GetRequiredService<IInstanceMoveService>();

        var primary = NewDirectory("primary");
        var target = NewDirectory("target");
        Assert.True((await workspace.InitializeAsync(primary)).IsSuccess);
        var root = await workspace.AddManagedRootAsync(target);
        Assert.True(root.IsSuccess);
        var created = await instances.CreateAsync("Restart Resolve", "1.20.1");
        Assert.True(created.IsSuccess);
        var plan = await moves.PlanAsync(created.Value!.Id, root.Value!.Id);
        Assert.True(plan.IsSuccess);
        Assert.True((await moves.ExecuteAsync(plan.Value!)).IsSuccess);

        // 模拟新进程启动：同一 workspace.json 装配到新的 AppPaths。
        var restarted = new AppPaths(environment.Root);
        var state = WorkspaceBootstrap.Apply(restarted, legacyGameRoot: null);

        Assert.NotEmpty(state.Roots);
        Assert.Equal(Path.Combine(target, "instances", created.Value.Id), restarted.GetInstanceDirectory(created.Value.Id));
        Assert.True(File.Exists(restarted.GetInstanceMetadataFile(created.Value.Id)));
    }

    [Fact]
    public async Task MovingIsBlockedWhenInstanceHasActiveSession()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices(services =>
            services.AddSingleton<IInstanceActivityGuard, ActiveGuard>());
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var instances = provider.GetRequiredService<IInstanceService>();
        var moves = provider.GetRequiredService<IInstanceMoveService>();

        var primary = NewDirectory("primary");
        var target = NewDirectory("target");
        Assert.True((await workspace.InitializeAsync(primary)).IsSuccess);
        var root = await workspace.AddManagedRootAsync(target);
        Assert.True(root.IsSuccess);
        var created = await instances.CreateAsync("Running", "1.20.1");
        Assert.True(created.IsSuccess);

        var plan = await moves.PlanAsync(created.Value!.Id, root.Value!.Id);

        Assert.True(plan.IsSuccess);
        Assert.Contains("instance.lifecycle.instance_running", plan.Value!.Blockers);
        var executed = await moves.ExecuteAsync(plan.Value!);
        Assert.False(executed.IsSuccess);
        Assert.Equal("instance.lifecycle.blocked", executed.Error!.Code);
    }

    [Fact]
    public async Task DeletingMovedInstanceUsesTargetRootQuarantine()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var instances = provider.GetRequiredService<IInstanceService>();
        var moves = provider.GetRequiredService<IInstanceMoveService>();
        var deletion = provider.GetRequiredService<IInstanceDeletionService>();
        var paths = environment.Paths;

        var primary = NewDirectory("primary");
        var target = NewDirectory("target");
        Assert.True((await workspace.InitializeAsync(primary)).IsSuccess);
        var root = await workspace.AddManagedRootAsync(target);
        Assert.True(root.IsSuccess);
        var created = await instances.CreateAsync("Delete After Move", "1.20.1");
        Assert.True(created.IsSuccess);
        var plan = await moves.PlanAsync(created.Value!.Id, root.Value!.Id);
        Assert.True(plan.IsSuccess);
        Assert.True((await moves.ExecuteAsync(plan.Value!)).IsSuccess);

        var deletionPlan = await deletion.PlanAsync(created.Value.Id);
        Assert.True(deletionPlan.IsSuccess, deletionPlan.Error?.ToString());
        var result = await deletion.DeleteAsync(deletionPlan.Value!);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(InstanceDeletionState.Purged, result.Value!.Journal.State);
        Assert.False(Directory.Exists(Path.Combine(target, "instances", created.Value.Id)));
        Assert.True(Directory.Exists(Path.Combine(target, ".ecl-trash")));
        Assert.Null(await instances.GetAsync(created.Value.Id));
        Assert.False(paths.TryGetInstanceRoot(created.Value.Id, out _));
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            TryDelete(directory);
        }

    }

    private string NewDirectory(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "ecl-f03-move-dirs", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(path);
        _directories.Add(Path.GetDirectoryName(path)!);
        return path;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }

    private sealed class ActiveGuard : IInstanceActivityGuard
    {
        public Task<Result<InstanceActivityGuardState>> InspectAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<InstanceActivityGuardState>.Ok(new InstanceActivityGuardState(
                instanceId,
                Exists: true,
                HasActiveSession: true,
                SessionId: "session-active",
                ProcessId: 123,
                ProcessAlive: true,
                HasPendingTransaction: false,
                InstanceActivityState.Idle,
                IdentityConflict: false,
                Detail: "运行中")));
    }
}
