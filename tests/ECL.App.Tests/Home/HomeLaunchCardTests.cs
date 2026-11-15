// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Tests.Support;
using Ecl.App.ViewModels;
using Ecl.App.ViewModels.Home;
using Ecl.Minecraft.Launch;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecl.App.Tests.Home;

/// <summary>
/// 主页启动卡（home.md §3）：启动状态按实例持久化、失败时的错误日志入口生命周期、
/// 普通/高级两种模式的实时呈现，以及“只有真实结果才结束步骤”的边界。
/// </summary>
public sealed class HomeLaunchCardTests
{
    [Fact]
    public void BindWithoutRecordShowsNeverLaunchedAndPendingSteps()
    {
        using var ui = new UiThread();
        using var buffer = new TestAppLogBuffer();
        var paths = HomeTestData.CreatePaths();
        var store = new FakeLaunchStatusStore();
        var card = CreateCard(ui, buffer, store, out _, "validate", "files");

        var instance = HomeTestData.Instance("a", paths);
        ui.Invoke(() => card.Bind(instance));

        Assert.Equal(HomeTestData.Localizer.Get("home.launch.statusNever"), card.LaunchStatusText);
        Assert.Equal(["validate", "files"], card.Steps.Select(step => step.StepId));
        Assert.All(card.Steps, step => Assert.Equal(HomeLaunchStepState.Pending, step.State));
        Assert.False(card.ShowErrorLogEntry);
        Assert.False(card.ShowNoSessionNotice);
        Assert.Equal("normal", card.TotalProgressKind);
        Assert.Equal(0, card.TotalProgressValue);
        Assert.True(card.CanLaunch);
    }

    [Fact]
    public void FailedRecordWithSessionOffersErrorLogEntry()
    {
        using var ui = new UiThread();
        using var buffer = new TestAppLogBuffer();
        var paths = HomeTestData.CreatePaths();
        var store = new FakeLaunchStatusStore();
        store.Seed(HomeTestData.Snapshot("a", LaunchAttemptState.Failed, sessionId: "session-1"));
        var card = CreateCard(ui, buffer, store, out _, "validate", "files", "start");

        ui.Invoke(() => card.Bind(HomeTestData.Instance("a", paths)));

        Assert.True(card.ShowErrorLogEntry);
        Assert.False(card.ShowNoSessionNotice);
        Assert.Equal("failed", card.TotalProgressKind);
        Assert.InRange(card.TotalProgressValue, 1, 99);
        Assert.Equal("缺少游戏文件", card.LaunchStatusDetail);
        Assert.Equal(HomeLaunchStepState.Failed, card.Steps.Single(step => step.StepId == "files").State);
        // 本次启动在失败步骤之后已经结束：后续步骤必须显示“未执行”，不能伪装为完成。
        Assert.Equal(HomeLaunchStepState.NotRun, card.Steps.Single(step => step.StepId == "start").State);
    }

    [Fact]
    public void FailedRecordWithoutSessionExplainsInsteadOfShowingDeadButton()
    {
        using var ui = new UiThread();
        using var buffer = new TestAppLogBuffer();
        var paths = HomeTestData.CreatePaths();
        var store = new FakeLaunchStatusStore();
        store.Seed(HomeTestData.Snapshot("a", LaunchAttemptState.Failed, sessionId: null));
        var card = CreateCard(ui, buffer, store, out _, "validate", "files");

        ui.Invoke(() => card.Bind(HomeTestData.Instance("a", paths)));

        Assert.False(card.ShowErrorLogEntry);
        Assert.True(card.ShowNoSessionNotice);
    }

    [Fact]
    public void SucceededRecordShowsGreenCompletedProgress()
    {
        using var ui = new UiThread();
        using var buffer = new TestAppLogBuffer();
        var paths = HomeTestData.CreatePaths();
        var store = new FakeLaunchStatusStore();
        store.Seed(HomeTestData.Snapshot("a", LaunchAttemptState.Succeeded, sessionId: "session-1"));
        var card = CreateCard(ui, buffer, store, out _, "validate", "files");

        ui.Invoke(() => card.Bind(HomeTestData.Instance("a", paths)));

        Assert.Equal("success", card.TotalProgressKind);
        Assert.Equal(100, card.TotalProgressValue);
        Assert.False(card.ShowErrorLogEntry);
        Assert.Equal(HomeTestData.Localizer.Get("home.launch.statusSucceeded"), card.LaunchStatusText);
    }

    [Fact]
    public void InterruptedRecordDoesNotClaimAnOutcome()
    {
        using var ui = new UiThread();
        using var buffer = new TestAppLogBuffer();
        var paths = HomeTestData.CreatePaths();
        var store = new FakeLaunchStatusStore();
        store.Seed(HomeTestData.Snapshot("a", LaunchAttemptState.Running));
        var card = CreateCard(ui, buffer, store, out _, "validate", "files");

        ui.Invoke(() => card.Bind(HomeTestData.Instance("a", paths)));

        Assert.Equal(HomeTestData.Localizer.Get("home.launch.statusInterrupted"), card.LaunchStatusText);
        Assert.All(card.Steps, step => Assert.Equal(HomeLaunchStepState.Pending, step.State));
        Assert.Equal("normal", card.TotalProgressKind);
        Assert.False(card.ShowErrorLogEntry);
        Assert.False(card.ShowNoSessionNotice);
    }

    [Fact]
    public async Task LaunchStreamsProgressAndAppliesThePersistedResult()
    {
        using var ui = new UiThread();
        using var buffer = new TestAppLogBuffer();
        var paths = HomeTestData.CreatePaths();
        var store = new FakeLaunchStatusStore();
        var card = CreateCard(ui, buffer, store, out var host, "validate", "files", "start");
        var instance = HomeTestData.Instance("a", paths);
        var stillLaunchingDuringHost = false;
        var totalDuringLaunch = 0.0;

        host.OnLaunch = (item, _, dryRun, progress) =>
        {
            Assert.False(dryRun);
            Assert.True(card.IsLaunching);
            progress!.Report(new LaunchStepProgress
            {
                StepId = "validate",
                Phase = LaunchStepProgressPhase.Started,
                Index = 0,
                Total = 2,
            });
            progress.Report(new LaunchStepProgress
            {
                StepId = "validate",
                Phase = LaunchStepProgressPhase.Completed,
                Index = 0,
                Total = 2,
                Status = LaunchStepStatus.Success,
                Message = "ok",
                Duration = TimeSpan.FromMilliseconds(120),
            });
            progress.Report(new LaunchStepProgress
            {
                StepId = "files",
                Phase = LaunchStepProgressPhase.Started,
                Index = 1,
                Total = 2,
            });
            totalDuringLaunch = card.TotalProgressValue;
            progress.Report(new LaunchStepProgress
            {
                StepId = "files",
                Phase = LaunchStepProgressPhase.Completed,
                Index = 1,
                Total = 2,
                Status = LaunchStepStatus.Failed,
                Message = "缺少游戏文件",
                Duration = TimeSpan.FromMilliseconds(80),
            });
            store.Seed(HomeTestData.Snapshot(item.Model.Id, LaunchAttemptState.Failed, "session-9"));
            stillLaunchingDuringHost = card.IsLaunching;
            return Task.CompletedTask;
        };

        ui.Invoke(() => card.Bind(instance));
        ui.Invoke(() => card.LaunchCommand.ExecuteAsync(null).GetAwaiter().GetResult());

        Assert.True(stillLaunchingDuringHost);
        Assert.False(card.IsLaunching);
        Assert.Equal(HomeLaunchStepState.Failed, card.Steps.Single(step => step.StepId == "files").State);
        Assert.Equal("failed", card.TotalProgressKind);
        Assert.True(card.ShowErrorLogEntry);
        Assert.InRange(totalDuringLaunch, 1, 99);
        Assert.InRange(card.TotalProgressValue, 1, 99);
        Assert.NotEmpty(card.Steps.Single(step => step.StepId == "files").Message);
        Assert.Equal(HomeLaunchStepState.NotRun, card.Steps.Single(step => step.StepId == "start").State);
    }

    [Fact]
    public async Task StartingAnotherLaunchHidesThePreviousErrorEntryImmediately()
    {
        using var ui = new UiThread();
        using var buffer = new TestAppLogBuffer();
        var paths = HomeTestData.CreatePaths();
        var store = new FakeLaunchStatusStore();
        store.Seed(HomeTestData.Snapshot("a", LaunchAttemptState.Failed, "session-old"));
        var card = CreateCard(ui, buffer, store, out var host, "validate");
        var visibleDuringLaunch = true;

        host.OnLaunch = (item, _, _, _) =>
        {
            visibleDuringLaunch = card.ShowErrorLogEntry;
            store.Seed(HomeTestData.Snapshot(item.Model.Id, LaunchAttemptState.Succeeded, "session-new"));
            return Task.CompletedTask;
        };

        ui.Invoke(() => card.Bind(HomeTestData.Instance("a", paths)));
        Assert.True(card.ShowErrorLogEntry);

        ui.Invoke(() => card.LaunchCommand.ExecuteAsync(null).GetAwaiter().GetResult());

        Assert.False(visibleDuringLaunch);
        Assert.False(card.ShowErrorLogEntry);
    }

    [Fact]
    public async Task AdvancedModeCollectsLiveOutputAndKeepsErrorLevels()
    {
        using var ui = new UiThread();
        using var buffer = new TestAppLogBuffer();
        var paths = HomeTestData.CreatePaths();
        var store = new FakeLaunchStatusStore();
        var card = CreateCard(ui, buffer, store, out var host, "validate");
        host.OnLaunch = (item, _, _, _) =>
        {
            buffer.Write(LogLevel.Information, "启动步骤开始");
            buffer.Write(LogLevel.Error, "启动失败原因");
            store.Seed(HomeTestData.Snapshot(item.Model.Id, LaunchAttemptState.Failed, "session-1"));
            return Task.CompletedTask;
        };

        ui.Invoke(() => card.Bind(HomeTestData.Instance("a", paths)));
        ui.Invoke(() => card.LaunchCommand.ExecuteAsync(null).GetAwaiter().GetResult());

        Assert.True(card.HasLogLines);
        Assert.Contains(card.LogLines, line => line.Message == "启动步骤开始" && line.Level == LogLevel.Information);
        Assert.Contains(card.LogLines, line => line.Message == "启动失败原因" && line.Level == LogLevel.Error);
    }

    [Fact]
    public async Task PreviewUsesTheHostOperationStateAndDoesNotStartALaunch()
    {
        using var ui = new UiThread();
        using var buffer = new TestAppLogBuffer();
        var paths = HomeTestData.CreatePaths();
        var store = new FakeLaunchStatusStore();
        var card = CreateCard(ui, buffer, store, out var host, "validate");
        InstanceOperationState? state = null;
        host.OnLaunch = (_, operation, dryRun, _) =>
        {
            Assert.True(dryRun);
            state = operation;
            operation.LaunchCommandPreview = "java -Xmx2048M -jar client.jar";
            return Task.CompletedTask;
        };

        ui.Invoke(() => card.Bind(HomeTestData.Instance("a", paths)));
        ui.Invoke(() => card.PreviewCommand.ExecuteAsync(null).GetAwaiter().GetResult());

        Assert.NotNull(state);
        Assert.True(card.ShowPreview);
        Assert.Equal("java -Xmx2048M -jar client.jar", card.PreviewText);
        Assert.False(card.IsLaunching);
        Assert.Null(store.Get("a"));
    }

    [Fact]
    public async Task PreviewFailureExplainsInsteadOfSilentlyShowingNothing()
    {
        using var ui = new UiThread();
        using var buffer = new TestAppLogBuffer();
        var paths = HomeTestData.CreatePaths();
        var store = new FakeLaunchStatusStore();
        var card = CreateCard(ui, buffer, store, out var host, "validate");
        host.OnLaunch = (_, operation, _, _) =>
        {
            operation.LaunchStatus = "未找到版本 1.21.1 的版本文件。";
            return Task.CompletedTask;
        };

        ui.Invoke(() => card.Bind(HomeTestData.Instance("a", paths)));
        ui.Invoke(() => card.PreviewCommand.ExecuteAsync(null).GetAwaiter().GetResult());

        Assert.False(card.ShowPreview);
        Assert.Contains("未找到版本", card.PreviewNote, StringComparison.Ordinal);
    }

    private static HomeLaunchCardViewModel CreateCard(
        UiThread ui,
        TestAppLogBuffer buffer,
        FakeLaunchStatusStore store,
        out FakeLaunchHost host,
        params string[] steps)
    {
        var created = new FakeLaunchHost();
        host = created;
        return ui.Invoke(() => new HomeLaunchCardViewModel(
            created,
            new FakeLaunchService(steps),
            store,
            buffer.Buffer,
            HomeTestData.Localizer,
            ui.Dispatcher));
    }
}





