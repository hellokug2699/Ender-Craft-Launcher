// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Tests.Support;
using Ecl.App.ViewModels.Home;
using Xunit;

namespace Ecl.App.Tests.Home;

/// <summary>
/// 主页中央实例切换栏（home.md §4）：折叠四格、展开可整理布局、编辑模式互换、
/// 删除后保留空位、排列持久化，以及“不提供创建/复制入口”的边界。
/// </summary>
public sealed class HomeInstanceSwitcherTests
{
    [Fact]
    public void LoadShowsFourDefaultSlotsAndOnlyExistingInstancesCollapsed()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        var switcher = CreateSwitcher(source, new FakeHomeLayoutStore());

        switcher.Load();

        Assert.Equal(4, switcher.Layout.Count);
        Assert.Equal(["a", "b"], switcher.Collapsed.Select(slot => slot.Instance!.Model.Id));
        Assert.Equal(2, switcher.Layout.Count(slot => slot.IsEmpty));
        Assert.True(switcher.HasInstances);
    }

    [Fact]
    public void LoadingBeforeInstancesArriveDoesNotCreateLeadingEmptyPositions()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        var switcher = CreateSwitcher(source, new FakeHomeLayoutStore());

        // 主页先建立骨架，实例列表稍后才由 Shell 初始化。
        switcher.Load();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));

        Assert.Equal("a", switcher.Layout[0].Instance!.Model.Id);
        Assert.Equal("b", switcher.Layout[1].Instance!.Model.Id);
        Assert.Equal(4, switcher.Layout.Count);
    }

    [Fact]
    public void NewlyRecognizedInstanceIsAppendedToTheLayout()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        var switcher = CreateSwitcher(source, new FakeHomeLayoutStore());

        switcher.Load();
        source.Items.Add(HomeTestData.Instance("b", paths));

        Assert.Equal(["a", "b"], switcher.Collapsed.Select(slot => slot.Instance!.Model.Id));
        // 新识别到的实例追加到排列末尾；已有的空位不会被自动补上（§4.4）。
        Assert.Equal("b", switcher.Layout[1].Instance!.Model.Id);
        Assert.True(switcher.Layout[2].IsEmpty);
    }

    [Fact]
    public void EditModeMoveSwapsTwoOccupiedPositions()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            source.Items.Add(HomeTestData.Instance(id, paths));
        }

        var store = new FakeHomeLayoutStore();
        var switcher = CreateSwitcher(source, store);
        switcher.Load();
        switcher.ToggleExpandedCommand.Execute(null);
        switcher.ToggleEditModeCommand.Execute(null);

        Assert.True(switcher.MoveSlot(0, 4));

        Assert.Equal("e", switcher.Layout[0].Instance!.Model.Id);
        Assert.Equal("a", switcher.Layout[4].Instance!.Model.Id);
        // 折叠区始终是“前四个位置上的实例”，所以互换会改变主页默认实例。
        Assert.Equal(["e", "b", "c", "d"], switcher.Collapsed.Select(slot => slot.Instance!.Model.Id));
        Assert.True(store.SaveCount >= 1);
        Assert.Equal(["e", "b", "c", "d", "a"], store.Load());
    }

    [Fact]
    public void MoveIntoEmptySlotLeavesAnEmptyPosition()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        var switcher = CreateSwitcher(source, new FakeHomeLayoutStore());
        switcher.Load();
        switcher.ToggleExpandedCommand.Execute(null);
        switcher.ToggleEditModeCommand.Execute(null);

        Assert.True(switcher.MoveSlot(0, 2));

        Assert.True(switcher.Layout[0].IsEmpty);
        Assert.Equal("a", switcher.Layout[2].Instance!.Model.Id);
    }

    [Fact]
    public void MoveRequiresExpandedEditMode()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        var switcher = CreateSwitcher(source, new FakeHomeLayoutStore());
        switcher.Load();

        Assert.False(switcher.MoveSlot(0, 1));

        switcher.ToggleExpandedCommand.Execute(null);
        Assert.False(switcher.MoveSlot(0, 1));

        switcher.ToggleEditModeCommand.Execute(null);
        Assert.True(switcher.MoveSlot(0, 1));
    }

    [Fact]
    public void RemovedInstanceKeepsItsPositionAsAnEmptySlot()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        var store = new FakeHomeLayoutStore();
        var switcher = CreateSwitcher(source, store);
        switcher.Load();

        source.Items.RemoveAt(1);

        Assert.True(switcher.Layout[1].IsEmpty);
        Assert.Single(switcher.Collapsed);
        Assert.Equal(["a", null], store.Load());
    }

    [Fact]
    public void SavedLayoutOrderIsRestoredWithHolesPreserved()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        var store = new FakeHomeLayoutStore();
        store.Seed("b", null, "a");
        var switcher = CreateSwitcher(source, store);

        switcher.Load();

        Assert.Equal("b", switcher.Layout[0].Instance!.Model.Id);
        Assert.True(switcher.Layout[1].IsEmpty);
        Assert.Equal("a", switcher.Layout[2].Instance!.Model.Id);
        Assert.Equal(["b", "a"], switcher.Collapsed.Select(slot => slot.Instance!.Model.Id));
    }

    [Fact]
    public void SelectionIsBlockedWhileLaunching()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        var switcher = CreateSwitcher(source, new FakeHomeLayoutStore());
        switcher.Load();

        switcher.SelectInstanceCommand.Execute(switcher.Collapsed[1]);
        Assert.Equal("b", source.SelectedInstance!.Model.Id);

        switcher.CanSwitch = false;
        switcher.SelectInstanceCommand.Execute(switcher.Collapsed[0]);
        Assert.Equal("b", source.SelectedInstance!.Model.Id);
    }

    [Fact]
    public void EditAndDeleteModesAreExclusive()
    {
        var switcher = CreateSwitcher(new FakeInstanceSource(), new FakeHomeLayoutStore());

        switcher.ToggleEditModeCommand.Execute(null);
        Assert.True(switcher.IsEditMode);
        Assert.True(switcher.IsExpanded);

        switcher.ToggleDeleteModeCommand.Execute(null);
        Assert.True(switcher.IsDeleteMode);
        Assert.False(switcher.IsEditMode);

        switcher.ToggleEditModeCommand.Execute(null);
        Assert.True(switcher.IsEditMode);
        Assert.False(switcher.IsDeleteMode);
    }

    [Fact]
    public void SwitcherExposesNoCreateCopyOrImportCommands()
    {
        // home.md §4.5：实例创建、复制、导入不属于中央实例切换栏。
        var names = typeof(HomeInstanceSwitcherViewModel)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name => name.EndsWith("Command", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(names, name =>
            name.Contains("Create", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Copy", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Import", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    private static HomeInstanceSwitcherViewModel CreateSwitcher(
        FakeInstanceSource source,
        FakeHomeLayoutStore store) =>
        new(source, store, HomeTestData.Localizer);
}


