// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;

namespace Ecl.App.Services;

/// <summary>
/// 动效策略：用户偏好（<c>launcher.animationsEnabled</c>）与 Windows 的“减少动画”
/// 系统偏好共同决定是否使用动画。任一来源要求减少动效时时长归零，
/// 交互状态仍由颜色/边框/焦点立即表达（ADR 0021 §6）。
///
/// 用户偏好由 <see cref="UserInterfacePreferences"/> 在设置变更时写入；页面/模板
/// 只读取这里的结果，不各自判断配置。
/// </summary>
public static class MotionSettings
{
    private static bool _userEnabled = true;

    /// <summary>用户是否开启界面动效；默认开启。关闭后不影响状态反馈与后台任务。</summary>
    public static bool UserEnabled
    {
        get => _userEnabled;
        set => _userEnabled = value;
    }

    /// <summary>操作系统已声明减少动画，或用户在 ECL 中关闭动效。</summary>
    public static bool IsReducedMotion => !UserEnabled || !SystemParameters.ClientAreaAnimation;

    public static TimeSpan Instant => IsReducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(60);

    public static TimeSpan Fast => IsReducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(120);

    public static TimeSpan Normal => IsReducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(180);

    public static Duration ToDuration(TimeSpan value) => new(value);

    /// <summary>页面转场只作用于表现层；返回 false 时调用方必须立即进入最终状态。</summary>
    public static bool TryBeginPageTransition(UIElement element)
    {
        if (IsReducedMotion)
        {
            element.Opacity = 1;
            return false;
        }

        return true;
    }
}
