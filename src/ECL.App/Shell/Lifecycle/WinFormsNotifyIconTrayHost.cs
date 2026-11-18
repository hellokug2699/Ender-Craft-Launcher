// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Ecl.App.Shell.Lifecycle;

/// <summary>
/// 基于 <see cref="Forms.NotifyIcon"/> 的托盘宿主。
///
/// 为什么要在这里隔离 WinForms：
/// - WPF 没有系统托盘图标 API，而引入 WinForms 只需要一个 <c>NotifyIcon</c>；
/// - WinForms 类型只出现在本文件，<see cref="ITrayHost"/>、窗口状态机与设置页都无法感知它，
///   因此未来换成 Shell_NotifyIcon 实现时不需要改调用方；
/// - 初始化（图标/菜单/Shell 通知区域）可能因为资源缺失或系统限制失败，
///   失败时只把 <see cref="IsAvailable"/> 置为 false 并记录原因，绝不阻断启动。
///
/// 线程契约：必须在创建主窗口的 UI 线程上构造；NotifyIcon 依赖该线程的消息泵，
/// WPF Dispatcher 会泵送消息，因此菜单与双击回调回到 UI 线程。
/// </summary>
public sealed class WinFormsNotifyIconTrayHost : ITrayHost, ITrayHostStatus
{
    public const string UnavailableInitKey = "tray.unavailable.initFailed";

    private readonly ILogger<WinFormsNotifyIconTrayHost> _logger;
    private readonly ILocalizer? _localizer;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _menu;
    private Drawing.Icon? _icon;
    private bool _disposed;

    /// <summary>
    /// 构造即初始化：失败只降级为不可用。设置页会在托盘不可用时显示原因，
    /// 窗口状态机则回退到任务栏最小化 / 正常退出。
    /// </summary>
    public WinFormsNotifyIconTrayHost(
        ILogger<WinFormsNotifyIconTrayHost> logger,
        ILocalizer? localizer = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _localizer = localizer;

        try
        {
            _icon = LoadIcon();

            _menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
            var open = new Forms.ToolStripMenuItem(Text("tray.menu.open", "打开 ECL"));
            open.Click += (_, _) => RaiseRestoreRequested();
            var exit = new Forms.ToolStripMenuItem(Text("tray.menu.exit", "退出 ECL"));
            exit.Click += (_, _) => RaiseExitRequested();
            _menu.Items.Add(open);
            _menu.Items.Add(exit);

            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = _icon,
                Text = Text("tray.tooltip", "Ender Craft Launcher"),
                Visible = false,
                ContextMenuStrip = _menu,
            };
            // 双击图标是托盘最通用的“带回窗口”手势，和菜单第一项等价。
            _notifyIcon.DoubleClick += (_, _) => RaiseRestoreRequested();

            IsAvailable = true;
            _logger.LogInformation("系统托盘已初始化。");
        }
        catch (Exception exception)
        {
            // 托盘是便利入口，不是应用可用的前提：任何初始化异常都只降级并记录。
            IsAvailable = false;
            UnavailableReasonKey = UnavailableInitKey;
            UnavailableDetail = exception.Message;
            _logger.LogWarning(exception, "初始化系统托盘失败；最小化到托盘的行为将不可用。");
            DisposeCore();
        }
    }

    public bool IsAvailable { get; }

    public string? UnavailableReasonKey { get; }

    public string? UnavailableDetail { get; }

    public event EventHandler? RestoreRequested;

    public event EventHandler? ExitRequested;

    public void Show()
    {
        if (!IsAvailable || _disposed || _notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = true;
    }

    public void Hide()
    {
        if (_disposed || _notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCore();
    }

    private void RaiseRestoreRequested() => RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void RaiseExitRequested() => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 图标优先取当前进程 EXE 内嵌的应用图标（csproj 的 ApplicationIcon），
    /// 这样安装目录被改名或移动到别处都不依赖固定文件路径；否则退回到随包 ICO，
    /// 最后才用系统默认图标——托盘图标缺失不应导致托盘不可用。
    /// </summary>
    private Drawing.Icon LoadIcon()
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            var extracted = Drawing.Icon.ExtractAssociatedIcon(executable!);
            if (extracted is not null)
            {
                return extracted;
            }
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "ECL.ico");
        if (File.Exists(bundled))
        {
            return new Drawing.Icon(bundled);
        }

        return Drawing.SystemIcons.Application;
    }

    private void DisposeCore()
    {
        // 先隐藏再释放：图标句柄释放后仍留在通知区域会变成“幽灵图标”。
        if (_notifyIcon is not null)
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogDebug(exception, "释放托盘图标时失败。");
            }
            finally
            {
                _notifyIcon = null;
            }
        }

        _menu?.Dispose();
        _menu = null;
        _icon?.Dispose();
        _icon = null;
    }

    /// <summary>本地化缺失时退回到调用方给出的默认文案，不让托盘菜单变成键名。</summary>
    private string Text(string key, string fallback)
    {
        var localized = _localizer?.Get(key);
        return string.IsNullOrWhiteSpace(localized) || string.Equals(localized, key, StringComparison.Ordinal)
            ? fallback
            : localized;
    }
}
