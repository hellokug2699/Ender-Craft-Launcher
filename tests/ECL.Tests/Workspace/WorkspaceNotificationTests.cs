// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Workspace;
using Xunit;

namespace Ecl.Tests.Workspace;

/// <summary>
/// 工作区状态通知契约：状态提交后按订阅顺序同步通知；非法迁移不通知；
/// 订阅者异常被隔离且不回滚状态；退订后不再收到通知。
/// </summary>
public sealed class WorkspaceNotificationTests
{
    private static WorkspaceEntry OpenReady(WorkspaceHost host)
    {
        var entry = host.Open(WorkspaceDescriptor.Create("home", "主页"));
        host.MarkReady(entry.Id);
        return entry;
    }

    [Fact]
    public void LifecycleTransitionEmitsExactlyOneNotification()
    {
        var host = new WorkspaceHost();
        var notifications = new List<WorkspaceChangeNotification>();
        host.Subscribe(notifications.Add);

        var entry = host.Open(WorkspaceDescriptor.Create("home", "主页"));

        var opened = Assert.Single(notifications);
        Assert.Equal(entry.Id, opened.WorkspaceId);
        Assert.Equal(WorkspaceChangeKind.LifecycleStateChanged, opened.Kind);
        Assert.Equal(WorkspaceLifecycleState.Created, opened.OldState);
        Assert.Equal(WorkspaceLifecycleState.Opening, opened.NewState);

        Assert.True(host.MarkReady(entry.Id));

        Assert.Equal(2, notifications.Count);
        Assert.Equal(WorkspaceLifecycleState.Opening, notifications[1].OldState);
        Assert.Equal(WorkspaceLifecycleState.Ready, notifications[1].NewState);
    }

    [Fact]
    public void InvalidTransitionEmitsNoNotification()
    {
        var host = new WorkspaceHost();
        var entry = OpenReady(host);
        var notifications = new List<WorkspaceChangeNotification>();
        host.Subscribe(notifications.Add);

        Assert.False(host.MarkReady(entry.Id));
        Assert.False(host.MarkSuspended(WorkspaceId.New()));

        Assert.Empty(notifications);
        Assert.Equal(WorkspaceLifecycleState.Ready, entry.State);
    }

    [Fact]
    public void DirtyStateChangesAreObservableOnlyWhenValueChanges()
    {
        var host = new WorkspaceHost();
        var entry = OpenReady(host);
        var notifications = new List<WorkspaceChangeNotification>();
        host.Subscribe(notifications.Add);

        Assert.True(host.MarkDirty(entry.Id, true));
        Assert.True(host.MarkDirty(entry.Id, true));
        Assert.True(host.MarkDirty(entry.Id, false));

        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, notification => Assert.Equal(WorkspaceChangeKind.DirtyStateChanged, notification.Kind));
        Assert.False(notifications[0].OldIsDirty);
        Assert.True(notifications[0].NewIsDirty);
        Assert.True(notifications[1].OldIsDirty);
        Assert.False(notifications[1].NewIsDirty);
    }

    [Fact]
    public void SuspendedWorkspaceActivationEmitsReadyNotification()
    {
        var host = new WorkspaceHost();
        var entry = OpenReady(host);
        host.Activate(entry.Id);
        Assert.True(host.MarkSuspended(entry.Id));

        var notifications = new List<WorkspaceChangeNotification>();
        host.Subscribe(notifications.Add);

        Assert.True(host.Activate(entry.Id));

        var resumed = Assert.Single(notifications);
        Assert.Equal(WorkspaceLifecycleState.Suspended, resumed.OldState);
        Assert.Equal(WorkspaceLifecycleState.Ready, resumed.NewState);
    }

    [Fact]
    public void SubscribersAreNotifiedInRegistrationOrder()
    {
        var host = new WorkspaceHost();
        var entry = OpenReady(host);
        var order = new List<string>();
        host.Subscribe(_ => order.Add("first"));
        host.Subscribe(_ => order.Add("second"));
        host.Subscribe(_ => order.Add("third"));

        Assert.True(host.MarkSuspended(entry.Id));

        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public void UnsubscribedConsumerNoLongerReceivesNotifications()
    {
        var host = new WorkspaceHost();
        var entry = OpenReady(host);
        var first = new List<WorkspaceChangeNotification>();
        var second = new List<WorkspaceChangeNotification>();
        var firstSubscription = host.Subscribe(first.Add);
        host.Subscribe(second.Add);

        firstSubscription.Dispose();
        Assert.True(host.MarkSuspended(entry.Id));

        Assert.Empty(first);
        Assert.Single(second);
    }

    [Fact]
    public void SubscriberFailureIsIsolatedAndDoesNotRollBackState()
    {
        var host = new WorkspaceHost();
        var entry = OpenReady(host);
        var received = new List<WorkspaceChangeNotification>();
        host.Subscribe(_ => throw new InvalidOperationException("订阅者失败"));
        host.Subscribe(received.Add);

        Assert.True(host.MarkSuspended(entry.Id));

        Assert.Equal(WorkspaceLifecycleState.Suspended, entry.State);
        Assert.Single(received);
        Assert.Equal(WorkspaceLifecycleState.Suspended, received[0].NewState);
    }

    [Fact]
    public void NotificationsAreRaisedSynchronouslyOnCallingThread()
    {
        var host = new WorkspaceHost();
        var entry = OpenReady(host);
        var expectedThread = Environment.CurrentManagedThreadId;
        var observedThreads = new List<int>();
        host.Subscribe(_ => observedThreads.Add(Environment.CurrentManagedThreadId));

        host.MarkSuspended(entry.Id);

        Assert.Equal([expectedThread], observedThreads);
    }

    [Fact]
    public void CloseEmitsClosingAndDisposedBeforeClosedEvent()
    {
        var host = new WorkspaceHost();
        var entry = OpenReady(host);
        var sequence = new List<string>();
        host.Subscribe(notification =>
            sequence.Add($"{notification.Kind}:{notification.OldState}->{notification.NewState}"));
        host.WorkspaceClosed += (_, _) => sequence.Add("closed-event");

        var attempt = host.Close(entry.Id);

        Assert.True(attempt.Closed);
        Assert.Equal(
            ["LifecycleStateChanged:Ready->Closing", "LifecycleStateChanged:Closing->Disposed", "closed-event"],
            sequence);
    }

    [Fact]
    public void FaultedStateIsObservable()
    {
        var host = new WorkspaceHost();
        var entry = OpenReady(host);
        var notifications = new List<WorkspaceChangeNotification>();
        host.Subscribe(notifications.Add);

        Assert.True(host.MarkFaulted(entry.Id, WorkspaceLifecycleState.Crashed));

        var crashed = Assert.Single(notifications);
        Assert.Equal(WorkspaceLifecycleState.Crashed, crashed.NewState);
    }
}
