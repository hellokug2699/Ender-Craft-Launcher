// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.ViewModels;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 实例级操作结果状态按工作区各持一份（B2 步骤 1c）；这里锁定“互不共享”这一契约，
/// 避免以后把它写成静态状态或共享集合。
/// </summary>
public sealed class InstanceOperationStateTests
{
    [Fact]
    public void TwoStatesDoNotShareCollections()
    {
        var first = new InstanceOperationState();
        var second = new InstanceOperationState();

        Assert.NotSame(first.LaunchSteps, second.LaunchSteps);
        Assert.Empty(first.LaunchSteps);
        Assert.Empty(second.LaunchSteps);
    }

    [Fact]
    public void ResetOnlyClearsItsOwnReport()
    {
        var first = new InstanceOperationState
        {
            LaunchStatus = "A 已启动",
            LaunchCommandPreview = "java -jar a.jar",
            InstallStatus = "A 正在补全",
        };
        var second = new InstanceOperationState { LaunchStatus = "B 已启动" };

        first.ResetLaunchReport();

        Assert.Empty(first.LaunchSteps);
        Assert.Equal(string.Empty, first.LaunchStatus);
        Assert.Equal(string.Empty, first.LaunchCommandPreview);
        Assert.Equal(string.Empty, first.InstallStatus);
        Assert.Equal("B 已启动", second.LaunchStatus);
    }
}
