// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;

namespace Ecl.App.Services;

/// <summary>
/// 动效策略：遵循 Windows 的减少动画偏好。关闭动效时所有时长归零，
/// 交互状态仍由颜色/边框/焦点立即表达。
/// </summary>
public static class MotionSettings
{
    public static bool IsReducedMotion => !SystemParameters.ClientAreaAnimation;

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
