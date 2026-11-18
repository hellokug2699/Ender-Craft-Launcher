// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Ecl.App.Services.Lifecycle;
using Ecl.App.Services.Notifications;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Shell.Lifecycle;

/// <summary>主窗口的可见性状态。状态迁移只由本类做，视图与页面不自行隐藏/显示窗口。</summary>
public enum MainWindowState
{
    Visible,
    MinimizedToTaskbar,
    HiddenToTray,
    Prompting,
    Exiting,
    Exited,
}

/// <summary>
/// 主窗口关闭与托盘的生命周期状态机（F-12）。
///
/// 为什么需要它：
/// - WPF 的 <c>Closing</c> 是同步事件，而关闭流程（询问、等待任务、释放容器）是异步的：
///   必须先取消本次关闭，等流程完成后再显式关闭窗口；
/// - 第二次点击关闭（询问中/退出中）必须继续取消，否则会绕过询问直接关窗；
/// - 托盘、最小化与关闭按钮的行为必须读写同一组设置键，不能各自猜测。
///
/// 安全边界：
/// - 不注册游戏进程 / LogHost 的停止步骤：退出启动器不等于结束正在运行的游戏；
/// - 关闭协调器单飞：重复请求返回同一个任务，服务容器只释放一次；
/// - “托盘不可用”只降级（按退出/留在任务栏）并明确告知，不静默丢请求。
/// </summary>
public sealed class MainWindowLifecycle : IDisposable
{
    private readonly Window _window;
    private readonly ITrayHost _tray;
    private readonly ISettingsService _settings;
    private readonly IClosePrompt _prompt;
    private readonly ICloseReadinessProbe _readiness;
    private readonly IAppShutdownCoordinator _coordinator;
    private readonly ILogger<MainWindowLifecycle> _logger;
    private readonly ILocalizer? _localizer;
    private readonly INotificationService? _notifications;
    private readonly Func<Task>? _prepareExit;
    private readonly Func<Task>? _exitHost;
    private readonly object _gate = new();

    private Task? _shutdownTask;
    private MainWindowState _state = MainWindowState.Visible;
    private bool _prompting;
    private bool _allowClose;
    private bool _suppressMinimize;
    private bool _disposed;

    public MainWindowLifecycle(
        Window window,
        ITrayHost tray,
        ISettingsService settings,
        IClosePrompt prompt,
        ICloseReadinessProbe readiness,
        IAppShutdownCoordinator coordinator,
        ILogger<MainWindowLifecycle> logger,
        ILocalizer? localizer = null,
        INotificationService? notifications = null,
        Func<Task>? prepareExit = null,
        Func<Task>? exitHost = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _localizer = localizer;
        _notifications = notifications;
        _prepareExit = prepareExit;
        _exitHost = exitHost;

        _window.Closing += OnClosing;
        _window.StateChanged += OnWindowStateChanged;
        _window.Closed += OnWindowClosed;
        _tray.RestoreRequested += OnTrayRestoreRequested;
        _tray.ExitRequested += OnTrayExitRequested;
    }

    public MainWindowState State => _state;

    public bool IsShuttingDown => _allowClose || _coordinator.IsShuttingDown;

    public event EventHandler<MainWindowState>? StateChanged;

    /// <summary>
    /// 启动时“登录启动直接最小化到托盘”的唯一入口。托盘不可用时不隐藏窗口，
    /// 否则用户启动后既看不到窗口也没有托盘图标。
    /// </summary>
    public void StartMinimizedToTray()
    {
        if (!_tray.IsAvailable)
        {
            _logger.LogInformation("已请求启动时最小化到托盘，但系统托盘不可用；保持窗口可见。");
            return;
        }

        HideToTray();
    }

    /// <summary>
    /// 隐藏到托盘。托盘不可用时不做任何隐藏，由调用方决定回退（关闭时按退出、最小化时留在任务栏）。
    /// </summary>
    public void HideToTray()
    {
        if (_disposed || _allowClose || !_tray.IsAvailable)
        {
            return;
        }

        _suppressMinimize = true;
        try
        {
            _tray.Show();
            _window.Hide();
            // 清掉最小化状态：否则从托盘恢复时窗口会以最小化状态回到任务栏，看起来像“没恢复”。
            _window.WindowState = WindowState.Normal;
        }
        finally
        {
            _suppressMinimize = false;
        }

        SetState(MainWindowState.HiddenToTray);
        _logger.LogInformation("主窗口已隐藏到系统托盘。");
    }

    /// <summary>从托盘恢复窗口：显示、还原最小化状态并激活。</summary>
    public void RestoreFromTray()
    {
        if (_disposed)
        {
            return;
        }

        _tray.Hide();
        _suppressMinimize = true;
        try
        {
            if (!_window.IsVisible)
            {
                _window.Show();
            }

            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }
        }
        finally
        {
            _suppressMinimize = false;
        }

        _window.Activate();
        SetState(MainWindowState.Visible);
        _logger.LogInformation("主窗口已从系统托盘恢复。");
    }

    /// <summary>
    /// 幂等的关闭入口：并发/重复调用返回同一个任务，服务容器只释放一次。
    /// 调用前应已获得用户批准；本方法不再次询问。
    /// </summary>
    public Task ShutdownAsync()
    {
        lock (_gate)
        {
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.Closing -= OnClosing;
        _window.StateChanged -= OnWindowStateChanged;
        _window.Closed -= OnWindowClosed;
        _tray.RestoreRequested -= OnTrayRestoreRequested;
        _tray.ExitRequested -= OnTrayExitRequested;
    }

    /// <summary>
    /// WPF 的 Closing 不能异步等待：第一次关闭一律取消并启动询问流程；
    /// 询问中或退出中的再次关闭同样取消，修掉“第二次点击绕过守卫直接关窗”的缺口。
    /// 只有已批准退出（<see cref="_allowClose"/>）才放行真实的窗口关闭。
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;

        if (_disposed || _prompting)
        {
            return;
        }

        _prompting = true;
        SetState(MainWindowState.Prompting);
        _ = HandleClosingAsync();
    }

    private async Task HandleClosingAsync()
    {
        try
        {
            var choice = await DecideCloseChoiceAsync().ConfigureAwait(true);
            if (choice == CloseChoice.Cancel)
            {
                RestoreAfterCancelledClose();
                return;
            }

            if (choice == CloseChoice.MinimizeToTray)
            {
                HideToTray();
                return;
            }

            var readiness = _readiness.Read();
            if (NeedsExitConfirmation(readiness))
            {
                var confirmed = await _prompt.ConfirmExitAsync(readiness).ConfigureAwait(true);
                if (!confirmed)
                {
                    RestoreAfterCancelledClose();
                    return;
                }
            }

            await ApproveExitAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // 询问失败时保持窗口打开比“退出但没确认”安全；用户仍可再次关闭。
            _logger.LogError(exception, "关闭询问流程失败；保持主窗口打开。");
            RestoreAfterCancelledClose();
        }
        finally
        {
            _prompting = false;
        }
    }

    /// <summary>
    /// 依据设置决定关闭按钮的语义。Ask 才会弹三选一；
    /// 配置了“最小化到托盘”但托盘不可用时按退出处理并说明（用户至少能退出）。
    /// </summary>
    private async Task<CloseChoice> DecideCloseChoiceAsync()
    {
        switch (ResolveCloseAction())
        {
            case LauncherKeys.CloseButtonActions.MinimizeToTray:
                if (_tray.IsAvailable)
                {
                    return CloseChoice.MinimizeToTray;
                }

                NotifyTrayUnavailable();
                return CloseChoice.Exit;
            case LauncherKeys.CloseButtonActions.Exit:
                return CloseChoice.Exit;
        }

        var decision = await _prompt.AskAsync(
                new ClosePromptRequest(
                    Text("close.prompt.message", "关闭 ECL 前要做什么？"),
                    _tray.IsAvailable,
                    Text("close.prompt.minimizeUnavailable", "系统托盘不可用，无法最小化到托盘。")))
            .ConfigureAwait(true);

        if (decision.Remember && decision.Choice is CloseChoice.MinimizeToTray or CloseChoice.Exit)
        {
            PersistCloseAction(decision.Choice);
        }

        if (decision.Choice == CloseChoice.MinimizeToTray && !_tray.IsAvailable)
        {
            NotifyTrayUnavailable();
            return CloseChoice.Exit;
        }

        return decision.Choice;
    }

    /// <summary>
    /// 关闭前是否需要确认：显式的 <c>confirmOnClose</c>，或存在必须让用户知道的状态
    /// （未保存工作区 / 不可安全中止）。后两者不能因为偏好关闭而被跳过。
    /// </summary>
    private bool NeedsExitConfirmation(CloseReadiness readiness) =>
        _settings.Get<bool>(LauncherKeys.ConfirmOnClose) ||
        readiness.HasUnsafeToAbort ||
        readiness.HasDirtyWorkspaces;

    private async Task ApproveExitAsync()
    {
        if (_allowClose)
        {
            return;
        }

        _allowClose = true;
        SetState(MainWindowState.Exiting);
        await ShutdownAsync().ConfigureAwait(true);
    }

    private async Task ShutdownCoreAsync()
    {
        SetState(MainWindowState.Exiting);
        try
        {
            // 容器释放前保存最后一次会话快照；释放后再写会读到已 Dispose 的跟踪器。
            if (_prepareExit is not null)
            {
                await _prepareExit().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "退出前保存会话快照失败。");
        }

        try
        {
            await _coordinator.ShutdownAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // 关闭流程异常不能让用户卡在“永远退不出去”的窗口上；仍继续执行宿主退出。
            _logger.LogError(exception, "执行应用关闭协调失败。");
        }

        try
        {
            if (_exitHost is not null)
            {
                await _exitHost().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "执行宿主退出失败。");
        }

        SetState(MainWindowState.Exited);
    }

    /// <summary>取消关闭后把窗口带回可用状态：保留窗口、恢复可见并重新激活。</summary>
    private void RestoreAfterCancelledClose()
    {
        _allowClose = false;
        _suppressMinimize = true;
        try
        {
            if (!_window.IsVisible)
            {
                _window.Show();
            }

            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }
        }
        finally
        {
            _suppressMinimize = false;
        }

        _window.Activate();
        SetState(MainWindowState.Visible);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState != WindowState.Minimized || _suppressMinimize || _disposed)
        {
            return;
        }

        if (ResolveMinimizeAction() != LauncherKeys.MinimizeActions.Tray || !_tray.IsAvailable)
        {
            // 托盘不可用时“最小化到托盘”退化为普通任务栏最小化，而不是把窗口藏起来。
            SetState(MainWindowState.MinimizedToTaskbar);
            return;
        }

        HideToTray();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _allowClose = true;
        SetState(MainWindowState.Exited);
    }

    private void OnTrayRestoreRequested(object? sender, EventArgs e) =>
        Dispatch(RestoreFromTray);

    private void OnTrayExitRequested(object? sender, EventArgs e) =>
        Dispatch(() => _ = ExitFromTrayAsync());

    /// <summary>
    /// 托盘“退出”也走同一套确认：用户可能还有下载在跑。
    /// 需要确认时必须先把窗口带回前台，否则弹窗宿主在隐藏窗口里不可见。
    /// </summary>
    private async Task ExitFromTrayAsync()
    {
        if (_disposed || _allowClose || _prompting)
        {
            return;
        }

        _prompting = true;
        try
        {
            var readiness = _readiness.Read();
            if (NeedsExitConfirmation(readiness))
            {
                RestoreFromTray();
                if (!await _prompt.ConfirmExitAsync(readiness).ConfigureAwait(true))
                {
                    return;
                }
            }

            await ApproveExitAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "托盘退出流程失败；保持窗口打开。");
        }
        finally
        {
            _prompting = false;
        }
    }

    private void PersistCloseAction(CloseChoice choice)
    {
        var value = choice == CloseChoice.MinimizeToTray
            ? LauncherKeys.CloseButtonActions.MinimizeToTray
            : LauncherKeys.CloseButtonActions.Exit;
        if (!_settings.TrySetRaw(LauncherKeys.CloseButtonAction, value, null, out var error))
        {
            _logger.LogWarning("保存关闭按钮行为失败：{Error}", error);
        }
    }

    private string ResolveCloseAction()
    {
        var raw = _settings.Get<string>(LauncherKeys.CloseButtonAction);
        return raw switch
        {
            LauncherKeys.CloseButtonActions.MinimizeToTray => LauncherKeys.CloseButtonActions.MinimizeToTray,
            LauncherKeys.CloseButtonActions.Exit => LauncherKeys.CloseButtonActions.Exit,
            // 未知值（手改配置）按“询问”处理：宁可多问一次，也不猜测用户意图。
            _ => LauncherKeys.CloseButtonActions.Ask,
        };
    }

    private string ResolveMinimizeAction() => _settings.Get<string>(LauncherKeys.MinimizeAction) switch
    {
        LauncherKeys.MinimizeActions.Tray => LauncherKeys.MinimizeActions.Tray,
        _ => LauncherKeys.MinimizeActions.Taskbar,
    };

    private void NotifyTrayUnavailable()
    {
        _logger.LogWarning("系统托盘不可用；“最小化到托盘”按退出处理。");
        try
        {
            _notifications?.Publish(new AppNotification(
                "tray:unavailable:close",
                AppNotificationCategory.Hint,
                AppNotificationSeverity.Warning,
                Text("close.trayUnavailable.title", "系统托盘不可用"),
                Text("close.trayUnavailable.message", "“最小化到托盘”当前不可用，已按退出处理。"),
                DateTimeOffset.UtcNow,
                NavigationDestination: NotificationDestinations.Settings));
        }
        catch (Exception exception)
        {
            // 通知只是补充说明：失败不能改变已经确定的退出路径。
            _logger.LogDebug(exception, "发布托盘不可用提示失败。");
        }
    }

    /// <summary>托盘事件可能由 WinForms 消息循环触发；所有窗口操作统一封送回 UI Dispatcher。</summary>
    private void Dispatch(Action action)
    {
        var dispatcher = _window.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 已关停：退出流程已由窗口关闭路径处理，这里不再排队。
        }
    }

    private void SetState(MainWindowState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        _logger.LogDebug("主窗口状态：{State}", state);
        StateChanged?.Invoke(this, state);
    }

    private string Text(string key, string fallback)
    {
        var localized = _localizer?.Get(key);
        return string.IsNullOrWhiteSpace(localized) || string.Equals(localized, key, StringComparison.Ordinal)
            ? fallback
            : localized;
    }
}
