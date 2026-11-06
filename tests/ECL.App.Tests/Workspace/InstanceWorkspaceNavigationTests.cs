// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core.Localization;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 实例工作区二级导航（B2 步骤 2）：分组、顺序与默认页严格来自
/// `instance-workspace.md` §10；未实现页面只以不可用项出现，不建空页；
/// 每个工作区的导航状态互相独立，关闭时释放。
/// </summary>
public sealed class InstanceWorkspaceNavigationTests
{
    private static readonly string[] ExpectedKeys =
    [
        InstanceWorkspacePageKeys.BasicInfo,
        InstanceWorkspacePageKeys.Modify,
        InstanceWorkspacePageKeys.Mods,
        InstanceWorkspacePageKeys.ResourcePacks,
        InstanceWorkspacePageKeys.Shaders,
        InstanceWorkspacePageKeys.DataPacks,
        InstanceWorkspacePageKeys.Screenshots,
        InstanceWorkspacePageKeys.Schematics,
        InstanceWorkspacePageKeys.Export,
        InstanceWorkspacePageKeys.Logs,
    ];

    private static JsonLocalizer Localizer() => new(CultureInfo.GetCultureInfo("zh-CN"));

    private static InstanceWorkspaceNavigationViewModel CreateNavigation(
        out object basicInfoPage,
        out object modsPage,
        bool includeBasicInfo = true)
    {
        basicInfoPage = new object();
        modsPage = new object();

        var pages = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [InstanceWorkspacePageKeys.Mods] = modsPage,
        };
        if (includeBasicInfo)
        {
            pages[InstanceWorkspacePageKeys.BasicInfo] = basicInfoPage;
        }

        return InstanceWorkspaceNavigationFactory.Create(Localizer(), pages);
    }

    [Fact]
    public void NavigationMatchesApprovedGroupAndItemOrder()
    {
        var navigation = CreateNavigation(out var basicInfo, out _);

        Assert.Equal(ExpectedKeys, navigation.Items.Select(item => item.Key));
        Assert.Equal(
            ["概览", "配置", "内容", "世界与文件", "诊断"],
            navigation.Items.Select(item => item.GroupTitle).Distinct());
        Assert.Equal(
            [1, 1, 4, 3, 1],
            navigation.Items
                .GroupBy(item => item.GroupTitle)
                .Select(group => group.Count()));

        var defaultItem = navigation.Items[0];
        Assert.Equal(InstanceWorkspacePageKeys.BasicInfo, defaultItem.Key);
        Assert.Equal("基本信息", defaultItem.Title);
        Assert.Same(defaultItem, navigation.SelectedItem);
        Assert.Same(basicInfo, navigation.CurrentPage);
    }

    [Fact]
    public void OnlyPagesWithRealImplementationsAreAvailable()
    {
        var navigation = CreateNavigation(out _, out var modsPage);

        Assert.True(navigation.Find(InstanceWorkspacePageKeys.BasicInfo)!.IsAvailable);
        Assert.True(navigation.Find(InstanceWorkspacePageKeys.Mods)!.IsAvailable);
        Assert.Same(modsPage, navigation.Find(InstanceWorkspacePageKeys.Mods)!.Page);

        foreach (var key in ExpectedKeys.Except(
                     [InstanceWorkspacePageKeys.BasicInfo, InstanceWorkspacePageKeys.Mods]))
        {
            var item = navigation.Find(key);
            Assert.NotNull(item);
            Assert.False(item!.IsAvailable);
            Assert.NotEmpty(item.UnavailableReason);
            Assert.Null(item.Page);
        }

        // G-01：日志入口在页面规格批准前不得呈现为可用页面。
        Assert.Contains("G-01", navigation.Find(InstanceWorkspacePageKeys.Logs)!.UnavailableReason);
    }

    [Fact]
    public void UnavailablePageCannotBecomeTheCurrentPage()
    {
        var navigation = CreateNavigation(out var basicInfo, out _);

        Assert.False(navigation.TryActivate(InstanceWorkspacePageKeys.Modify));
        Assert.Equal(InstanceWorkspacePageKeys.BasicInfo, navigation.CurrentPageKey);

        navigation.SelectedItem = navigation.Find(InstanceWorkspacePageKeys.Schematics);

        Assert.Equal(InstanceWorkspacePageKeys.BasicInfo, navigation.SelectedItem!.Key);
        Assert.Same(basicInfo, navigation.CurrentPage);
    }

    [Fact]
    public void ActivatingAvailablePageSwitchesCurrentPage()
    {
        var navigation = CreateNavigation(out _, out var modsPage);

        Assert.True(navigation.TryActivate(InstanceWorkspacePageKeys.Mods));

        Assert.Equal(InstanceWorkspacePageKeys.Mods, navigation.CurrentPageKey);
        Assert.Same(modsPage, navigation.CurrentPage);
    }

    [Fact]
    public void ModifyPageBecomesAvailableWhenTheWorkspaceProvidesIt()
    {
        var modifyPage = new object();
        var navigation = InstanceWorkspaceNavigationFactory.Create(
            Localizer(),
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [InstanceWorkspacePageKeys.BasicInfo] = new object(),
                [InstanceWorkspacePageKeys.Mods] = new object(),
                [InstanceWorkspacePageKeys.Modify] = modifyPage,
            });

        var item = navigation.Find(InstanceWorkspacePageKeys.Modify);
        Assert.NotNull(item);
        Assert.True(item!.IsAvailable);
        Assert.Same(modifyPage, item.Page);
        Assert.Equal(string.Empty, item.UnavailableReason);
        Assert.Equal("修改", item.Title);
        Assert.Equal("配置", item.GroupTitle);

        Assert.True(navigation.TryActivate(InstanceWorkspacePageKeys.Modify));
        Assert.Equal(InstanceWorkspacePageKeys.Modify, navigation.CurrentPageKey);
        Assert.Same(modifyPage, navigation.CurrentPage);
    }

    [Fact]
    public void ResourcePackPageBecomesAvailableWhenTheWorkspaceProvidesIt()
    {
        var packsPage = new object();
        var navigation = InstanceWorkspaceNavigationFactory.Create(
            Localizer(),
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [InstanceWorkspacePageKeys.BasicInfo] = new object(),
                [InstanceWorkspacePageKeys.Mods] = new object(),
                [InstanceWorkspacePageKeys.ResourcePacks] = packsPage,
            });

        var item = navigation.Find(InstanceWorkspacePageKeys.ResourcePacks);
        Assert.NotNull(item);
        Assert.True(item!.IsAvailable);
        Assert.Same(packsPage, item.Page);
        Assert.Equal(string.Empty, item.UnavailableReason);
        Assert.Equal("资源包", item.Title);
        Assert.Equal("内容", item.GroupTitle);

        // 光影包页仍按 B2b 的后续步骤保持不可用，不创建空页。
        var shaders = navigation.Find(InstanceWorkspacePageKeys.Shaders);
        Assert.False(shaders!.IsAvailable);
        Assert.NotEqual(string.Empty, shaders.UnavailableReason);

        Assert.True(navigation.TryActivate(InstanceWorkspacePageKeys.ResourcePacks));
        Assert.Equal(InstanceWorkspacePageKeys.ResourcePacks, navigation.CurrentPageKey);
        Assert.Same(packsPage, navigation.CurrentPage);
    }

    [Fact]
    public void TwoWorkspacesKeepIndependentNavigationState()
    {
        var first = CreateNavigation(out var firstBasic, out var firstMods);
        var second = CreateNavigation(out var secondBasic, out var secondMods);

        Assert.True(first.TryActivate(InstanceWorkspacePageKeys.Mods));

        Assert.Same(firstMods, first.CurrentPage);
        Assert.Same(secondBasic, second.CurrentPage);
        Assert.NotSame(first.CurrentPage, second.CurrentPage);
        Assert.Equal(InstanceWorkspacePageKeys.BasicInfo, second.CurrentPageKey);
        Assert.NotSame(firstBasic, secondBasic);
    }

    [Fact]
    public void MissingDefaultPageFallsBackToFirstAvailablePage()
    {
        var navigation = CreateNavigation(out _, out var modsPage, includeBasicInfo: false);

        Assert.False(navigation.Find(InstanceWorkspacePageKeys.BasicInfo)!.IsAvailable);
        Assert.Equal(InstanceWorkspacePageKeys.Mods, navigation.CurrentPageKey);
        Assert.Same(modsPage, navigation.CurrentPage);
    }

    [Fact]
    public void DisposeClearsSelectionAndIsIdempotent()
    {
        var navigation = CreateNavigation(out _, out _);

        navigation.Dispose();
        navigation.Dispose();

        Assert.True(navigation.IsDisposed);
        Assert.Null(navigation.SelectedItem);
        Assert.Null(navigation.CurrentPage);
        Assert.Empty(navigation.Items);
        Assert.Equal(string.Empty, navigation.CurrentPageKey);
    }
}
