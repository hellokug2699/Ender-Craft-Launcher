// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Workspace;
using Xunit;

namespace Ecl.Tests.Workspace;

public sealed class WorkspaceHostTests
{
    private static WorkspaceDescriptor Descriptor(string toolId, string? target = null) =>
        WorkspaceDescriptor.Create(toolId, $"{toolId} 工作区", target);

    private static (WorkspaceHost Host, WorkspaceEntry First, WorkspaceEntry Second) CreateHostWithTwoWorkspaces()
    {
        var host = new WorkspaceHost();
        var first = host.Open(Descriptor("home"));
        host.MarkReady(first.Id);
        host.Activate(first.Id);

        var second = host.Open(Descriptor("instances", "survival-a1b2c3"));
        host.MarkReady(second.Id);
        host.Activate(second.Id);
        return (host, first, second);
    }

    [Fact]
    public void OpenRegistersWorkspaceInOpeningState()
    {
        var host = new WorkspaceHost();

        var entry = host.Open(Descriptor("home"));

        Assert.Equal(WorkspaceLifecycleState.Opening, entry.State);
        Assert.Same(entry, Assert.Single(host.Workspaces));
        Assert.Null(host.Active);
        Assert.True(host.Layout.IsEmpty);
    }

    [Fact]
    public void DuplicateWorkspaceIdIsRejected()
    {
        var host = new WorkspaceHost();
        var descriptor = Descriptor("home");
        host.Open(descriptor);

        Assert.Throws<InvalidOperationException>(() => host.Open(descriptor));
    }

    [Fact]
    public void OpeningWorkspaceIsNotActivatableUntilReady()
    {
        var host = new WorkspaceHost();
        var entry = host.Open(Descriptor("home"));

        Assert.False(host.Activate(entry.Id));
        Assert.True(host.MarkReady(entry.Id));
        Assert.True(host.Activate(entry.Id));
        Assert.Same(entry, host.Active);
        Assert.Equal(new[] { entry.Id }, host.Layout.Workspaces);
    }

    [Fact]
    public void ActivatingAnotherWorkspaceReplacesSingleVisibleLayout()
    {
        var (host, first, second) = CreateHostWithTwoWorkspaces();

        Assert.Same(second, host.Active);
        Assert.Equal(new[] { second.Id }, host.Layout.Workspaces);

        Assert.True(host.Activate(first.Id));
        Assert.Equal(new[] { first.Id }, host.Layout.Workspaces);
        Assert.Equal(2, host.Workspaces.Count);
    }

    [Fact]
    public void ActivateRaisesEventOnlyWhenActiveWorkspaceChanges()
    {
        var (host, first, _) = CreateHostWithTwoWorkspaces();
        var activated = new List<WorkspaceId>();
        host.WorkspaceActivated += (_, args) => activated.Add(args.Entry.Id);

        Assert.True(host.Activate(first.Id));
        Assert.True(host.Activate(first.Id));

        Assert.Equal(new[] { first.Id }, activated);
    }

    [Fact]
    public void SuspendedWorkspaceResumesOnActivate()
    {
        var host = new WorkspaceHost();
        var entry = host.Open(Descriptor("home"));
        host.MarkReady(entry.Id);
        host.Activate(entry.Id);

        Assert.True(host.MarkSuspended(entry.Id));
        Assert.Equal(WorkspaceLifecycleState.Suspended, entry.State);

        Assert.True(host.Activate(entry.Id));
        Assert.Equal(WorkspaceLifecycleState.Ready, entry.State);
    }

    [Fact]
    public void CloseRemovesWorkspaceAndActivatesNeighbour()
    {
        var (host, first, second) = CreateHostWithTwoWorkspaces();
        var closed = new List<WorkspaceDescriptor>();
        host.WorkspaceClosed += (_, args) => closed.Add(args.Descriptor);

        var attempt = host.Close(second.Id);

        Assert.True(attempt.Closed);
        Assert.Equal(WorkspaceLifecycleState.Disposed, second.State);
        Assert.Same(first, host.Active);
        Assert.Equal(new[] { first.Id }, host.Layout.Workspaces);
        Assert.Single(closed);
        Assert.Equal(second.Descriptor.Id, closed[0].Id);
    }

    [Fact]
    public void ClosingLastWorkspaceClearsActiveAndLayout()
    {
        var host = new WorkspaceHost();
        var entry = host.Open(Descriptor("home"));
        host.MarkReady(entry.Id);
        host.Activate(entry.Id);

        var attempt = host.Close(entry.Id);

        Assert.True(attempt.Closed);
        Assert.Null(host.Active);
        Assert.Empty(host.Workspaces);
        Assert.True(host.Layout.IsEmpty);
    }

    [Fact]
    public void DirtyWorkspaceRequiresConfirmationBeforeClosing()
    {
        var host = new WorkspaceHost();
        var entry = host.Open(Descriptor("nbt"));
        host.MarkReady(entry.Id);
        host.Activate(entry.Id);
        host.MarkDirty(entry.Id, true);

        var decision = host.RequestClose(entry.Id);
        Assert.True(decision.CanClose);
        Assert.True(decision.RequiresConfirmation);
        Assert.Equal(WorkspaceCloseReason.UnsavedChanges, decision.Reason);

        var refused = host.Close(entry.Id);
        Assert.False(refused.Closed);
        Assert.Single(host.Workspaces);

        var accepted = host.Close(entry.Id, confirmed: true);
        Assert.True(accepted.Closed);
        Assert.Empty(host.Workspaces);
    }

    [Fact]
    public void ClosingAlreadyClosedWorkspaceIsRefused()
    {
        var host = new WorkspaceHost();
        var entry = host.Open(Descriptor("home"));
        host.MarkReady(entry.Id);
        host.Close(entry.Id);

        var attempt = host.Close(entry.Id);

        Assert.False(attempt.Closed);
        Assert.Equal(WorkspaceCloseReason.AlreadyClosed, attempt.Decision.Reason);
    }

    [Fact]
    public void ClosingWorkspaceDoesNotAffectOtherWorkspacesOrTheirState()
    {
        var (host, first, second) = CreateHostWithTwoWorkspaces();
        host.MarkDirty(first.Id, true);

        host.Close(second.Id);

        Assert.True(first.IsDirty);
        Assert.Equal(WorkspaceLifecycleState.Ready, first.State);
        Assert.Same(first, host.Find(first.Id));
    }

    [Fact]
    public void MarkDirtyOnClosedWorkspaceIsRejected()
    {
        var host = new WorkspaceHost();
        var entry = host.Open(Descriptor("home"));
        host.MarkReady(entry.Id);
        host.Close(entry.Id);

        Assert.False(host.MarkDirty(entry.Id, true));
    }

    [Fact]
    public void FaultedWorkspaceCanStillBeActivatedForErrorPresentation()
    {
        var (host, first, _) = CreateHostWithTwoWorkspaces();

        Assert.True(host.MarkFaulted(first.Id, WorkspaceLifecycleState.Crashed));
        Assert.True(host.Activate(first.Id));
        Assert.True(first.IsFaulted);
        Assert.Same(first, host.Active);
    }

    [Fact]
    public void InvalidFaultStateIsRejected()
    {
        var host = new WorkspaceHost();
        var entry = host.Open(Descriptor("home"));

        Assert.Throws<ArgumentOutOfRangeException>(() => host.MarkFaulted(entry.Id, WorkspaceLifecycleState.Ready));
    }

    [Fact]
    public void TrySetLayoutRejectsUnknownWorkspace()
    {
        var host = new WorkspaceHost();
        var entry = host.Open(Descriptor("home"));
        host.MarkReady(entry.Id);
        host.Activate(entry.Id);

        var layout = WorkspaceLayout.Single(entry.Id)
            .AddLeaf(entry.Id, WorkspaceId.New(), WorkspaceSplitOrientation.Horizontal);

        Assert.False(host.TrySetLayout(layout));
        Assert.Equal(new[] { entry.Id }, host.Layout.Workspaces);
    }

    [Fact]
    public void TrySetLayoutAcceptsSplitOfOpenWorkspaces()
    {
        var (host, first, second) = CreateHostWithTwoWorkspaces();
        var layout = WorkspaceLayout.Single(first.Id)
            .AddLeaf(first.Id, second.Id, WorkspaceSplitOrientation.Vertical);

        Assert.True(host.TrySetLayout(layout));
        Assert.Equal(new[] { first.Id, second.Id }, host.Layout.Workspaces);
        Assert.Same(second, host.Active);
    }

    [Fact]
    public void FindByTargetMatchesToolAndOptionalResource()
    {
        var (host, _, second) = CreateHostWithTwoWorkspaces();

        Assert.Same(second, host.FindByTarget("instances", "survival-a1b2c3"));
        Assert.Null(host.FindByTarget("instances", "other-instance"));
        Assert.Null(host.FindByTarget("home", "survival-a1b2c3"));
    }
}
