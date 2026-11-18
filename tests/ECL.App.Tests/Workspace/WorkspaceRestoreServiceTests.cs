// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Ecl.App.Services.Lifecycle;
using Ecl.App.Tests.Support;
using Ecl.Core;
using Ecl.Core.Workspace.Session;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// F-12 工作区会话恢复：没有快照不询问、失效实例给出原因、损坏/不兼容快照回退而不抛异常、
/// 单项恢复失败不影响其余条目。
/// </summary>
public sealed class WorkspaceRestoreServiceTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MissingSnapshotIsReportedAsNoneAndNeverAsksTheUser()
    {
        var store = new FakeSessionStore { Status = WorkspaceSessionLoadStatus.None };
        var service = Create(store, out var target);

        var plan = service.Inspect();

        Assert.Equal(WorkspaceRestoreAvailability.None, plan.Availability);
        Assert.Empty(plan.Candidates);
        Assert.Equal(0, plan.Skipped);

        var result = await service.RestoreAsync(plan);
        Assert.Equal(0, result.Restored);
        Assert.Empty(result.Failures);
        Assert.Empty(target.Entries);
    }

    [Fact]
    public void ValidEntriesAreRestorableAndMissingInstancesCarryAReason()
    {
        var store = new FakeSessionStore
        {
            Snapshot = Snapshot(
                new WorkspaceSessionEntry("mods", null, "mods"),
                new WorkspaceSessionEntry(null, "instance-a", null),
                new WorkspaceSessionEntry(null, "instance-gone", null)),
        };
        var service = Create(store, out _, out var instances);
        instances.Instances.Add(new GameInstance
        {
            Id = "instance-a",
            Name = "生存",
            GameVersion = "1.21.1",
            VersionId = "1.21.1",
        });

        var plan = service.Inspect();

        Assert.Equal(WorkspaceRestoreAvailability.Available, plan.Availability);
        Assert.Equal(3, plan.Candidates.Count);
        Assert.True(plan.Candidates[0].CanRestore);
        Assert.True(plan.Candidates[1].CanRestore);
        Assert.False(plan.Candidates[2].CanRestore);
        Assert.Equal(WorkspaceRestoreService.ReasonInstanceMissing, plan.Candidates[2].Reason);
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void EmptyEntryIsSkippedInsteadOfClaimingRestore()
    {
        var store = new FakeSessionStore
        {
            Snapshot = Snapshot(new WorkspaceSessionEntry(null, null, null)),
        };
        var service = Create(store, out _);

        var plan = service.Inspect();

        var candidate = Assert.Single(plan.Candidates);
        Assert.False(candidate.CanRestore);
        Assert.Equal(WorkspaceRestoreService.ReasonEntryEmpty, candidate.Reason);
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void JsonStoreRoundTripsEntriesAndReportsAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ecl-f12-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonWorkspaceSessionStore(
                new AppPaths(root),
                NullLogger<JsonWorkspaceSessionStore>.Instance);
            store.Save(Snapshot(
                new WorkspaceSessionEntry("mods", null, "mods"),
                new WorkspaceSessionEntry(null, "instance-a", null)));

            var loaded = store.Load();

            Assert.Equal(WorkspaceSessionLoadStatus.Available, store.LastLoadStatus);
            Assert.NotNull(loaded);
            Assert.Equal(WorkspaceSessionSnapshot.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(2, loaded.Open.Count);
            Assert.Equal("mods", loaded.Open[0].ToolId);
            Assert.Equal("instance-a", loaded.Open[1].InstanceId);
            Assert.Equal("home", loaded.NavigationDestination);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptedSnapshotIsReportedAsUnreadableWithoutThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ecl-f12-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var sessionStore = new JsonWorkspaceSessionStore(
                paths,
                NullLogger<JsonWorkspaceSessionStore>.Instance);
            Directory.CreateDirectory(paths.ConfigDirectory);
            File.WriteAllText(Path.Combine(paths.ConfigDirectory, JsonWorkspaceSessionStore.FileName), "{ 不是 json");

            var service = new WorkspaceRestoreService(
                sessionStore,
                new FakeInstanceService(),
                new RecordingTarget(),
                NullLogger<WorkspaceRestoreService>.Instance);

            var plan = service.Inspect();

            Assert.Equal(WorkspaceRestoreAvailability.Unreadable, plan.Availability);
            Assert.Empty(plan.Candidates);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IncompatibleSnapshotVersionIsReportedSeparately()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ecl-f12-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var sessionStore = new JsonWorkspaceSessionStore(
                paths,
                NullLogger<JsonWorkspaceSessionStore>.Instance);
            Directory.CreateDirectory(paths.ConfigDirectory);
            File.WriteAllText(
                Path.Combine(paths.ConfigDirectory, JsonWorkspaceSessionStore.FileName),
                """
                {"schemaVersion":2,"open":[],"activeIndex":0,"capturedAt":"2026-01-01T00:00:00+00:00"}
                """);

            var service = new WorkspaceRestoreService(
                sessionStore,
                new FakeInstanceService(),
                new RecordingTarget(),
                NullLogger<WorkspaceRestoreService>.Instance);

            var plan = service.Inspect();

            Assert.Equal(WorkspaceRestoreAvailability.Incompatible, plan.Availability);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreFailureOnOneEntryDoesNotBlockTheOthers()
    {
        var store = new FakeSessionStore
        {
            Snapshot = Snapshot(
                new WorkspaceSessionEntry(null, "instance-a", null),
                new WorkspaceSessionEntry(null, "instance-b", null)),
        };
        var service = Create(store, out var target, out var instances);
        instances.Instances.Add(new GameInstance
        {
            Id = "instance-a",
            Name = "A",
            GameVersion = "1.21.1",
            VersionId = "1.21.1",
        });
        instances.Instances.Add(new GameInstance
        {
            Id = "instance-b",
            Name = "B",
            GameVersion = "1.21.1",
            VersionId = "1.21.1",
        });
        target.FailForInstanceId = "instance-a";

        var plan = service.Inspect();
        var result = await service.RestoreAsync(plan);

        Assert.Equal(1, result.Restored);
        Assert.False(result.IsComplete);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("instance-a", failure.InstanceId);
        Assert.Equal(WorkspaceRestoreService.ReasonApplyFailed, failure.Reason);
        Assert.Equal("instance-b", Assert.Single(target.Entries).InstanceId);
    }

    [Fact]
    public async Task MissingRestoreTargetIsReportedPerEntryInsteadOfPretendingSuccess()
    {
        var store = new FakeSessionStore
        {
            Snapshot = Snapshot(new WorkspaceSessionEntry("mods", null, "mods")),
        };
        var service = new WorkspaceRestoreService(
            store,
            new FakeInstanceService(),
            target: null,
            NullLogger<WorkspaceRestoreService>.Instance);

        var plan = service.Inspect();
        var result = await service.RestoreAsync(plan);

        Assert.Equal(0, result.Restored);
        Assert.Equal(
            WorkspaceRestoreService.ReasonTargetUnavailable,
            Assert.Single(result.Failures).Reason);
    }

    [Fact]
    public async Task CancellationStopsRestoringRemainingEntries()
    {
        var store = new FakeSessionStore
        {
            Snapshot = Snapshot(
                new WorkspaceSessionEntry("mods", null, "mods"),
                new WorkspaceSessionEntry("logs", null, "logs")),
        };
        var service = Create(store, out var target);
        var plan = service.Inspect();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RestoreAsync(plan, cancellation.Token));
        Assert.Empty(target.Entries);
    }

    private static WorkspaceRestoreService Create(FakeSessionStore store, out RecordingTarget target)
        => Create(store, out target, out _);

    private static WorkspaceRestoreService Create(
        FakeSessionStore store,
        out RecordingTarget target,
        out FakeInstanceService instances)
    {
        target = new RecordingTarget();
        instances = new FakeInstanceService();
        return new WorkspaceRestoreService(
            store,
            instances,
            target,
            NullLogger<WorkspaceRestoreService>.Instance);
    }

    private static WorkspaceSessionSnapshot Snapshot(params WorkspaceSessionEntry[] entries) =>
        new(
            WorkspaceSessionSnapshot.CurrentSchemaVersion,
            entries,
            0,
            "home",
            CapturedAt);

    private sealed class FakeSessionStore : IWorkspaceSessionStore
    {
        public WorkspaceSessionSnapshot? Snapshot { get; set; }

        public WorkspaceSessionLoadStatus Status { get; set; } = WorkspaceSessionLoadStatus.Available;

        public WorkspaceSessionLoadStatus LastLoadStatus { get; private set; } = WorkspaceSessionLoadStatus.None;

        public WorkspaceSessionSnapshot? Load()
        {
            LastLoadStatus = Snapshot is null ? Status : WorkspaceSessionLoadStatus.Available;
            return Snapshot;
        }

        public void Save(WorkspaceSessionSnapshot snapshot) => Snapshot = snapshot;

        public void Clear() => Snapshot = null;
    }

    private sealed class RecordingTarget : IWorkspaceRestoreTarget
    {
        public List<WorkspaceSessionEntry> Entries { get; } = [];

        public string? FailForInstanceId { get; set; }

        public void Restore(WorkspaceSessionEntry entry)
        {
            if (FailForInstanceId is not null &&
                string.Equals(entry.InstanceId, FailForInstanceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("restore boom");
            }

            Entries.Add(entry);
        }
    }
}
