// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Media.Animation;
using Ecl.App.Services;

namespace Ecl.App.Dialogs;

/// <summary>
/// 弹窗动效规格（交接 §3.3 / §3.4）：进入、退出与层叠切换的时长、位移、缩放集中在这里，
/// 宿主视图按这些数值创建动画，页面与各弹窗内容不得各自写 Storyboard 或魔法数字。
///
/// 动画只作用于表现层：弹窗的业务结果、模态、焦点与遮罩清理都不依赖动画完成回调；
/// 减少动画偏好（用户关闭动效或 Windows“减少动画”）时所有时长归零，
/// 调用方必须立即进入终态，而不是跳过状态更新。
/// </summary>
public static class DialogMotion
{
    /// <summary>弹窗进入：淡入 + 从下方轻微上移 + 轻微放大到 1。</summary>
    public static readonly TimeSpan EnterDurationFull = TimeSpan.FromMilliseconds(160);

    /// <summary>弹窗退出：短促淡出 + 轻微缩小/上移，必须先于遮罩与宿主状态释放完成。</summary>
    public static readonly TimeSpan ExitDurationFull = TimeSpan.FromMilliseconds(120);

    /// <summary>层叠切换（关闭顶层露出下一层）：交叉淡入淡出，比独立进入/退出更快。</summary>
    public static readonly TimeSpan SwitchDurationFull = TimeSpan.FromMilliseconds(140);

    /// <summary>进入时的起始纵向位移（正数表示从下方进入）。</summary>
    public const double EnterOffset = 12;

    /// <summary>退出时的目标纵向位移（负数为向上收拢）。</summary>
    public const double ExitOffset = -6;

    /// <summary>进入时的起始缩放；接近 1，避免“弹跳”式夸张效果。</summary>
    public const double EnterScale = 0.975;

    /// <summary>退出时收缩到的缩放。</summary>
    public const double ExitScale = 0.985;

    public static TimeSpan EnterDuration => EnterDurationFor(MotionSettings.IsReducedMotion);

    public static TimeSpan ExitDuration => ExitDurationFor(MotionSettings.IsReducedMotion);

    public static TimeSpan SwitchDuration => SwitchDurationFor(MotionSettings.IsReducedMotion);

    public static TimeSpan EnterDurationFor(bool reducedMotion) => reducedMotion ? TimeSpan.Zero : EnterDurationFull;

    public static TimeSpan ExitDurationFor(bool reducedMotion) => reducedMotion ? TimeSpan.Zero : ExitDurationFull;

    public static TimeSpan SwitchDurationFor(bool reducedMotion) => reducedMotion ? TimeSpan.Zero : SwitchDurationFull;

    /// <summary>进入缓动：快速减速，观感上“落定”而不是滑行。</summary>
    public static IEasingFunction CreateEnterEase() => new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>退出缓动：加速离开，让关闭感觉干脆并有明确方向。</summary>
    public static IEasingFunction CreateExitEase() => new CubicEase { EasingMode = EasingMode.EaseIn };
}
