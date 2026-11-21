// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Media.Animation;
using Ecl.App.Dialogs;
using Ecl.App.Services;
using Xunit;

namespace Ecl.App.Tests.Dialogs;

/// <summary>
/// 弹窗动效规格（交接 §3.3 / §3.4）：时长、位移与缩放集中定义；
/// 减少动画偏好下所有时长归零，但状态与焦点语义不变。
/// </summary>
public sealed class DialogMotionTests
{
    [Fact]
    public void DurationsAreShortAndOrdered()
    {
        // 退出必须比进入短促：关闭不能拖沓，也不能先消失遮罩再收卡片。
        Assert.True(DialogMotion.ExitDurationFull < DialogMotion.EnterDurationFull);
        Assert.True(DialogMotion.SwitchDurationFull < DialogMotion.EnterDurationFull);
        Assert.True(DialogMotion.EnterDurationFull < TimeSpan.FromMilliseconds(250));
        Assert.True(DialogMotion.ExitDurationFull >= TimeSpan.FromMilliseconds(80));
    }

    [Fact]
    public void ReducedMotionCollapsesEveryDialogDuration()
    {
        Assert.Equal(TimeSpan.Zero, DialogMotion.EnterDurationFor(reducedMotion: true));
        Assert.Equal(TimeSpan.Zero, DialogMotion.ExitDurationFor(reducedMotion: true));
        Assert.Equal(TimeSpan.Zero, DialogMotion.SwitchDurationFor(reducedMotion: true));

        Assert.Equal(DialogMotion.EnterDurationFull, DialogMotion.EnterDurationFor(reducedMotion: false));
        Assert.Equal(DialogMotion.ExitDurationFull, DialogMotion.ExitDurationFor(reducedMotion: false));
    }

    [Fact]
    public void PropertiesFollowTheGlobalMotionPreference()
    {
        // 不做全局状态写入（测试并行安全）：属性必须与当前偏好推导一致。
        Assert.Equal(DialogMotion.EnterDurationFor(MotionSettings.IsReducedMotion), DialogMotion.EnterDuration);
        Assert.Equal(DialogMotion.ExitDurationFor(MotionSettings.IsReducedMotion), DialogMotion.ExitDuration);
        Assert.Equal(DialogMotion.SwitchDurationFor(MotionSettings.IsReducedMotion), DialogMotion.SwitchDuration);
    }

    [Fact]
    public void TransformValuesStaySubtle()
    {
        Assert.InRange(DialogMotion.EnterScale, 0.9, 0.999);
        Assert.InRange(DialogMotion.ExitScale, 0.9, 1.0);
        Assert.True(DialogMotion.EnterOffset > 0, "进入时应从下方轻微上移。");
        Assert.True(DialogMotion.ExitOffset < 0, "退出时应向上收拢，方向与进入相反。");
        Assert.True(Math.Abs(DialogMotion.EnterOffset) <= 24, "位移应克制，不产生大幅飞入。");
    }

    [Fact]
    public void EnterUsesEaseOutAndExitUsesEaseIn()
    {
        var enter = Assert.IsType<CubicEase>(DialogMotion.CreateEnterEase());
        var exit = Assert.IsType<CubicEase>(DialogMotion.CreateExitEase());

        Assert.Equal(EasingMode.EaseOut, enter.EasingMode);
        Assert.Equal(EasingMode.EaseIn, exit.EasingMode);
    }
}
