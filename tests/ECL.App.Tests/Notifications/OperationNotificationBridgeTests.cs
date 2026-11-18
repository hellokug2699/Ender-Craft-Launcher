// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services.Notifications;
using Ecl.App.Tests.Support;
using Ecl.Core.Localization;
using Ecl.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Notifications;

/// <summary>
/// F-12 任务 → 通知映射：终态各发一次、取消不提示、进行中状态节流 30 秒。
/// 桥接只观察快照，不取消也不修改任务，这些断言同时验证它没有副作用。
/// </summary>
public sealed class OperationNotificationBridgeTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ILocalizer Localizer =
        new JsonLocalizer(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public void CompletedOperationPublishesOneCompletionNotification()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid();

        fixture.Raise(Snapshot(id, OperationState.Running));
        fixture.Raise(Snapshot(id, OperationState.Completed));

        var notification = Assert.Single(fixture.Service.Recent);
        Assert.Equal(AppNotificationCategory.TaskCompleted, notification.Category);
        Assert.Equal(AppNotificationSeverity.Success, notification.Severity);
        Assert.Contains("完成", notification.Message);
        Assert.Equal(id.ToString("N"), notification.CorrelationId);
    }

    [Fact]
    public void FailedOperationCarriesTheFailureReason()
    {
        using var fixture = new Fixture();

        fixture.Raise(Snapshot(
            Guid.NewGuid(),
            OperationState.Failed,
            error: new OperationError(OperationErrorKind.Network, "download.failed", "连接超时")));

        var notification = Assert.Single(fixture.Service.Recent);
        Assert.Equal(AppNotificationCategory.TaskFailed, notification.Category);
        Assert.Equal(AppNotificationSeverity.Error, notification.Severity);
        Assert.Contains("连接超时", notification.Message);
    }

    [Fact]
    public void CancelledOperationIsNotAnnounced()
    {
        using var fixture = new Fixture();

        fixture.Raise(Snapshot(Guid.NewGuid(), OperationState.Cancelled));

        Assert.Empty(fixture.Service.Recent);
    }

    [Fact]
    public void PausedOperationIsThrottledToOncePerThirtySeconds()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid();
        fixture.Raise(Snapshot(id, OperationState.Running));

        fixture.Raise(Snapshot(id, OperationState.Running, paused: true));
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        fixture.Raise(Snapshot(id, OperationState.Running, paused: true));

        var notification = Assert.Single(fixture.Service.Recent);
        Assert.Equal(AppNotificationCategory.BackgroundTask, notification.Category);
        Assert.Contains("暂停", notification.Message);

        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        fixture.Raise(Snapshot(id, OperationState.Running, paused: true));

        Assert.Equal(2, fixture.Service.Recent.Count);
    }

    [Fact]
    public void StalledAndRetryingMapToBackgroundTaskNotifications()
    {
        using var fixture = new Fixture();
        var stalled = Guid.NewGuid();
        var retrying = Guid.NewGuid();

        fixture.Raise(Snapshot(stalled, OperationState.Running, stalled: true));
        fixture.Raise(Snapshot(retrying, OperationState.Retrying));

        Assert.Equal(2, fixture.Service.Recent.Count);
        Assert.All(fixture.Service.Recent, item => Assert.Equal(AppNotificationCategory.BackgroundTask, item.Category));
        Assert.Contains(fixture.Service.Recent, item => item.Message.Contains("没有进度", StringComparison.Ordinal));
        Assert.Contains(fixture.Service.Recent, item => item.Message.Contains("重试", StringComparison.Ordinal));
    }

    [Fact]
    public void RunningProgressUpdatesDoNotNotify()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid();

        fixture.Raise(Snapshot(id, OperationState.Running));
        fixture.Raise(Snapshot(id, OperationState.Running));

        Assert.Empty(fixture.Service.Recent);
    }

    [Fact]
    public void DisposingTheBridgeStopsObserving()
    {
        var fixture = new Fixture();
        var id = Guid.NewGuid();
        fixture.Bridge.Dispose();
        fixture.Bridge.Dispose();

        fixture.Raise(Snapshot(id, OperationState.Completed));

        Assert.Empty(fixture.Service.Recent);
        fixture.Dispose();
    }

    private static OperationSnapshot Snapshot(
        Guid id,
        OperationState state,
        bool paused = false,
        bool stalled = false,
        OperationError? error = null) =>
        new()
        {
            Id = id,
            Kind = OperationKind.Download,
            Title = "资源包",
            State = state,
            IsPaused = paused,
            IsStalled = stalled,
            Error = error,
            StartedAt = Start,
            UpdatedAt = Start,
        };

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Store = new TestOperationStore();
            Time = new FakeTimeProvider(Start);
            Service = new NotificationService(
                new AlwaysEnabledPreferences(),
                NullLogger<NotificationService>.Instance,
                Time);
            Bridge = new OperationNotificationBridge(
                Store,
                Service,
                NullLogger<OperationNotificationBridge>.Instance,
                Localizer,
                Time);
        }

        public TestOperationStore Store { get; }

        public FakeTimeProvider Time { get; }

        public NotificationService Service { get; }

        public OperationNotificationBridge Bridge { get; }

        public void Raise(OperationSnapshot snapshot) => Store.Raise(snapshot);

        public void Dispose() => Bridge.Dispose();
    }

    private sealed class AlwaysEnabledPreferences : INotificationPreferences
    {
        public bool IsEnabled(AppNotificationCategory category) => true;
    }

    private sealed class TestOperationStore : IOperationStore
    {
        private readonly List<OperationSnapshot> _snapshots = [];

        public IReadOnlyList<OperationSnapshot> Snapshots => _snapshots;

        public event EventHandler<OperationSnapshot>? Changed;

        public OperationSnapshot? TryGet(Guid operationId) =>
            _snapshots.LastOrDefault(snapshot => snapshot.Id == operationId);

        public void Raise(OperationSnapshot snapshot)
        {
            _snapshots.Add(snapshot);
            Changed?.Invoke(this, snapshot);
        }
    }
}
