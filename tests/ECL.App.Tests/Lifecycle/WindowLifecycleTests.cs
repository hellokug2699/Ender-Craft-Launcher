// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using Ecl.App.Services.Lifecycle;
using Ecl.App.Shell.Lifecycle;
using Ecl.App.Tests.Support;
using Ecl.Configuration.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Lifecycle;

/// <summary>
/// F-12 主窗口关闭/托盘状态机：Ask 的三个真实分支、记住选择、取消保留窗口、
/// 托盘不可用回退、二次关闭不绕过守卫，以及“最小化到托盘”与单飞关闭。
/// 使用真实 <see cref="Window"/> + 独立 STA Dispatcher；托盘、询问与就绪探测都是手写替身。
/// </summary>
public sealed class WindowLifecycleTests
{
    [Fact]
    public void AskCancelKeepsWindowAndDoesNotShutDown()
    {
        using var host = new Host();
        host.Prompt.NextChoice = CloseChoice.Cancel;

        host.Close();

        Assert.Equal(1, host.Prompt.AskCalls);
        Assert.Equal(0, host.Coordinator.ShutdownCalls);
        Assert.True(host.IsWindowVisible());
        Assert.Equal(MainWindowState.Visible, host.State());
        Assert.False(host.Tray.IsShown);
    }

    [Fact]
    public void AskMinimizeHidesWindowToTray()
    {
        using var host = new Host();
        host.Prompt.NextChoice = CloseChoice.MinimizeToTray;

        host.Close();

        Assert.True(host.Tray.IsShown);
        Assert.False(host.IsWindowVisible());
        Assert.Equal(MainWindowState.HiddenToTray, host.State());
        Assert.Equal(0, host.Coordinator.ShutdownCalls);
    }

    [Fact]
    public void AskExitWithoutConfirmClosesOnce()
    {
        using var host = new Host(confirmOnClose: false);
        host.Prompt.NextChoice = CloseChoice.Exit;

        host.Close();
        host.Drain();

        Assert.Equal(0, host.Prompt.ConfirmCalls);
        Assert.Equal(1, host.ExitCalls);
        Assert.Equal(1, host.Coordinator.ShutdownCalls);
        Assert.False(host.IsWindowVisible());
    }

    [Fact]
    public void AskRememberPersistsTheChosenCloseAction()
    {
        using var host = new Host(confirmOnClose: false);
        host.Prompt.NextChoice = CloseChoice.Exit;
        host.Prompt.RememberOnAsk = true;

        host.Close();
        host.Drain();

        Assert.Equal(
            LauncherKeys.CloseButtonActions.Exit,
            host.Settings.GetRawString(LauncherKeys.CloseButtonAction));
    }

    [Fact]
    public void ConfirmingExitShowsRealActiveWorkAndCancelKeepsWindow()
    {
        using var host = new Host(confirmOnClose: true);
        host.Readiness.Readiness = new CloseReadiness(
            ActiveOperations: 2,
            ActiveBackgroundWork: 3,
            HasDirtyWorkspaces: false,
            HasUnsafeToAbort: false);
        host.Prompt.NextChoice = CloseChoice.Exit;
        host.Prompt.ConfirmExitResult = false;

        host.Close();

        Assert.Equal(1, host.Prompt.ConfirmCalls);
        Assert.Equal(2, host.Prompt.LastReadiness!.ActiveOperations);
        Assert.Equal(3, host.Prompt.LastReadiness.ActiveBackgroundWork);
        Assert.True(host.IsWindowVisible());
        Assert.Equal(0, host.Coordinator.ShutdownCalls);
    }

    [Fact]
    public void SecondCloseWhilePromptingIsStillCancelled()
    {
        using var host = new Host();
        host.Prompt.BlockAsk = true;
        host.Prompt.NextChoice = CloseChoice.Cancel;

        host.Close();
        host.Close();

        // 第二次关闭必须继续取消：既不重复询问，也不能绕过询问直接关窗。
        Assert.Equal(1, host.Prompt.AskCalls);
        Assert.True(host.IsWindowVisible());
        Assert.Equal(0, host.Coordinator.ShutdownCalls);

        host.Prompt.ReleaseAsk();
        host.Drain();

        Assert.True(host.IsWindowVisible());
        Assert.Equal(0, host.Coordinator.ShutdownCalls);
    }

    [Fact]
    public void ConfiguredMinimizeFallsBackToExitWhenTrayIsUnavailable()
    {
        using var host = new Host(
            confirmOnClose: false,
            closeButtonAction: LauncherKeys.CloseButtonActions.MinimizeToTray);
        host.Tray.IsAvailable = false;

        host.Close();
        host.Drain();

        // 托盘不可用时不能把窗口藏起来：按退出处理，用户至少能离开。
        Assert.Equal(1, host.ExitCalls);
        Assert.Equal(0, host.Prompt.AskCalls);
        Assert.False(host.IsWindowVisible());
    }

    [Fact]
    public void MinimizeActionTrayHidesWindowButTaskbarKeepsItVisible()
    {
        using var host = new Host(minimizeAction: LauncherKeys.MinimizeActions.Tray);

        host.Minimize();

        Assert.True(host.Tray.IsShown);
        Assert.False(host.IsWindowVisible());
        Assert.Equal(MainWindowState.HiddenToTray, host.State());

        host.Restore();

        Assert.True(host.IsWindowVisible());
        Assert.Equal(MainWindowState.Visible, host.State());
    }

    [Fact]
    public void StartMinimizedToTrayKeepsWindowVisibleWhenTrayIsUnavailable()
    {
        using var host = new Host();
        host.Tray.IsAvailable = false;

        host.StartMinimized();

        Assert.True(host.IsWindowVisible());
        Assert.Equal(MainWindowState.Visible, host.State());
    }

    [Fact]
    public async Task ShutdownIsSingleFlight()
    {
        using var host = new Host();

        var first = host.Lifecycle.ShutdownAsync();
        var second = host.Lifecycle.ShutdownAsync();
        await Task.WhenAll(first, second);

        Assert.Same(first, second);
        Assert.Equal(1, host.Coordinator.ShutdownCalls);
    }

    [Fact]
    public void SafetyRelevantReadinessForcesConfirmationEvenWhenConfirmOnCloseIsOff()
    {
        using var host = new Host(confirmOnClose: false);
        host.Readiness.Readiness = new CloseReadiness(0, 0, HasDirtyWorkspaces: true, HasUnsafeToAbort: false);
        host.Prompt.NextChoice = CloseChoice.Exit;
        host.Prompt.ConfirmExitResult = false;

        host.Close();

        Assert.Equal(1, host.Prompt.ConfirmCalls);
        Assert.True(host.IsWindowVisible());
        Assert.Equal(0, host.Coordinator.ShutdownCalls);
    }

    /// <summary>
    /// 真实窗口 + 状态机夹具。所有 WPF 对象都在 <see cref="UiThread"/> 上创建；
    /// 退出由宿主回调在稍后关闭窗口，模拟生产里的 <c>Application.Shutdown()</c>。
    /// </summary>
    private sealed class Host : IDisposable
    {
        public Host(
            bool confirmOnClose = false,
            string closeButtonAction = LauncherKeys.CloseButtonActions.Ask,
            string minimizeAction = LauncherKeys.MinimizeActions.Taskbar)
        {
            Ui = new UiThread();
            Settings.Values[LauncherKeys.CloseButtonAction] = closeButtonAction;
            Settings.Values[LauncherKeys.MinimizeAction] = minimizeAction;
            Settings.Values[LauncherKeys.ConfirmOnClose] = confirmOnClose ? "true" : "false";

            Func<Task> exitHost = () =>
            {
                ExitCalls++;
                // 不在 Closing 处理栈里重入 Close：排到下一次 Dispatcher 迭代，等价于 Application.Shutdown。
                Ui.Dispatcher.BeginInvoke(new Action(() => Window?.Close()));
                return Task.CompletedTask;
            };

            Ui.Invoke(() =>
            {
                Window = new Window { Width = 320, Height = 200, Title = "ecl-lifecycle-test" };
                Lifecycle = new MainWindowLifecycle(
                    Window,
                    Tray,
                    Settings,
                    Prompt,
                    Readiness,
                    Coordinator,
                    NullLogger<MainWindowLifecycle>.Instance,
                    exitHost: exitHost);
                Window.Show();
            });
        }

        public UiThread Ui { get; }

        public FakeTrayHost Tray { get; } = new();

        public FakeClosePrompt Prompt { get; } = new();

        public FakeSettingsService Settings { get; } = new();

        public FakeCloseReadinessProbe Readiness { get; } = new();

        public FakeShutdownCoordinator Coordinator { get; } = new();

        public Window? Window { get; private set; }

        public MainWindowLifecycle Lifecycle { get; private set; } = null!;

        public int ExitCalls { get; private set; }

        public void Close() => Ui.Invoke(() => Window!.Close());

        public void Drain() => Ui.Drain();

        public bool IsWindowVisible() => Ui.Invoke(() => Window?.IsVisible == true);

        public MainWindowState State() => Ui.Invoke(() => Lifecycle.State);

        public void Minimize() => Ui.Invoke(() => Window!.WindowState = WindowState.Minimized);

        public void Restore() => Ui.Invoke(() => Lifecycle.RestoreFromTray());

        public void StartMinimized() => Ui.Invoke(() => Lifecycle.StartMinimizedToTray());

        public void Dispose()
        {
            try
            {
                Ui.Invoke(() =>
                {
                    Lifecycle.Dispose();
                    if (Window is not null)
                    {
                        Window.Close();
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // 窗口在关停过程中关闭：测试清理不应因此失败。
            }

            Ui.Dispose();
        }
    }
}
