// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 设置中心二级页面的稳定 Key。顺序与归属的唯一权威来源是
/// <c>hints/ui/pages/settings.md</c> §3.2（8 个分类，常规默认）；
/// 这里的 Key 只用于代码内引用，不是深链接语法。
/// </summary>
public static class SettingsPageKeys
{
    public const string General = "settings.general";
    public const string Interface = "settings.interface";
    public const string Java = "settings.java";
    public const string GameLaunch = "settings.game";
    public const string Downloads = "settings.downloads";
    public const string Network = "settings.network";
    public const string Storage = "settings.storage";
    public const string Additions = "settings.additions";
}

/// <summary>
/// 一个可定位的设置项：设置搜索按名称与说明匹配，激活结果后切到所属二级页并高亮该项。
/// </summary>
public sealed record SettingsSearchEntry(
    string PageKey,
    string PageTitle,
    string OptionKey,
    string Title,
    string Description);

/// <summary>
/// 设置中心可用的二级页面。未实现的分类不构造空页，只在导航中保留准确的不可用原因
/// （与实例工作区 §10 的导航约定相同）。
/// </summary>
public interface ISettingsPage
{
    string Key { get; }

    string Title { get; }

    string Subtitle { get; }

    /// <summary>供设置搜索使用的可定位项；只包含页面真实渲染的设置。</summary>
    IReadOnlyList<SettingsSearchEntry> SearchEntries { get; }

    /// <summary>页面被选中或重新进入设置中心时调用；用于惰性读取页面状态。</summary>
    void Activate();

    /// <summary>只把本页的设置项恢复为元数据默认值，不触碰其它分类或文件。</summary>
    void ResetToDefaults();

    /// <summary>
    /// 本页是否支持“恢复本分类默认值”。Java 管理页的列表与登记不是元数据默认值，
    /// 不提供会把用户登记误清空的按钮。
    /// </summary>
    bool CanResetDefaults => true;

    /// <summary>高亮并滚动到指定设置项；项不存在时返回 <c>false</c>。</summary>
    bool Highlight(string optionKey);
}

/// <summary>
/// 设置中心二级导航项。页面实现存在时 <see cref="Page"/> 非空且可用；
/// 未实现的分类不建空页，只保留导航项并给出准确的不可用原因。
/// </summary>
public sealed class SettingsNavigationItemViewModel
{
    public SettingsNavigationItemViewModel(
        string key,
        string title,
        ISettingsPage? page,
        string unavailableReason = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Key = key;
        Title = title;
        Page = page;
        UnavailableReason = page is null ? unavailableReason : string.Empty;
    }

    public string Key { get; }

    public string Title { get; }

    public ISettingsPage? Page { get; }

    public string UnavailableReason { get; }

    public bool IsAvailable => Page is not null;
}

/// <summary>
/// 设置中心二级导航状态（B3 步骤 1）：
/// - 分类顺序固定为 <c>settings.md</c> §3.2，默认“常规”；
/// - 切换分类只改变当前页，保留当前会话选择；
/// - 不可用分类保留在列表中，但不允许被选中，也不构造空页。
/// </summary>
public sealed class SettingsNavigationViewModel : ObservableObject
{
    private SettingsNavigationItemViewModel? _selectedItem;

    public SettingsNavigationViewModel(
        IEnumerable<SettingsNavigationItemViewModel> items,
        string defaultKey)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items = new ObservableCollection<SettingsNavigationItemViewModel>(items);
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        _selectedItem = Items.FirstOrDefault(item =>
                            string.Equals(item.Key, defaultKey, StringComparison.Ordinal) && item.IsAvailable)
                        ?? Items.FirstOrDefault(item => item.IsAvailable);

        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(CurrentPageTitle));
    }

    public ObservableCollection<SettingsNavigationItemViewModel> Items { get; }

    /// <summary>导航列表直接绑定它；分类没有分组，因此不使用 GroupStyle。</summary>
    public ICollectionView ItemsView { get; }

    public SettingsNavigationItemViewModel? SelectedItem
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
                OnPropertyChanged(nameof(CurrentPageTitle));

                // 用户点击导航时只改变选中项；页面的惰性读取必须在这里触发
                // （设置搜索与重新进入走 TryActivate/ResetToDefault，同样会激活）。
                value?.Page?.Activate();
            }
        }
    }

    public ISettingsPage? CurrentPage => SelectedItem?.Page;

    public string CurrentPageTitle => CurrentPage?.Title ?? string.Empty;

    public SettingsNavigationItemViewModel? Find(string key) =>
        Items.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));

    /// <summary>按 Key 激活二级页；Key 不存在或分类未实现时返回 <c>false</c> 并保持当前页。</summary>
    public bool TryActivate(string key)
    {
        if (Find(key) is not { IsAvailable: true } item)
        {
            return false;
        }

        SelectedItem = item;
        item.Page!.Activate();
        return true;
    }

    /// <summary>重新进入设置中心时回到默认分类（常规），符合 <c>settings.md</c> §2/§3。</summary>
    public void ResetToDefault()
    {
        if (Items.FirstOrDefault(item => item.Key == SettingsPageKeys.General) is { IsAvailable: true } general)
        {
            SelectedItem = general;
            general.Page!.Activate();
        }
    }
}

/// <summary>设置搜索的一条命中结果；激活后切页并高亮对应设置项。</summary>
public sealed class SettingsSearchResultViewModel(SettingsSearchEntry entry)
{
    public SettingsSearchEntry Entry { get; } = entry;

    public string PageKey => Entry.PageKey;

    public string PageTitle => Entry.PageTitle;

    public string OptionKey => Entry.OptionKey;

    public string Title => Entry.Title;

    public string Description => Entry.Description;
}
