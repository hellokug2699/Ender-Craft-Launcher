// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.ViewModels;
using Ecl.App.Workspace;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 实例工作区自有状态（B2 步骤 1）：身份来自打开时的实例快照，
/// 两个工作区互不影响；释放幂等且只通知一次。
/// </summary>
public sealed class InstanceWorkspaceStateTests
{
    private static InstanceItemViewModel Item(string id, string name)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "ecl-workspace-state-tests"));
        var localizer = new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));
        return new InstanceItemViewModel(
            new GameInstance
            {
                Id = id,
                Name = name,
                GameVersion = "1.21.1",
                VersionId = "1.21.1-fabric",
                Loader = "fabric",
                LoaderVersion = "0.16.10",
            },
            localizer,
            paths);
    }

    [Fact]
    public void TwoWorkspacesKeepTheirOwnIdentity()
    {
        var first = new InstanceWorkspaceState(Item("a", "实例 A"));
        var second = new InstanceWorkspaceState(Item("b", "实例 B"));

        Assert.Equal("a", first.InstanceId);
        Assert.Equal("实例 A", first.Title);
        Assert.Equal("b", second.InstanceId);
        Assert.Equal("实例 B", second.Title);
        Assert.NotEqual(first.InstanceId, second.InstanceId);
    }

    [Fact]
    public void DisposingOneWorkspaceDoesNotAffectTheOther()
    {
        var first = new InstanceWorkspaceState(Item("a", "实例 A"));
        var second = new InstanceWorkspaceState(Item("b", "实例 B"));

        first.Dispose();

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        Assert.Equal("b", second.InstanceId);
    }

    [Fact]
    public void DisposeIsIdempotentAndNotifiesOnce()
    {
        var state = new InstanceWorkspaceState(Item("a", "实例 A"));
        var notifications = 0;
        state.Disposed += (_, _) => notifications++;

        state.Dispose();
        state.Dispose();

        Assert.Equal(1, notifications);
        Assert.True(state.IsDisposed);
    }

    [Fact]
    public void ReplacingTheItemRefreshesIdentityWithoutChangingTheInstance()
    {
        var state = new InstanceWorkspaceState(Item("a", "实例 A"));
        var notifications = 0;
        state.IdentityChanged += (_, _) => notifications++;

        // 受控变更成功后的新记录：同一 InstanceId，新版本。
        var refreshed = new InstanceItemViewModel(
            new GameInstance
            {
                Id = "a",
                Name = "实例 A",
                GameVersion = "1.21",
                VersionId = "1.21-fabric",
                Loader = "fabric",
                LoaderVersion = "0.16.10",
            },
            new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")),
            new AppPaths(Path.Combine(Path.GetTempPath(), "ecl-workspace-state-tests")));

        state.ReplaceItem(refreshed);

        Assert.Equal("a", state.InstanceId);
        Assert.Equal("1.21", state.Model.GameVersion);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void ReplacingTheItemWithAnotherInstanceIsRejected()
    {
        var state = new InstanceWorkspaceState(Item("a", "实例 A"));

        Assert.Throws<InvalidOperationException>(() => state.ReplaceItem(Item("b", "实例 B")));
        Assert.Equal("a", state.InstanceId);
    }

    [Fact]
    public void ReplacingTheItemAfterDisposeIsRejected()
    {
        var state = new InstanceWorkspaceState(Item("a", "实例 A"));
        state.Dispose();

        Assert.Throws<ObjectDisposedException>(() => state.ReplaceItem(Item("a", "实例 A")));
    }
}
