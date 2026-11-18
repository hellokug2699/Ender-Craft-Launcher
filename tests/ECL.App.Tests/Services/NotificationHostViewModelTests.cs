// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Services.Notifications;
using Ecl.App.Shell.Notifications;
using Ecl.App.Tests.Support;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Services;

/// <summary>
/// F-12 应用内通知宿主：最多 3 条、6 秒自动消失、点击导航、后台线程封送与退订。
/// 计时由 <see cref="FakeTimeProvider"/> 驱动，测试不真正等待。
/// </summary>
public sealed class NotificationHostViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AtMostThreeNotificationsAreVisibleNewestFirst()
    {
        using var host = new Host();

        for (var index = 1; index <= 4; index++)
        {
            host.Publish($"task:{index}", AppNotificationCategory.TaskCompleted, $"结果 {index}");
        }

        host.Drain();

        var items = host.Items();
        Assert.Equal(NotificationHostViewModel.MaxVisible, items.Count);
        Assert.Equal("task:4", items[0].Id);
        Assert.Equal("task:3", items[1].Id);
        Assert.Equal("task:2", items[2].Id);
    }

    [Fact]
    public void NotificationDisappearsAfterSixSeconds()
    {
        using var host = new Host();
        host.Publish("task:1", AppNotificationCategory.TaskCompleted, "完成");

        host.Drain();
        Assert.Single(host.Items());

        host.Time.Advance(NotificationHostViewModel.DefaultLifetime);
        host.WaitUntilEmpty();

        Assert.Empty(host.Items());
    }

    [Fact]
    public void ClickNavigatesToTheDestinationAndRemovesTheNotification()
    {
        using var host = new Host();
        host.Publish(
            "update:1",
            AppNotificationCategory.BackgroundTask,
            "发现新版本",
            NotificationDestinations.About);
        host.Drain();

        var item = Assert.Single(host.Items());
        host.Ui.Invoke(() => host.ViewModel.ActivateCommand.Execute(item));
        host.Drain();

        Assert.Equal([NotificationDestinations.About], host.Navigated);
        Assert.Empty(host.Items());
    }

    [Fact]
    public void ClickWithoutDestinationOnlyDismisses()
    {
        using var host = new Host();
        host.Publish("hint:1", AppNotificationCategory.Hint, "提示");
        host.Drain();

        var item = Assert.Single(host.Items());
        host.Ui.Invoke(() => host.ViewModel.ActivateCommand.Execute(item));
        host.Drain();

        Assert.Empty(host.Navigated);
        Assert.Empty(host.Items());
    }

    [Fact]
    public void DismissOnlyRemovesTheUiItem()
    {
        using var host = new Host();
        host.Publish("task:1", AppNotificationCategory.TaskCompleted, "完成");
        host.Drain();

        var item = Assert.Single(host.Items());
        host.Ui.Invoke(() => host.ViewModel.DismissCommand.Execute(item));
        host.Drain();

        Assert.Empty(host.Items());
        // 任务状态与通知中心缓存不受提示条关闭影响。
        Assert.Single(host.Service.Recent);
    }

    [Fact]
    public void DisposeUnsubscribesAndStopsShowingNotifications()
    {
        using var host = new Host();
        host.Publish("task:1", AppNotificationCategory.TaskCompleted, "完成");
        host.Drain();
        Assert.Single(host.Items());

        host.Ui.Invoke(() => host.ViewModel.Dispose());
        host.Drain();

        host.Publish("task:2", AppNotificationCategory.TaskCompleted, "第二条");
        host.Drain();

        Assert.Empty(host.Items());
    }

    [Fact]
    public async Task PublishingFromABackgroundThreadIsMarshaledToTheUiThread()
    {
        using var host = new Host();

        await Task.Run(() => host.Publish("task:bg", AppNotificationCategory.TaskCompleted, "后台结果"));

        host.WaitUntil(() => host.Items().Count == 1);

        Assert.Equal("task:bg", host.Items()[0].Id);
    }

    private sealed class Host : IDisposable
    {
        private static readonly ILocalizer Localizer =
            new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

        public Host()
        {
            Ui = new UiThread();
            Time = new FakeTimeProvider(Start);
            Service = new NotificationService(
                new EnabledPreferences(),
                NullLogger<NotificationService>.Instance,
                Time);
            ViewModel = Ui.Invoke(() => new NotificationHostViewModel(
                Service,
                Localizer,
                Ui.Dispatcher,
                Time,
                NullLogger<NotificationHostViewModel>.Instance));
            ViewModel.Navigate = destination =>
            {
                Navigated.Add(destination);
                return true;
            };
        }

        public UiThread Ui { get; }

        public FakeTimeProvider Time { get; }

        public NotificationService Service { get; }

        public NotificationHostViewModel ViewModel { get; }

        public List<string> Navigated { get; } = [];

        public void Publish(
            string id,
            AppNotificationCategory category,
            string title,
            string? destination = null) =>
            Service.Publish(new AppNotification(
                id,
                category,
                AppNotificationSeverity.Info,
                title,
                "正文",
                Time.GetUtcNow(),
                NavigationDestination: destination));

        public void Drain() => Ui.Drain();

        public IReadOnlyList<NotificationItemViewModel> Items() =>
            Ui.Invoke(() => ViewModel.Items.ToArray());

        public void WaitUntilEmpty() =>
            WaitUntil(() => Items().Count == 0);

        public void WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                Drain();
                if (condition())
                {
                    return;
                }

                Thread.Sleep(10);
            }

            Assert.Fail("等待通知宿主状态超时。");
        }

        public void Dispose()
        {
            Ui.Invoke(() => ViewModel.Dispose());
            Ui.Dispose();
        }
    }

    private sealed class EnabledPreferences : INotificationPreferences
    {
        public bool IsEnabled(AppNotificationCategory category) => true;
    }
}
