// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services.Notifications;
using Ecl.App.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Notifications;

/// <summary>
/// F-12 通知中心：类别偏好、强制通知、脱敏、去重、有界与并发。
/// 这些行为直接决定“用户看到什么”，因此断言文本而不是只断言数量。
/// </summary>
public sealed class NotificationServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DisabledCategoryIsDroppedWithoutRaisingEvent()
    {
        var preferences = new StubPreferences { Enabled = false };
        var service = Create(preferences, out _);
        var raised = 0;
        service.Published += (_, _) => raised++;

        service.Publish(Notification("task:1", AppNotificationCategory.TaskCompleted, "完成", "下载完成"));

        Assert.Empty(service.Recent);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void MandatoryNotificationBypassesCategoryPreference()
    {
        var preferences = new StubPreferences { Enabled = false };
        var service = Create(preferences, out _);

        service.Publish(Notification(
            "data:1",
            AppNotificationCategory.TaskFailed,
            "删除失败",
            "托管数据未能删除",
            mandatory: true));

        var notification = Assert.Single(service.Recent);
        Assert.True(notification.IsMandatory);
        Assert.Equal("删除失败", notification.Title);
    }

    [Fact]
    public void CredentialsAndLocalPathsAreRedactedBeforePublishing()
    {
        var preferences = new StubPreferences { Enabled = true };
        var service = Create(preferences, out _);

        service.Publish(Notification(
            "redact:1",
            AppNotificationCategory.TaskFailed,
            "失败 token=abcdef123456",
            "Bearer eyJhbGciOiJIUzI1NiJ9 写入 C:\\Users\\alice\\AppData\\Roaming\\ECL\\config 失败"));

        var notification = Assert.Single(service.Recent);
        Assert.Contains("token=***", notification.Title);
        Assert.Contains("Bearer ***", notification.Message);
        Assert.Contains(LocalPathPlaceholder, notification.Message);
        Assert.DoesNotContain("abcdef123456", notification.Title);
        Assert.DoesNotContain("alice", notification.Message);
        Assert.DoesNotContain("C:\\Users", notification.Message);
    }

    [Fact]
    public void DuplicateIdWithinWindowIsPublishedOnce()
    {
        var preferences = new StubPreferences { Enabled = true };
        var service = Create(preferences, out var time);
        var raised = 0;
        service.Published += (_, _) => raised++;

        service.Publish(Notification("duplicate", AppNotificationCategory.TaskCompleted, "完成", "第一次"));
        time.Advance(TimeSpan.FromMinutes(4));
        service.Publish(Notification("duplicate", AppNotificationCategory.TaskCompleted, "完成", "第二次"));

        Assert.Single(service.Recent);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SameIdIsAcceptedAgainAfterTheWindow()
    {
        var preferences = new StubPreferences { Enabled = true };
        var service = Create(preferences, out var time);

        service.Publish(Notification("retry", AppNotificationCategory.TaskCompleted, "完成", "第一次"));
        time.Advance(TimeSpan.FromMinutes(6));
        service.Publish(Notification("retry", AppNotificationCategory.TaskCompleted, "完成", "第二次"));

        Assert.Equal(2, service.Recent.Count);
        Assert.Equal("第二次", service.Recent[0].Message);
    }

    [Fact]
    public void RecentIsBoundedAndNewestFirst()
    {
        var preferences = new StubPreferences { Enabled = true };
        var service = Create(preferences, out _);

        for (var index = 0; index < NotificationService.MaxRecent + 10; index++)
        {
            service.Publish(Notification($"task:{index}", AppNotificationCategory.TaskCompleted, "完成", $"第 {index} 项"));
        }

        Assert.Equal(NotificationService.MaxRecent, service.Recent.Count);
        Assert.Equal($"第 {NotificationService.MaxRecent + 9} 项", service.Recent[0].Message);
    }

    [Fact]
    public void LongTextIsTruncatedToTheBound()
    {
        var preferences = new StubPreferences { Enabled = true };
        var service = Create(preferences, out _);

        service.Publish(Notification("long", AppNotificationCategory.TaskCompleted, "完成", new string('a', 900)));

        var notification = Assert.Single(service.Recent);
        Assert.Equal(NotificationService.MaxTextLength, notification.Message.Length);
        Assert.EndsWith("…", notification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentPublishesStayBoundedAndDoNotThrow()
    {
        var preferences = new StubPreferences { Enabled = true };
        var service = Create(preferences, out _);

        Parallel.For(0, 400, index =>
            service.Publish(Notification($"concurrent:{index}", AppNotificationCategory.BackgroundTask, "提示", "内容")));

        Assert.InRange(service.Recent.Count, 1, NotificationService.MaxRecent);
    }

    [Fact]
    public void MissingIdentityIsDroppedInsteadOfBreakingCallers()
    {
        var preferences = new StubPreferences { Enabled = true };
        var service = Create(preferences, out _);

        service.Publish(Notification(string.Empty, AppNotificationCategory.TaskCompleted, "完成", "内容"));
        service.Publish(Notification("id", AppNotificationCategory.TaskCompleted, string.Empty, "内容"));

        Assert.Empty(service.Recent);
    }

    [Fact]
    public void NotificationPreferencesMapEachCategoryToItsOwnKey()
    {
        var settings = new FakeSettingsService();
        var preferences = new NotificationPreferences(settings);

        Assert.True(preferences.IsEnabled(AppNotificationCategory.TaskCompleted));
        settings.Values["launcher.notifyTaskCompleted"] = "false";
        Assert.False(preferences.IsEnabled(AppNotificationCategory.TaskCompleted));
        Assert.True(preferences.IsEnabled(AppNotificationCategory.TaskFailed));

        settings.Values["launcher.notifyTaskFailed"] = "false";
        Assert.False(preferences.IsEnabled(AppNotificationCategory.TaskFailed));
        Assert.True(preferences.IsEnabled(AppNotificationCategory.BackgroundTask));

        settings.Values["launcher.showOperationHints"] = "false";
        Assert.False(preferences.IsEnabled(AppNotificationCategory.Hint));
    }

    private const string LocalPathPlaceholder = "<local-path>";

    private static NotificationService Create(StubPreferences preferences, out FakeTimeProvider timeProvider)
    {
        timeProvider = new FakeTimeProvider(Start);
        return new NotificationService(preferences, NullLogger<NotificationService>.Instance, timeProvider);
    }

    private static AppNotification Notification(
        string id,
        AppNotificationCategory category,
        string title,
        string message,
        bool mandatory = false) =>
        new(id, category, AppNotificationSeverity.Info, title, message, Start, IsMandatory: mandatory);

    private sealed class StubPreferences : INotificationPreferences
    {
        public bool Enabled { get; set; } = true;

        public bool IsEnabled(AppNotificationCategory category) => Enabled;
    }
}
