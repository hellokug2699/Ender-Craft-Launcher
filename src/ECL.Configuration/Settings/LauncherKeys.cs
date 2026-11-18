// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Configuration.Settings;

/// <summary>
/// 主程序设置与桌面生命周期相关的全局配置键（F-12）。
///
/// 这些键只在设置元数据中登记，具体呈现由设置中心“常规”页负责（本切片不修改页面）。
/// 键本身承载真实语义：自动启动注册、关闭/最小化行为、更新检查周期、通知开关与
/// “普通操作二次确认 / 成功结果弹窗自动关闭”策略，都不是占位开关。
///
/// <see cref="UpdateLastCheckedAt"/> 与 <see cref="UpdateLastCheckedChannel"/> 是调度器写入的
/// 内部状态（<c>IsHidden</c>），只用于判断“是否到期”和“通道是否变化”，不是用户偏好。
/// </summary>
public static class LauncherKeys
{
    /// <summary>是否在 Windows 登录时启动 ECL（真实写入 HKCU Run 项）。</summary>
    public const string StartWithWindows = "launcher.startWithWindows";

    /// <summary>登录启动时直接最小化到托盘，而不是显示主窗口。</summary>
    public const string StartMinimizedToTray = "launcher.startMinimizedToTray";

    /// <summary>启动后是否回到主页工作区。</summary>
    public const string ShowHomeOnStartup = "launcher.showHomeOnStartup";

    /// <summary>点击主窗口关闭按钮时的行为。</summary>
    public const string CloseButtonAction = "launcher.closeButtonAction";

    /// <summary>点击最小化按钮时的行为。</summary>
    public const string MinimizeAction = "launcher.minimizeAction";

    /// <summary>关闭主窗口前是否确认（普通确认，默认开启）。</summary>
    public const string ConfirmOnClose = "launcher.confirmOnClose";

    /// <summary>启动后自动检查更新的周期。</summary>
    public const string UpdateCheckInterval = "launcher.updateCheckInterval";

    public const string NotifyTaskCompleted = "launcher.notifyTaskCompleted";

    public const string NotifyTaskFailed = "launcher.notifyTaskFailed";

    /// <summary>后台任务状态变化（暂停、停滞、重试）是否提示；不是任务完成的替代。</summary>
    public const string NotifyBackgroundTasks = "launcher.notifyBackgroundTasks";

    /// <summary>操作提示（Hint 类通知）是否显示。</summary>
    public const string ShowOperationHints = "launcher.showOperationHints";

    /// <summary>
    /// 普通操作（可逆、非破坏性）是否需要二次确认。
    /// 强制确认（删除托管数据、吊销信任等）不受该开关影响。
    /// </summary>
    public const string ConfirmOrdinaryActions = "launcher.confirmOrdinaryActions";

    /// <summary>成功结果弹窗是否在短时间后自动关闭；关键结果永远等待用户。</summary>
    public const string AutoCloseSuccessDialogs = "launcher.autoCloseSuccessDialogs";

    /// <summary>最近一次成功更新检查的时间（UTC，往返格式）。内部调度状态。</summary>
    public const string UpdateLastCheckedAt = "launcher.update.lastCheckedAt";

    /// <summary>最近一次成功更新检查使用的通道值（原始设置值）。内部调度状态。</summary>
    public const string UpdateLastCheckedChannel = "launcher.update.lastCheckedChannel";

    /// <summary>是否启用更新检查（既有键，调度器与关于页共用）。</summary>
    public const string CheckUpdates = "launcher.checkUpdates";

    /// <summary>更新通道（既有键）。</summary>
    public const string UpdateChannel = "launcher.updateChannel";

    /// <summary>关闭按钮行为的规范值。</summary>
    public static class CloseButtonActions
    {
        public const string Ask = "ask";
        public const string MinimizeToTray = "minimizeToTray";
        public const string Exit = "exit";
    }

    /// <summary>最小化行为的规范值。</summary>
    public static class MinimizeActions
    {
        public const string Taskbar = "taskbar";
        public const string Tray = "tray";
    }

    /// <summary>更新检查周期的规范值；解析失败按 <see cref="Startup"/> 处理。</summary>
    public static class UpdateCheckIntervals
    {
        public const string Startup = "startup";
        public const string Daily = "daily";
        public const string Weekly = "weekly";
    }
}
