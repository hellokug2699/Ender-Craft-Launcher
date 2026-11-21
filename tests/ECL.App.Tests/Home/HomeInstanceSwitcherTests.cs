// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels.Home;
using Xunit;

namespace Ecl.App.Tests.Home;

/// <summary>
/// 主页中央实例切换栏（home.md §4，2026-11-21 维护者修订）：一格一实例、格子数量等于实例数量、
/// 折叠态显示前四个（不足按实际数量）、编辑模式互换、删除不留空位、排列立即保存并可跨会话恢复，
/// 以及“不提供创建/复制入口”的边界。
/// </summary>
public sealed class HomeInstanceSwitcherTests
{
    [Fact]
    public void LayoutHasExactlyOneCellPerInstance()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        var switcher = CreateSwitcher(source, new FakeHomeLayoutStore());

        switcher.Load();

        // 不再补齐到四个位置：有多少实例就多少格子。
        Assert.Equal(2, switcher.Layout.Count);
        Assert.DoesNotContain(switcher.Layout, slot => slot.IsEmpty);
        Assert.Equal(["a", "b"], switcher.Collapsed.Select(slot => slot.Instance!.Model.Id));
        Assert.True(switcher.HasInstances);
    }

    [Fact]
    public void ManyInstancesGetOneCellEachCollapsedShowsTheFirstFour()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        foreach (var id in new[] { "a", "b", "c", "d", "e", "f" })
        {
            source.Items.Add(HomeTestData.Instance(id, paths));
        }

        var switcher = CreateSwitcher(source, new FakeHomeLayoutStore());
        switcher.Load();

        Assert.Equal(6, switcher.Layout.Count);
        Assert.Equal(["a", "b", "c", "d"], switcher.Collapsed.Select(slot => slot.Instance!.Model.Id));
    }

    [Fact]
    public void LoadingBeforeInstancesArriveDoesNotReserveEmptyCells()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        var switcher = CreateSwitcher(source, new FakeHomeLayoutStore());

        // 主页先建立骨架，实例列表稍后才由 Shell 初始化。
        switcher.Load();
        Assert.Empty(switcher.Layout);

        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));

        Assert.Equal(2, switcher.Layout.Count);
        Assert.Equal("a", switcher.Layout[0].Instance!.Model.Id);
        Assert.Equal("b", switcher.Layout[1].Instance!.Model.Id);
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

        Assert.Equal(2, switcher.Layout.Count);
        Assert.Equal(["a", "b"], switcher.Collapsed.Select(slot => slot.Instance!.Model.Id));
    }

    [Fact]
    public void EditModeMoveSwapsTwoPositionsAndSavesImmediately()
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
        // 折叠区是排列中的前四个实例，因此互换会改变主页默认实例。
        Assert.Equal(["e", "b", "c", "d"], switcher.Collapsed.Select(slot => slot.Instance!.Model.Id));
        Assert.True(store.SaveCount >= 1);
        Assert.Equal(["e", "b", "c", "d", "a"], store.Load());
    }

    [Fact]
    public void MoveBeyondTheInstanceCountIsRejected()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        var store = new FakeHomeLayoutStore();
        var switcher = CreateSwitcher(source, store);
        switcher.Load();
        switcher.ToggleExpandedCommand.Execute(null);
        switcher.ToggleEditModeCommand.Execute(null);

        // 一格一实例：没有“展示用空位”可以拖入，越界位置不产生新格子。
        Assert.False(switcher.MoveSlot(0, 2));
        Assert.Equal(2, switcher.Layout.Count);
        Assert.Equal(["a", "b"], store.Load());
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
    public void RemovedInstanceLeavesNoHole()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        source.Items.Add(HomeTestData.Instance("c", paths));
        var store = new FakeHomeLayoutStore();
        var switcher = CreateSwitcher(source, store);
        switcher.Load();

        source.Items.RemoveAt(1);

        Assert.Equal(2, switcher.Layout.Count);
        Assert.DoesNotContain(switcher.Layout, slot => slot.IsEmpty);
        Assert.Equal(["a", "c"], switcher.Layout.Select(slot => slot.Instance!.Model.Id));
        Assert.Equal(["a", "c"], store.Load());
    }

    [Fact]
    public void ExplicitRemovalNotificationDropsTheCellAndPersists()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        var store = new FakeHomeLayoutStore();
        var switcher = CreateSwitcher(source, store);
        switcher.Load();

        switcher.NotifyInstanceRemoved("a");

        Assert.Equal(["b"], store.Load());
        Assert.Single(switcher.Layout);
    }

    [Fact]
    public void SavedOrderIsRestoredAcrossSwitcherInstances()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        source.Items.Add(HomeTestData.Instance("c", paths));
        var store = new FakeHomeLayoutStore();

        var first = CreateSwitcher(source, store);
        first.Load();
        first.ToggleExpandedCommand.Execute(null);
        first.ToggleEditModeCommand.Execute(null);
        Assert.True(first.MoveSlot(0, 2));

        // 重新进入主页/重启 ECL：同一份存储必须恢复用户刚才的排列。
        var second = CreateSwitcher(source, store);
        second.Load();

        Assert.Equal(["c", "b", "a"], second.Layout.Select(slot => slot.Instance!.Model.Id));
        Assert.Equal(["c", "b", "a"], second.Collapsed.Select(slot => slot.Instance!.Model.Id));
    }

    [Fact]
    public void LegacyHolesAndDuplicatesAreDroppedInsteadOfBecomingCells()
    {
        var paths = HomeTestData.CreatePaths();
        var source = new FakeInstanceSource();
        source.Items.Add(HomeTestData.Instance("a", paths));
        source.Items.Add(HomeTestData.Instance("b", paths));
        var store = new FakeHomeLayoutStore();
        // 旧版本可能保存了上百个空位或重复项：读取后必须只剩真实实例。
        store.Seed("b", null, "a", "b");

        var switcher = CreateSwitcher(source, store);
        switcher.Load();

        Assert.Equal(["b", "a"], switcher.Layout.Select(slot => slot.Instance!.Model.Id));
        Assert.DoesNotContain(switcher.Layout, slot => slot.IsEmpty);
        Assert.Equal(["b", "a"], store.Load());
    }

    [Fact]
    public async Task LegacyLayoutFileIsRewrittenWithoutHoles()
    {
        var paths = HomeTestData.CreatePaths();
        paths.EnsureCreated();
        var file = Path.Combine(paths.ConfigDirectory, "home-layout.json");
        var holes = string.Join(",", Enumerable.Repeat("null", 40));
        await File.WriteAllTextAsync(
            file,
            $$"""{"Slots":["instance-a",{{holes}},"instance-b"]}""");

        var store = new HomeLayoutStore(
            paths,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HomeLayoutStore>.Instance);
        var loaded = store.Load();

        Assert.Equal(["instance-a", "instance-b"], loaded);
        // 读取时立即回写：文件自身不再携带空位，下次保存不会把空洞写回来。
        var rewritten = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain("null", rewritten);
        Assert.Equal(["instance-a", "instance-b"], store.Load());
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
