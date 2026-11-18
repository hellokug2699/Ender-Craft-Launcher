// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Dialogs;
using Ecl.App.Services.Confirmation;
using Ecl.App.Services.Lifecycle;
using Ecl.App.Shell.Lifecycle;
using Ecl.App.Tests.Support;
using Ecl.Core.Localization;
using Ecl.Core.Workspace.Session;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Lifecycle;

/// <summary>
/// F-12 启动恢复询问：没有快照不询问、有效快照才询问、拒绝不恢复、
/// 部分条目不可恢复/恢复失败时用关键结果弹窗逐项说明。
/// </summary>
public sealed class StartupRestorePromptTests
{
    private static readonly ILocalizer Localizer =
        new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task NoSnapshotNeverAsksAndNeverRestores()
    {
        var fixture = new Fixture(WorkspaceSessionLoadStatus.None);

        var result = await fixture.Prompt.RunAsync();

        Assert.Null(result);
        Assert.Empty(fixture.Dialogs.Requests);
        Assert.Empty(fixture.Target.Entries);
    }

    [Fact]
    public async Task AcceptingRestoresEveryRestorableEntry()
    {
        var fixture = new Fixture(WorkspaceSessionLoadStatus.Available);
        fixture.Instances.Instances.Add(Instance("instance-a"));
        fixture.Store.Snapshot = Snapshot(
            new WorkspaceSessionEntry(null, "instance-a", null),
            new WorkspaceSessionEntry("settings", null, "settings"));
        fixture.Dialogs.Results.Enqueue(new DialogResult(DialogOutcome.Primary));

        var result = await fixture.Prompt.RunAsync();

        Assert.NotNull(result);
        Assert.Equal(2, result.Restored);
        Assert.Empty(result.Failures);
        Assert.Equal(2, fixture.Target.Entries.Count);
        Assert.Empty(fixture.Results.Requests);
    }

    [Fact]
    public async Task DecliningLeavesAllWorkspacesUntouched()
    {
        var fixture = new Fixture(WorkspaceSessionLoadStatus.Available);
        fixture.Instances.Instances.Add(Instance("instance-a"));
        fixture.Store.Snapshot = Snapshot(new WorkspaceSessionEntry(null, "instance-a", null));
        fixture.Dialogs.Results.Enqueue(new DialogResult(DialogOutcome.Secondary));

        var result = await fixture.Prompt.RunAsync();

        Assert.Null(result);
        Assert.Empty(fixture.Target.Entries);
        Assert.Empty(fixture.Results.Requests);
    }

    [Fact]
    public async Task MissingInstanceIsExplainedPerItemWithACriticalResultDialog()
    {
        var fixture = new Fixture(WorkspaceSessionLoadStatus.Available);
        fixture.Instances.Instances.Add(Instance("instance-a"));
        fixture.Store.Snapshot = Snapshot(
            new WorkspaceSessionEntry(null, "instance-a", null),
            new WorkspaceSessionEntry(null, "instance-gone", null));
        fixture.Dialogs.Results.Enqueue(new DialogResult(DialogOutcome.Primary));

        var result = await fixture.Prompt.RunAsync();

        Assert.NotNull(result);
        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Skipped);
        var request = Assert.Single(fixture.Results.Requests);
        Assert.True(request.IsCritical);
        Assert.Contains("instance-gone", request.Message, StringComparison.Ordinal);
        Assert.Contains("已恢复 1 个，跳过 1 个，失败 0 个", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreFailureIsReportedWithoutRollingBackSuccessfulEntries()
    {
        var fixture = new Fixture(WorkspaceSessionLoadStatus.Available);
        fixture.Instances.Instances.Add(Instance("instance-a"));
        fixture.Instances.Instances.Add(Instance("instance-b"));
        fixture.Store.Snapshot = Snapshot(
            new WorkspaceSessionEntry(null, "instance-a", null),
            new WorkspaceSessionEntry(null, "instance-b", null));
        fixture.Dialogs.Results.Enqueue(new DialogResult(DialogOutcome.Primary));
        fixture.Target.FailForInstanceId = "instance-a";

        var result = await fixture.Prompt.RunAsync();

        Assert.NotNull(result);
        Assert.Equal(1, result.Restored);
        Assert.False(result.IsComplete);
        var request = Assert.Single(fixture.Results.Requests);
        Assert.True(request.IsCritical);
        Assert.Contains("instance-a", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnavailableDialogHostSkipsRestoreInsteadOfRestoringSilently()
    {
        var fixture = new Fixture(WorkspaceSessionLoadStatus.Available);
        fixture.Instances.Instances.Add(Instance("instance-a"));
        fixture.Store.Snapshot = Snapshot(new WorkspaceSessionEntry(null, "instance-a", null));
        fixture.Dialogs.IsAvailable = false;

        var result = await fixture.Prompt.RunAsync();

        Assert.Null(result);
        Assert.Empty(fixture.Target.Entries);
    }

    [Fact]
    public async Task UnreadableSnapshotIsNotPresentedAsAnEmptySession()
    {
        var fixture = new Fixture(WorkspaceSessionLoadStatus.Unreadable);

        var result = await fixture.Prompt.RunAsync();

        Assert.Null(result);
        Assert.Empty(fixture.Dialogs.Requests);
    }

    private static GameInstance Instance(string id) => new()
    {
        Id = id,
        Name = id,
        GameVersion = "1.21.1",
        VersionId = "1.21.1",
    };

    private static WorkspaceSessionSnapshot Snapshot(params WorkspaceSessionEntry[] entries) =>
        new(
            WorkspaceSessionSnapshot.CurrentSchemaVersion,
            entries,
            0,
            "home",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class Fixture
    {
        public Fixture(WorkspaceSessionLoadStatus status)
        {
            Store = new FakeSessionStore { Status = status };
            Instances = new FakeInstanceService();
            Target = new RecordingTarget();
            Restore = new WorkspaceRestoreService(
                Store,
                Instances,
                Target,
                NullLogger<WorkspaceRestoreService>.Instance);
            Prompt = new StartupRestorePrompt(
                Restore,
                Dialogs,
                Results,
                Localizer,
                NullLogger<StartupRestorePrompt>.Instance);
        }

        public FakeSessionStore Store { get; }

        public FakeInstanceService Instances { get; }

        public RecordingTarget Target { get; }

        public WorkspaceRestoreService Restore { get; }

        public FakeDialogEngine Dialogs { get; } = new();

        public FakeResultDialogService Results { get; } = new();

        public StartupRestorePrompt Prompt { get; }
    }

    private sealed class FakeSessionStore : IWorkspaceSessionStore
    {
        public WorkspaceSessionSnapshot? Snapshot { get; set; }

        public WorkspaceSessionLoadStatus Status { get; set; }

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

    private sealed class FakeResultDialogService : IResultDialogService
    {
        public List<ResultDialogRequest> Requests { get; } = [];

        public Task<DialogResult> ShowResultAsync(
            ResultDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(DialogResult.Closed);
        }
    }
}
