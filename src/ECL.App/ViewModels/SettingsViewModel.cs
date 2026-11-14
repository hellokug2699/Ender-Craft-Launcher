// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.App.ViewModels.Settings;
using Ecl.Configuration.Settings;
using Ecl.Core.Diagnostics;
using Ecl.Core.Localization;
using Ecl.Deployment;
using Ecl.Minecraft.Launch;

namespace Ecl.App.ViewModels;

/// <summary>
/// 设置中心 Shell（B3 步骤 1）。
///
/// 按 <c>settings.md</c> §3 使用纵向二级导航（常规 / 界面偏好 / Java 管理 /
/// 游戏与启动 / 下载 / 网络 / 存储与路径 / 扩展管理），首次进入默认“常规”，
/// 切换分类保留当前会话选择，重新进入设置中心回到常规。
///
/// 页面内的设置项由 ADR 0002 的元数据驱动；本类不保存全局/实例作用域开关，
/// 实例专属配置归实例工作区（C-13）。未实现的分类只在导航中保留准确的不可用状态。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;

    public SettingsViewModel(
        ISettingsService settings,
        ILocalizer localizer,
        CodeSigningTrustService codeSigning,
        IDialogService dialogs,
        SettingsJavaPageViewModel javaPage,
        IMemoryAllocationService? memoryAllocation = null,
        Ecl.Configuration.Network.IProxyCredentialStore? proxyCredentials = null,
        Ecl.Download.IProxyConnectionTester? proxyTester = null,
        Ecl.Core.IAppPaths? paths = null,
        Ecl.Core.Storage.IWorkspaceService? workspace = null,
        Ecl.Core.Storage.IWorkspaceMigrationService? migration = null,
        Ecl.Operations.IOperationStore? operations = null,
        IAdditionsService? additions = null)
    {
        _localizer = localizer;
        Navigation = SettingsPageCatalog.CreateNavigation(
            settings,
            localizer,
            dialogs,
            codeSigning,
            javaPage,
            memoryAllocation ?? new MemoryAllocationService(settings, new SystemMemoryProbe()),
            proxyCredentials,
            proxyTester,
            paths,
            workspace,
            migration,
            operations,
            additions);
        Navigation.PropertyChanged += OnNavigationPropertyChanged;
    }

    /// <summary>8 个固定分类的纵向二级导航；不可用分类保留说明且不可选中。</summary>
    public SettingsNavigationViewModel Navigation { get; }

    /// <summary>存储与路径页；Shell 用它接入「管理存储空间」入口（页面不实现清理）。</summary>
    public SettingsStoragePageViewModel? StoragePage =>
        Navigation.Find(SettingsPageKeys.Storage)?.Page as SettingsStoragePageViewModel;

    public ObservableCollection<SettingsSearchResultViewModel> SearchResults { get; } = [];

    /// <summary>窄窗口下的布局收缩状态（ADR 0050）：导航收为内容区顶部的页面选择器。</summary>
    [ObservableProperty]
    private bool _isNavigationCollapsed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasSearchResults;

    public bool HasNoSearchResults => IsSearching && !HasSearchResults;

    public string SearchSummary => _localizer.Get("settings.search.results", SearchResults.Count);

    public string ResetDefaultsText => _localizer.Get("settings.reset.command");

    /// <summary>当前分类是否提供“恢复本分类默认值”；Java 管理页没有元数据默认值可恢复。</summary>
    public bool CanResetCurrentPage => Navigation.CurrentPage?.CanResetDefaults == true;

    /// <summary>
    /// 进入设置中心（首次打开或从其它目的地返回）：按 §2 回到“常规”，
    /// 不重置用户在本页各设置项上已经持久化的值。
    /// </summary>
    public void Activate()
    {
        SearchText = string.Empty;
        Navigation.ResetToDefault();
    }

    [RelayCommand]
    private void OpenSearchResult(SettingsSearchResultViewModel? result)
    {
        if (result is null)
        {
            return;
        }

        // 先清空搜索，让内容区切回页面本身，再激活页面并定位设置项。
        SearchText = string.Empty;
        if (!Navigation.TryActivate(result.PageKey))
        {
            return;
        }

        Navigation.CurrentPage?.Highlight(result.OptionKey);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ResetCurrentPage()
    {
        // 所有已实现页面都通过 ISettingsPage 提供按分类恢复默认值；
        // 不要再只识别通用元数据页（界面偏好/游戏与启动是专用页面）。
        Navigation.CurrentPage?.ResetToDefaults();
    }
    partial void OnSearchTextChanged(string value) => UpdateSearch();

    private void UpdateSearch()
    {
        var query = SearchText.Trim();
        IsSearching = query.Length > 0;
        SearchResults.Clear();

        if (IsSearching)
        {
            foreach (var item in Navigation.Items.Where(item => item.IsAvailable))
            {
                foreach (var entry in item.Page!.SearchEntries)
                {
                    if (Matches(entry.Title, query) || Matches(entry.Description, query))
                    {
                        SearchResults.Add(new SettingsSearchResultViewModel(entry));
                    }
                }
            }
        }

        HasSearchResults = SearchResults.Count > 0;
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(SearchSummary));
    }

    private static bool Matches(string text, string query) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void OnNavigationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsNavigationViewModel.CurrentPage))
        {
            OnPropertyChanged(nameof(ResetDefaultsText));
            OnPropertyChanged(nameof(CanResetCurrentPage));
        }
    }
}
