// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Shell.Inspector;
using Ecl.Core.Localization;
using Xunit;

namespace Ecl.App.Tests.Shell;

/// <summary>
/// Inspector 选择层的最小契约（ADR 0056 §3、迁移规划 §4.5）：
/// 只读摘要、可下发/清空、切换工作区后不残留上一工作区的选择。
/// </summary>
public sealed class InspectorSelectionTests
{
    private static InspectorViewModel CreateViewModel() =>
        new(new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));

    [Fact]
    public void NoSelectionByDefault()
    {
        var inspector = CreateViewModel();

        Assert.False(inspector.HasSelection);
        Assert.Null(inspector.Selection);
    }

    [Fact]
    public void ShowSelectionPublishesReadOnlySummary()
    {
        var inspector = CreateViewModel();

        inspector.ShowSelection(new InspectorSelectionContext(
            "生存模式",
            "Forge 1.20.1",
            [new InspectorSelectionField("来源", "本机导入")]));

        Assert.True(inspector.HasSelection);
        Assert.Equal("生存模式", inspector.Selection!.Title);
        Assert.Equal("Forge 1.20.1", inspector.Selection.Subtitle);
        Assert.Equal("来源", Assert.Single(inspector.Selection.Fields).Label);
    }

    [Fact]
    public void ClearSelectionKeepsWorkspaceLayer()
    {
        var inspector = CreateViewModel();
        inspector.ShowSelection(new InspectorSelectionContext("生存模式"));

        inspector.ClearSelection();

        Assert.False(inspector.HasSelection);
        Assert.Null(inspector.Selection);
    }

    [Fact]
    public void ClearingWorkspaceContextAlsoClearsSelection()
    {
        var inspector = CreateViewModel();
        inspector.ShowSelection(new InspectorSelectionContext("生存模式"));

        inspector.Clear();

        Assert.False(inspector.HasContext);
        Assert.False(inspector.HasSelection);
    }

    [Fact]
    public void SelectionHeadingIsLocalized()
    {
        var inspector = CreateViewModel();

        Assert.Equal("当前选中", inspector.SelectionHeading);
    }
}
