// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core.Localization;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 实例工作区二级页面的稳定 Key。顺序、分组与归属的唯一权威来源是
/// <c>hints/ui/pages/instance-workspace.md</c> §10；这里的 Key 只用于代码内引用，
/// 不是深链接语法，也不得据此自行增删页面。
/// </summary>
public static class InstanceWorkspacePageKeys
{
    public const string BasicInfo = "overview.basic";
    public const string Modify = "configure.modify";
    public const string Mods = "content.mods";
    public const string ResourcePacks = "content.resourcePacks";
    public const string Shaders = "content.shaders";
    public const string DataPacks = "content.dataPacks";
    public const string Screenshots = "files.screenshots";
    public const string Schematics = "files.schematics";
    public const string Export = "files.export";
    public const string Logs = "diagnostics.logs";
}

/// <summary>
/// 一个实例二级导航项。页面实现存在时 <see cref="Page"/> 非空且可用；
/// 尚未实现的页面不创建空页，只保留导航项并以准确的不可用原因呈现，
/// 不会被伪装成可点击入口（§10、迁移规划 §5.3「不可用」态）。
/// </summary>
public sealed class InstanceNavigationItemViewModel
{
    public InstanceNavigationItemViewModel(
        string key,
        string title,
        string groupKey,
        string groupTitle,
        object? page,
        string unavailableReason = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupTitle);

        Key = key;
        Title = title;
        GroupKey = groupKey;
        GroupTitle = groupTitle;
        Page = page;
        UnavailableReason = page is null ? unavailableReason : string.Empty;
    }

    public string Key { get; }

    public string Title { get; }

    public string GroupKey { get; }

    /// <summary>已本地化的分组标题，供分组列表直接使用。</summary>
    public string GroupTitle { get; }

    /// <summary>该导航项对应的页面 ViewModel；未实现时为 <c>null</c>，不构造空页。</summary>
    public object? Page { get; }

    /// <summary>不可用原因的本地化文案；可用时为空串。</summary>
    public string UnavailableReason { get; }

    public bool IsAvailable => Page is not null;
}

/// <summary>
/// 实例工作区二级导航状态（B2 步骤 2）。
///
/// 导航项、分组与默认页严格来自 <c>instance-workspace.md</c> §10；切换二级页只改变
/// 本工作区的 <see cref="CurrentPage"/>，不新开工作区、不改变工作区标题（迁移规划 §5.1）。
/// 每个实例工作区一个实例，互不共享选中项与页面对象；关闭工作区时由
/// <see cref="InstanceDetailPageViewModel"/> 释放。
/// </summary>
public sealed class InstanceWorkspaceNavigationViewModel : ObservableObject, IDisposable
{
    private InstanceNavigationItemViewModel? _selectedItem;

    public InstanceWorkspaceNavigationViewModel(
        IEnumerable<InstanceNavigationItemViewModel> items,
        string defaultKey)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items = new ObservableCollection<InstanceNavigationItemViewModel>(items);
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(InstanceNavigationItemViewModel.GroupTitle)));

        _selectedItem = Items.FirstOrDefault(item =>
                            string.Equals(item.Key, defaultKey, StringComparison.Ordinal) && item.IsAvailable)
                        ?? Items.FirstOrDefault(item => item.IsAvailable);

        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(CurrentPageKey));
    }

    /// <summary>按 §10 顺序排列的全部导航项（含不可用项）。</summary>
    public ObservableCollection<InstanceNavigationItemViewModel> Items { get; }

    /// <summary>按分组标题分组的导航视图；导航列表直接绑定它。</summary>
    public ICollectionView ItemsView { get; }

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// 当前选中的二级页。绑定层（ListBox）只能选中可用项；这里再次拒绝不可用项，
    /// 防止代码或键盘路径绕过禁用状态进入空页。
    /// </summary>
    public InstanceNavigationItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (value is { IsAvailable: false })
            {
                return;
            }

            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(CurrentPage));
                OnPropertyChanged(nameof(CurrentPageKey));
            }
        }
    }

    /// <summary>当前二级页面的数据上下文；没有可用页面时为 <c>null</c>。</summary>
    public object? CurrentPage => SelectedItem?.Page;

    public string CurrentPageKey => SelectedItem?.Key ?? string.Empty;

    public InstanceNavigationItemViewModel? Find(string key) =>
        Items.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));

    /// <summary>按 Key 激活二级页；Key 不存在或页面未实现时返回 <c>false</c> 并保持当前页。</summary>
    public bool TryActivate(string key)
    {
        if (Find(key) is not { IsAvailable: true } item)
        {
            return false;
        }

        SelectedItem = item;
        return true;
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        SelectedItem = null;
        Items.Clear();
    }
}

/// <summary>
/// 根据 §10 的权威导航树构造实例工作区二级导航。
/// 只有调用方提供了页面对象的条目才可用；没有页面的条目保留为不可用项并给出来源明确的
/// 原因，不创建空页、不伪造入口。
/// </summary>
public static class InstanceWorkspaceNavigationFactory
{
    private sealed record GroupSpec(string Key, string TitleKey, (string Key, string TitleKey)[] Items);

    private static readonly GroupSpec[] Tree =
    [
        new(
            "overview",
            "instance.nav.group.overview",
            [(InstanceWorkspacePageKeys.BasicInfo, "instance.nav.basicInfo")]),
        new(
            "configure",
            "instance.nav.group.configure",
            [(InstanceWorkspacePageKeys.Modify, "instance.nav.modify")]),
        new(
            "content",
            "instance.nav.group.content",
            [
                (InstanceWorkspacePageKeys.Mods, "instance.nav.mods"),
                (InstanceWorkspacePageKeys.ResourcePacks, "instance.nav.resourcePacks"),
                (InstanceWorkspacePageKeys.Shaders, "instance.nav.shaders"),
                (InstanceWorkspacePageKeys.DataPacks, "instance.nav.dataPacks"),
            ]),
        new(
            "files",
            "instance.nav.group.files",
            [
                (InstanceWorkspacePageKeys.Screenshots, "instance.nav.screenshots"),
                (InstanceWorkspacePageKeys.Schematics, "instance.nav.schematics"),
                (InstanceWorkspacePageKeys.Export, "instance.nav.export"),
            ]),
        new(
            "diagnostics",
            "instance.nav.group.diagnostics",
            [(InstanceWorkspacePageKeys.Logs, "instance.nav.logs")]),
    ];

    public static InstanceWorkspaceNavigationViewModel Create(
        ILocalizer localizer,
        IReadOnlyDictionary<string, object> pages)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(pages);

        var items = new List<InstanceNavigationItemViewModel>();
        foreach (var group in Tree)
        {
            var groupTitle = localizer.Get(group.TitleKey);
            foreach (var (key, titleKey) in group.Items)
            {
                pages.TryGetValue(key, out var page);
                var unavailableReason = page is null
                    ? localizer.Get(string.Equals(key, InstanceWorkspacePageKeys.Logs, StringComparison.Ordinal)
                        ? "instance.nav.unavailable.logsSpec"
                        : "instance.nav.unavailable.notImplemented")
                    : string.Empty;

                items.Add(new InstanceNavigationItemViewModel(
                    key,
                    localizer.Get(titleKey),
                    group.Key,
                    groupTitle,
                    page,
                    unavailableReason));
            }
        }

        return new InstanceWorkspaceNavigationViewModel(items, InstanceWorkspacePageKeys.BasicInfo);
    }
}
