// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Shell.Lifecycle;

/// <summary>
/// 系统托盘宿主（F-12）。接口刻意不出现任何 WinForms 类型：
/// 窗口状态机与测试替身只依赖“是否可用 + 恢复/退出请求 + 显示/隐藏”，
/// 具体托盘实现可以被替换或在不支持的环境里退化为不可用。
/// </summary>
public interface ITrayHost : IDisposable
{
    /// <summary>托盘是否真的可用。false 时 <see cref="Show"/>/<see cref="Hide"/> 不产生效果。</summary>
    bool IsAvailable { get; }

    /// <summary>用户要求把主窗口带回前台（双击图标或菜单“打开 ECL”）。</summary>
    event EventHandler? RestoreRequested;

    /// <summary>用户通过托盘菜单要求退出 ECL；退出流程仍由窗口状态机决定。</summary>
    event EventHandler? ExitRequested;

    void Show();

    void Hide();
}

/// <summary>
/// 托盘不可用的稳定原因，供设置页如实说明。与 <see cref="ITrayHost"/> 分开，
/// 是因为“托盘能不能用”是实现细节，不需要污染窗口状态机的依赖面。
/// </summary>
public interface ITrayHostStatus
{
    /// <summary>本地化键（<c>tray.unavailable.*</c>）；可用时为 null。</summary>
    string? UnavailableReasonKey { get; }

    /// <summary>实现提供的原始错误文本，仅用于日志与诊断，不直接展示给用户。</summary>
    string? UnavailableDetail { get; }
}
