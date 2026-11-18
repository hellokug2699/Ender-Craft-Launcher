// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services.Confirmation;
using Ecl.App.Tests.Support;
using Ecl.Configuration.Settings;
using Xunit;

namespace Ecl.App.Tests.Services;

/// <summary>
/// F-12 确认策略：普通确认可以按偏好关闭，强制确认永远弹窗。
/// 这条边界决定了“开关不能替代确认”，所以单独断言。
/// </summary>
public sealed class ConfirmationPolicyTests
{
    [Fact]
    public void MandatoryConfirmationAlwaysRequiresUserApproval()
    {
        var settings = new FakeSettingsService();
        settings.Values[LauncherKeys.ConfirmOrdinaryActions] = "false";
        var policy = new ConfirmationPolicy(settings);

        Assert.True(policy.RequiresConfirmation(ConfirmationKind.Mandatory));
    }

    [Fact]
    public void OrdinaryConfirmationFollowsThePreference()
    {
        var settings = new FakeSettingsService();
        var policy = new ConfirmationPolicy(settings);

        // 默认开启：普通操作仍然询问一次。
        Assert.True(policy.RequiresConfirmation(ConfirmationKind.Ordinary));

        settings.Values[LauncherKeys.ConfirmOrdinaryActions] = "false";
        Assert.False(policy.RequiresConfirmation(ConfirmationKind.Ordinary));
    }

    [Fact]
    public void UnknownKindIsTreatedAsMandatory()
    {
        var settings = new FakeSettingsService();
        settings.Values[LauncherKeys.ConfirmOrdinaryActions] = "false";
        var policy = new ConfirmationPolicy(settings);

        Assert.True(policy.RequiresConfirmation((ConfirmationKind)99));
    }

    [Fact]
    public void DefaultPreferenceKeepsExistingConfirmationBehaviour()
    {
        // 未写入过任何值时，既有 30 处 Confirm 调用点（默认 Mandatory）行为不变。
        var policy = new ConfirmationPolicy(new FakeSettingsService());

        Assert.True(policy.RequiresConfirmation(ConfirmationKind.Mandatory));
        Assert.True(policy.RequiresConfirmation(ConfirmationKind.Ordinary));
    }
}
