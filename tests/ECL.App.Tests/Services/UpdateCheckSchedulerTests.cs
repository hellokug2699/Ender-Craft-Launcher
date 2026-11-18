// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Services;
using Ecl.App.Services.Notifications;
using Ecl.App.Services.Updates;
using Ecl.App.Tests.Support;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Services;

/// <summary>
/// F-12 更新检查调度：周期到期、通道变化、单飞、禁用不触网、失败可见与状态键写入。
/// 所有网络调用都由假 <see cref="IUpdateCheckService"/> 记录，测试不会发出真实请求。
/// </summary>
public sealed class UpdateCheckSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly ILocalizer Localizer =
        new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public async Task DisabledSettingReturnsDisabledWithoutAnyNetworkRequest()
    {
        var fixture = new Fixture();
        fixture.Settings.Values[LauncherKeys.CheckUpdates] = "false";

        var report = await fixture.Scheduler.CheckNowAsync(userInitiated: true);

        Assert.NotNull(report);
        Assert.Equal(UpdateCheckOutcome.Disabled, report.Outcome);
        Assert.Equal(0, fixture.Updates.Calls);
        // 关闭时也不写状态键：否则重新开启后会用“上次检查时间”掩盖真实情况。
        Assert.Equal(string.Empty, fixture.Settings.GetRawString(LauncherKeys.UpdateLastCheckedAt));
        Assert.Empty(fixture.Notifications.Recent);
    }

    [Fact]
    public async Task StartupCadenceIsDueOnEveryBackgroundEvaluation()
    {
        var fixture = new Fixture();

        await fixture.Scheduler.CheckNowAsync(userInitiated: false);
        await fixture.Scheduler.CheckNowAsync(userInitiated: false);

        Assert.Equal(2, fixture.Updates.Calls);
    }

    [Fact]
    public async Task DailyCadenceWaitsAFullDayBeforeRechecking()
    {
        var fixture = new Fixture();
        fixture.Settings.Values[LauncherKeys.UpdateCheckInterval] = LauncherKeys.UpdateCheckIntervals.Daily;

        await fixture.Scheduler.CheckNowAsync(userInitiated: false);
        Assert.Equal(1, fixture.Updates.Calls);
        Assert.False(string.IsNullOrWhiteSpace(fixture.Settings.GetRawString(LauncherKeys.UpdateLastCheckedAt)));
        Assert.Equal("main", fixture.Settings.GetRawString(LauncherKeys.UpdateLastCheckedChannel));

        await fixture.Scheduler.CheckNowAsync(userInitiated: false);
        Assert.Equal(1, fixture.Updates.Calls);

        fixture.Time.Advance(TimeSpan.FromHours(23));
        await fixture.Scheduler.CheckNowAsync(userInitiated: false);
        Assert.Equal(1, fixture.Updates.Calls);

        fixture.Time.Advance(TimeSpan.FromHours(2));
        await fixture.Scheduler.CheckNowAsync(userInitiated: false);
        Assert.Equal(2, fixture.Updates.Calls);
    }

    [Fact]
    public async Task WeeklyCadenceWaitsASevenDayWindow()
    {
        var fixture = new Fixture();
        fixture.Settings.Values[LauncherKeys.UpdateCheckInterval] = LauncherKeys.UpdateCheckIntervals.Weekly;

        await fixture.Scheduler.CheckNowAsync(userInitiated: false);
        fixture.Time.Advance(TimeSpan.FromDays(6));
        await fixture.Scheduler.CheckNowAsync(userInitiated: false);
        Assert.Equal(1, fixture.Updates.Calls);

        fixture.Time.Advance(TimeSpan.FromDays(2));
        await fixture.Scheduler.CheckNowAsync(userInitiated: false);
        Assert.Equal(2, fixture.Updates.Calls);
    }

    [Fact]
    public async Task ChannelChangeForcesAnImmediateRecheck()
    {
        var fixture = new Fixture();
        fixture.Settings.Values[LauncherKeys.UpdateCheckInterval] = LauncherKeys.UpdateCheckIntervals.Weekly;
        await fixture.Scheduler.CheckNowAsync(userInitiated: false);

        fixture.Settings.Values[LauncherKeys.UpdateChannel] = "dev";
        await fixture.Scheduler.CheckNowAsync(userInitiated: false);

        Assert.Equal(2, fixture.Updates.Calls);
        Assert.Equal("dev", fixture.Settings.GetRawString(LauncherKeys.UpdateLastCheckedChannel));
    }

    [Fact]
    public async Task UserInitiatedCheckIgnoresTheCadence()
    {
        var fixture = new Fixture();
        fixture.Settings.Values[LauncherKeys.UpdateCheckInterval] = LauncherKeys.UpdateCheckIntervals.Weekly;

        await fixture.Scheduler.CheckNowAsync(userInitiated: false);
        await fixture.Scheduler.CheckNowAsync(userInitiated: true);

        Assert.Equal(2, fixture.Updates.Calls);
    }

    [Fact]
    public async Task ConcurrentChecksShareOneInFlightRequest()
    {
        var fixture = new Fixture();
        fixture.Updates.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = fixture.Scheduler.CheckNowAsync(userInitiated: false);
        var second = fixture.Scheduler.CheckNowAsync(userInitiated: false);
        fixture.Updates.Gate.SetResult();

        var results = await Task.WhenAll(first, second);

        // 单飞：并发的第二次调用复用同一个网络请求，不会产生第二组通知。
        Assert.Equal(1, fixture.Updates.Calls);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task SuccessfulCheckWritesTheTwoHiddenStateKeys()
    {
        var fixture = new Fixture();
        fixture.Updates.Report = ReportOf(UpdateCheckOutcome.UpdateAvailable, available: "0.9.2");
        var reported = 0;
        fixture.Scheduler.ReportAvailable += (_, _) => reported++;

        var report = await fixture.Scheduler.CheckNowAsync(userInitiated: false);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, report!.Outcome);
        Assert.Same(report, fixture.Scheduler.LastReport);
        Assert.Equal(1, reported);
        var lastCheckedAt = fixture.Settings.GetRawString(LauncherKeys.UpdateLastCheckedAt);
        Assert.True(DateTimeOffset.TryParse(
            lastCheckedAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed));
        Assert.Equal(Start, parsed);
        Assert.Equal("main", fixture.Settings.GetRawString(LauncherKeys.UpdateLastCheckedChannel));
    }

    [Fact]
    public async Task AvailableUpdateIsAnnouncedAsBackgroundTask()
    {
        var fixture = new Fixture();
        fixture.Updates.Report = ReportOf(UpdateCheckOutcome.UpdateAvailable, available: "0.9.2");

        await fixture.Scheduler.CheckNowAsync(userInitiated: false);

        var notification = Assert.Single(fixture.Notifications.Recent);
        Assert.Equal(AppNotificationCategory.BackgroundTask, notification.Category);
        Assert.Equal(NotificationDestinations.About, notification.NavigationDestination);
        Assert.Contains("0.9.2", notification.Message);
    }

    [Fact]
    public async Task FailedOutcomeIsVisibleAsTaskFailureButNotRecordedAsChecked()
    {
        var fixture = new Fixture();
        fixture.Updates.Report = ReportOf(
            UpdateCheckOutcome.Failed,
            errorCode: "update.check_failed",
            message: "连接被重置");

        var report = await fixture.Scheduler.CheckNowAsync(userInitiated: false);

        Assert.Equal(UpdateCheckOutcome.Failed, report!.Outcome);
        var notification = Assert.Single(fixture.Notifications.Recent);
        Assert.Equal(AppNotificationCategory.TaskFailed, notification.Category);
        Assert.Contains("连接被重置", notification.Message);
        // 失败不写 lastCheckedAt：否则界面会显示“刚刚检查过”。
        Assert.Equal(string.Empty, fixture.Settings.GetRawString(LauncherKeys.UpdateLastCheckedAt));
    }

    [Fact]
    public async Task UnavailableOutcomeOnlyUpdatesLastReportWithoutNoise()
    {
        var fixture = new Fixture();
        fixture.Updates.Report = ReportOf(
            UpdateCheckOutcome.Unavailable,
            errorCode: "update.feed_not_configured",
            message: "未配置更新源。");

        var report = await fixture.Scheduler.CheckNowAsync(userInitiated: false);

        Assert.Equal(UpdateCheckOutcome.Unavailable, report!.Outcome);
        Assert.Same(report, fixture.Scheduler.LastReport);
        Assert.Empty(fixture.Notifications.Recent);
        Assert.Equal(string.Empty, fixture.Settings.GetRawString(LauncherKeys.UpdateLastCheckedChannel));
    }

    [Fact]
    public async Task UnexpectedExceptionIsLoggedAndAnnouncedInsteadOfKillingTheLoop()
    {
        var fixture = new Fixture();
        fixture.Updates.Failure = new InvalidOperationException("boom");

        var report = await fixture.Scheduler.CheckNowAsync(userInitiated: false);

        Assert.Null(report);
        var notification = Assert.Single(fixture.Notifications.Recent);
        Assert.Equal(AppNotificationCategory.TaskFailed, notification.Category);
    }

    [Fact]
    public async Task StartAsyncWithCancelledTokenPerformsNoRequest()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await fixture.Scheduler.StartAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, fixture.Updates.Calls);
    }

    [Fact]
    public async Task StartAsyncRunsTheStartupCheckIsIdempotentAndStopsOnCancellation()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();

        var loop = fixture.Scheduler.StartAsync(cancellation.Token);
        await WaitUntilAsync(() => fixture.Updates.Calls == 1);

        var second = fixture.Scheduler.StartAsync(cancellation.Token);
        Assert.Same(loop, second);

        cancellation.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, fixture.Updates.Calls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("等待条件超时。");
    }

    private static UpdateCheckReport ReportOf(
        UpdateCheckOutcome outcome,
        string channel = "main",
        string? available = null,
        string? errorCode = null,
        string? message = null) =>
        new(outcome, channel, "0.9.1", available, null, null, errorCode, message);

    private sealed class Fixture
    {
        public Fixture()
        {
            Time = new FakeTimeProvider(Start);
            Notifications = new NotificationService(
                new AlwaysEnabledPreferences(),
                NullLogger<NotificationService>.Instance,
                Time);
            Scheduler = new UpdateCheckScheduler(
                Settings,
                Updates,
                Notifications,
                Time,
                NullLogger<UpdateCheckScheduler>.Instance,
                Localizer);
        }

        public FakeSettingsService Settings { get; } = new();

        public FakeUpdateCheckService Updates { get; } = new();

        public FakeTimeProvider Time { get; }

        public NotificationService Notifications { get; }

        public UpdateCheckScheduler Scheduler { get; }
    }

    private sealed class AlwaysEnabledPreferences : INotificationPreferences
    {
        public bool IsEnabled(AppNotificationCategory category) => true;
    }

    private sealed class FakeUpdateCheckService : IUpdateCheckService
    {
        public int Calls { get; private set; }

        public UpdateCheckReport Report { get; set; } = ReportOf(UpdateCheckOutcome.UpToDate);

        public Exception? Failure { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public async Task<UpdateCheckReport> CheckAsync(CancellationToken cancellationToken = default)
        {
            // 与真实服务一致：已取消的令牌不会产生请求。
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            return Report;
        }
    }
}
