// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services.Lifecycle;

namespace Ecl.App.Shell.Lifecycle;

/// <summary>主窗口关闭时用户可选的三条路径。取消不是“退出”，必须与关闭对话框可区分。</summary>
public enum CloseChoice
{
    MinimizeToTray,
    Exit,
    Cancel,
}

/// <summary>
/// 一次关闭询问的结论。<see cref="Remember"/> 为 true 时调用方把选择写成
/// <c>launcher.closeButtonAction</c>，但“取消”不会被记住（取消不是一种持久偏好）。
/// </summary>
public sealed record ClosePromptDecision(CloseChoice Choice, bool Remember);

/// <summary>关闭询问的展示参数；文案由界面本地化，服务不拼接用户可见文案。</summary>
public sealed record ClosePromptRequest(
    string Message,
    bool CanMinimizeToTray,
    string MinimizeUnavailableReason);

/// <summary>
/// 关闭询问入口。抽出接口是为了让窗口状态机可以在没有真实弹窗宿主的环境
/// （启动早期、测试）里仍然走同一条决策路径，而不是把分支散落在窗口代码里。
/// </summary>
public interface IClosePrompt
{
    /// <summary>三选一询问：最小化到托盘 / 退出 / 取消。</summary>
    Task<ClosePromptDecision> AskAsync(
        ClosePromptRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>展示真实活动任务数后确认退出；取消返回 false。</summary>
    Task<bool> ConfirmExitAsync(
        CloseReadiness readiness,
        CancellationToken cancellationToken = default);
}
