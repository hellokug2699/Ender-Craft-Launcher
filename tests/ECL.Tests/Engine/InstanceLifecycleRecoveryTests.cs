// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Storage;
using Ecl.Minecraft.Engine.Lifecycle;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

/// <summary>
/// F-03 收尾：隔离区继续清理、退休源清理、生命周期 journal 扫描/丢弃与重配置计划/journal。
/// 全部使用临时工作区，不依赖真实用户目录或网络。
/// </summary>
public sealed class InstanceLifecycleRecoveryTests
{
    [Fact]
    public async Task QuarantineCleanupFailureIsResumable()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var deletions = provider.GetRequiredService<IInstanceDeletionService>();
        var created = (await instances.CreateAsync("CleanupRetry", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var locked = Path.Combine(game, "locked.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(locked)!);
        await File.WriteAllTextAsync(locked, "blocked");
        File.SetAttributes(locked, FileAttributes.ReadOnly);

        var plan = await deletions.PlanAsync(created.Id);
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        var deleted = await deletions.DeleteAsync(plan.Value!);
        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());

        // 只读文件让隔离区清理失败：记录已移除、数据仍在隔离区，必须进入 NeedsRecovery 而不是“已释放”。
        Assert.Equal(InstanceDeletionState.NeedsRecovery, deleted.Value!.Journal.State);
        Assert.NotNull(deleted.Value.Journal.TombstonePath);
        Assert.True(Directory.Exists(deleted.Value.Journal.TombstonePath));
        Assert.Empty(await instances.GetInstancesAsync());

        var cleanup = provider.GetRequiredService<IInstanceLifecycleRecoveryService>();
        var inspected = await cleanup.InspectAsync();
        Assert.True(inspected.IsSuccess, inspected.Error?.ToString());
        var entry = Assert.Single(inspected.Value!.Entries);
        Assert.Equal("resume_purge", entry.Action);

        // 隔离后记录目录已移入隔离区，只读文件随之移动。
        var quarantinedLocked = Path.Combine(
            deleted.Value.Journal.TombstonePath!,
            "minecraft",
            "locked.dat");
        File.SetAttributes(quarantinedLocked, FileAttributes.Normal);
        var resumed = await deletions.ResumeCleanupAsync(plan.Value!.PlanId);
        Assert.True(resumed.IsSuccess, resumed.Error?.ToString());
        Assert.Equal(InstanceDeletionState.Purged, resumed.Value!.State);
        Assert.False(Directory.Exists(deleted.Value.Journal.TombstonePath));
    }

    [Fact]
    public async Task InspectReportsOrphanQuarantineAndDetachedData()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var deletions = provider.GetRequiredService<IInstanceDeletionService>();
        var recovery = provider.GetRequiredService<IInstanceLifecycleRecoveryService>();
        var created = (await instances.CreateAsync("DetachedData", "1.21.1")).Value!;
        var save = Path.Combine(
            environment.Paths.GetInstanceGameDirectory(created.Id),
            "saves",
            "world",
            "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        await File.WriteAllTextAsync(save, "world");

        var plan = await deletions.PlanAsync(created.Id, mode: InstanceDeletionMode.RemoveRecordOnly);
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        var deleted = await deletions.DeleteAsync(plan.Value!);
        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.Equal(InstanceDeletionState.Purged, deleted.Value!.Journal.State);

        var orphanTrash = Path.Combine(environment.Paths.GameRoot, ".ecl-trash", "ghost-instance-plan");
        Directory.CreateDirectory(orphanTrash);

        var inspected = await recovery.InspectAsync();
        Assert.True(inspected.IsSuccess, inspected.Error?.ToString());
        var report = inspected.Value!;
        Assert.Contains(report.Orphans, item => item.Kind == "quarantine" && item.Path.Contains("ghost-instance"));
        Assert.Contains(
            report.Orphans,
            item => item.Kind == "detached_game_data" &&
                    string.Equals(item.InstanceId, created.Id, StringComparison.Ordinal));
        Assert.DoesNotContain(report.Orphans, item => item.Kind == "quarantine" && item.InstanceId == created.Id);
    }

    [Fact]
    public async Task CancelledMoveKeepsStagingAndCanBeDiscarded()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var instances = provider.GetRequiredService<IInstanceService>();
        var moves = provider.GetRequiredService<IInstanceMoveService>();
        var recovery = provider.GetRequiredService<IInstanceLifecycleRecoveryService>();
        var primary = NewDirectory("primary");
        var target = NewDirectory("target");
        Assert.True((await workspace.InitializeAsync(primary)).IsSuccess);
        var targetRoot = await workspace.AddManagedRootAsync(target);
        Assert.True(targetRoot.IsSuccess, targetRoot.Error?.ToString());
        var created = (await instances.CreateAsync("Interrupted Move", "1.20.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        Directory.CreateDirectory(game);
        for (var index = 0; index < 4; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(game, $"file-{index}.dat"), $"data-{index}");
        }

        var plan = await moves.PlanAsync(created.Id, targetRoot.Value!.Id);
        Assert.True(plan.IsSuccess, plan.Error?.ToString());

        using var cancellation = new CancellationTokenSource();
        var result = await moves.ExecuteAsync(
            plan.Value!,
            new CancelOnFirstProgress(cancellation),
            cancellation.Token);
        Assert.False(result.IsSuccess);
        Assert.Equal("source.cancelled", result.Error!.Code);

        var journal = await moves.GetAsync(plan.Value!.PlanId);
        Assert.True(journal.IsSuccess, journal.Error?.ToString());
        Assert.True(Directory.Exists(environment.Paths.GetInstanceDirectory(created.Id)));

        var discarded = await recovery.DiscardAsync(plan.Value.PlanId);
        Assert.True(discarded.IsSuccess, discarded.Error?.ToString());
        Assert.Equal("Discarded", discarded.Value!.State);
        var staging = Path.Combine(target, "instances", $".ecl-migrate-{plan.Value.PlanId}");
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task CommittedMoveRetiredSourceIsCleanedThroughRecovery()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var instances = provider.GetRequiredService<IInstanceService>();
        var moves = provider.GetRequiredService<IInstanceMoveService>();
        var recovery = provider.GetRequiredService<IInstanceLifecycleRecoveryService>();
        var primary = NewDirectory("primary");
        var target = NewDirectory("target");
        Assert.True((await workspace.InitializeAsync(primary)).IsSuccess);
        var targetRoot = await workspace.AddManagedRootAsync(target);
        Assert.True(targetRoot.IsSuccess);
        var created = (await instances.CreateAsync("Move And Clean", "1.20.1")).Value!;
        var options = Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "options.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(options)!);
        await File.WriteAllTextAsync(options, "lang:zh_cn");

        var plan = await moves.PlanAsync(created.Id, targetRoot.Value!.Id);
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        var moved = await moves.ExecuteAsync(plan.Value!);
        Assert.True(moved.IsSuccess, moved.Error?.ToString());
        var retired = moved.Value!.RetiredSourcePath;
        Assert.NotNull(retired);
        Assert.True(Directory.Exists(retired));

        var inspected = await recovery.InspectAsync(created.Id);
        Assert.True(inspected.IsSuccess);
        Assert.Contains(inspected.Value!.Entries, entry => entry.Action == "cleanup_source");

        var cleaned = await recovery.CleanupSourceAsync(plan.Value!.PlanId);
        Assert.True(cleaned.IsSuccess, cleaned.Error?.ToString());
        Assert.False(Directory.Exists(retired));
        var journal = await moves.GetAsync(plan.Value.PlanId);
        Assert.True(journal.IsSuccess);
        Assert.Equal("SourceCleaned", journal.Value!.State);
        Assert.Null(journal.Value.RetiredSourcePath);
    }

    [Fact]
    public async Task ReconfigurePlanReportsNoChangeAndRunningSession()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var reconfigures = provider.GetRequiredService<IInstanceReconfigureService>();
        var sessions = provider.GetRequiredService<IGameSessionService>();
        var created = (await instances.CreateAsync("Reconfig Blocked", "1.21.1")).Value!;

        var same = await reconfigures.PlanAsync(created.Id, created.GameVersion, created.Loader);
        Assert.True(same.IsSuccess, same.Error?.ToString());
        Assert.Contains("instance.lifecycle.no_change", same.Value!.Blockers);

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

        var running = await reconfigures.PlanAsync(created.Id, "1.20.1", "vanilla");
        Assert.True(running.IsSuccess, running.Error?.ToString());
        Assert.Contains("instance.lifecycle.instance_running", running.Value!.Blockers);
    }

    [Fact]
    public async Task ReconfigureCommitWritesJournalAndVerifies()
    {
        using var environment = new TestEnvironment();
        var installer = new ScriptedInstaller();
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IInstanceConfigurationInstaller>(installer));
        installer.Instances = provider.GetRequiredService<IInstanceService>();
        installer.Paths = environment.Paths;
        installer.Mode = ScriptedInstaller.Behaviour.CommitEverything;
        var instances = installer.Instances;
        var reconfigures = provider.GetRequiredService<IInstanceReconfigureService>();
        var created = (await instances.CreateAsync("Reconfig Success", "1.21.1")).Value!;

        var plan = await reconfigures.PlanAsync(created.Id, "1.20.1", "vanilla");
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        Assert.Empty(plan.Value!.Blockers);

        var executed = await reconfigures.ExecuteAsync(plan.Value);
        Assert.True(executed.IsSuccess, executed.Error?.ToString());
        Assert.Equal("1.20.1", executed.Value!.Instance.GameVersion);
        Assert.NotNull(executed.Value.SnapshotId);
        Assert.Equal("Completed", executed.Value.Journal.State);
        Assert.Contains(executed.Value.Verification, item => item.StartsWith("ok:version json", StringComparison.Ordinal));

        var journal = await reconfigures.GetAsync(plan.Value.PlanId);
        Assert.True(journal.IsSuccess, journal.Error?.ToString());
        Assert.Equal("Completed", journal.Value!.State);
        Assert.Equal("1.21.1", journal.Value.MetadataRestorePoint!.GameVersion);

        var reloaded = await instances.GetAsync(created.Id);
        Assert.Equal("1.20.1", reloaded!.GameVersion);
    }

    [Fact]
    public async Task ReconfigureFailureWithoutMetadataIsRolledBackAndVerifiedMismatchNeedsRecovery()
    {
        using var environment = new TestEnvironment();
        var installer = new ScriptedInstaller();
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IInstanceConfigurationInstaller>(installer));
        installer.Instances = provider.GetRequiredService<IInstanceService>();
        installer.Paths = environment.Paths;
        installer.Mode = ScriptedInstaller.Behaviour.FailWithoutMetadata;
        var instances = installer.Instances;
        var reconfigures = provider.GetRequiredService<IInstanceReconfigureService>();
        var created = (await instances.CreateAsync("Reconfig Fail", "1.21.1")).Value!;

        var plan = await reconfigures.PlanAsync(created.Id, "1.20.1", "vanilla");
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        var failed = await reconfigures.ExecuteAsync(plan.Value!);
        Assert.False(failed.IsSuccess);
        Assert.Equal("install.download_failed", failed.Error!.Code);
        var journal = await reconfigures.GetAsync(plan.Value!.PlanId);
        Assert.True(journal.IsSuccess, journal.Error?.ToString());
        Assert.Equal("RolledBack", journal.Value!.State);
        Assert.Equal("1.21.1", (await instances.GetAsync(created.Id))!.GameVersion);

        installer.Mode = ScriptedInstaller.Behaviour.CommitWithoutVersionJson;
        var plan2 = await reconfigures.PlanAsync(created.Id, "1.20.1", "vanilla");
        Assert.True(plan2.IsSuccess, plan2.Error?.ToString());
        var mismatch = await reconfigures.ExecuteAsync(plan2.Value!);
        Assert.False(mismatch.IsSuccess);
        Assert.Equal("instance.lifecycle.needs_recovery", mismatch.Error!.Code);
        var journal2 = await reconfigures.GetAsync(plan2.Value!.PlanId);
        Assert.True(journal2.IsSuccess);
        Assert.Equal("NeedsRecovery", journal2.Value!.State);
    }

    [Fact]
    public async Task ReconfigureRejectsExternalInstanceWithoutConfirmation()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var store = provider.GetRequiredService<IInstanceStore>();
        var reconfigures = provider.GetRequiredService<IInstanceReconfigureService>();
        var external = NewDirectory("external-game");
        await store.SaveAsync(new GameInstance
        {
            Id = "external-reconfigure",
            Name = "External Reconfigure",
            GameVersion = "1.20.1",
            VersionId = "1.20.1",
            GameDirectory = external,
        });

        var blocked = await reconfigures.PlanAsync("external-reconfigure", "1.21.1", "vanilla");
        Assert.True(blocked.IsSuccess, blocked.Error?.ToString());
        Assert.Contains("instance.lifecycle.external_write_requires_confirmation", blocked.Value!.Blockers);

        var confirmed = await reconfigures.PlanAsync(
            "external-reconfigure",
            "1.21.1",
            "vanilla",
            externalWriteConfirmed: true);
        Assert.True(confirmed.IsSuccess, confirmed.Error?.ToString());
        Assert.DoesNotContain("instance.lifecycle.external_write_requires_confirmation", confirmed.Value!.Blockers);
        Assert.True(await instances.GetAsync("external-reconfigure") is not null);
    }

    private static string NewDirectory(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "ecl-lifecycle-recovery", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CancelOnFirstProgress(CancellationTokenSource cancellation)
        : IProgress<InstanceMoveProgress>
    {
        public void Report(InstanceMoveProgress value) => cancellation.Cancel();
    }

    /// <summary>可编排的安装器：用于验证重配置 journal 在成功/失败/复验不符三种路径下的状态。</summary>
    private sealed class ScriptedInstaller : IInstanceConfigurationInstaller
    {
        public enum Behaviour
        {
            CommitEverything,
            FailWithoutMetadata,
            CommitWithoutVersionJson,
        }

        public Behaviour Mode { get; set; } = Behaviour.CommitEverything;

        public IInstanceService? Instances { get; set; }

        public AppPaths? Paths { get; set; }

        public async Task<InstallOutcome> ApplyConfigurationAsync(
            string instanceId,
            GameInstance target,
            IProgress<InstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            switch (Mode)
            {
                case Behaviour.FailWithoutMetadata:
                    return InstallOutcome.Fail(new InstallFailure(
                        new EclError("install.download_failed", "模拟下载失败"),
                        InstallRollbackResult.Empty,
                        MetadataRestored: true,
                        DiagnosticPath: null));
                default:
                    await Instances!.UpdateConfigurationAsync(
                        instanceId,
                        target.GameVersion,
                        target.Loader,
                        target.LoaderVersion,
                        target.FabricApiVersion,
                        target.OptiFineVersion,
                        cancellationToken);
                    if (Mode == Behaviour.CommitEverything)
                    {
                        var directory = System.IO.Path.Combine(Paths!.VersionsDirectory, target.VersionId);
                        Directory.CreateDirectory(directory);
                        await File.WriteAllTextAsync(
                            System.IO.Path.Combine(directory, $"{target.VersionId}.json"),
                            "{}",
                            cancellationToken);
                    }

                    return InstallOutcome.Ok(new InstallReport(target.VersionId, 1, 0, 16, TimeSpan.Zero));
            }
        }
    }
}
