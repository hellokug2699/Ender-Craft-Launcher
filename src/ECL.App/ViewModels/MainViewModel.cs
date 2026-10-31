// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core;
using Ecl.Core.Localization;

namespace Ecl.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly HomeViewModel _home;
    private readonly DownloadViewModel _download;
    private readonly ModsViewModel _mods;
    private readonly SettingsViewModel _settings;
    private readonly InstancesViewModel _instances;
    private readonly InstanceDetailPageViewModel _instanceDetailPage;
    private bool _showingInstanceDetails;

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    public OperationCenterViewModel OperationCenter { get; }

    public object CurrentPage => _showingInstanceDetails ? _instanceDetailPage : SelectedNavItem.Page;

    [ObservableProperty]
    private NavItemViewModel _selectedNavItem;

    public string VersionText { get; }

    /// <summary>形如“构建于 2026-10-31 13:40”，便于区分分发出去的二进制。</summary>
    public string BuildTimeText { get; }

    public string DataDirectory { get; }

    public string GameDirectory { get; }

    public MainViewModel(
        HomeViewModel home,
        DownloadViewModel download,
        InstancesViewModel instances,
        ModsViewModel mods,
        SettingsViewModel settings,
        AboutViewModel about,
        OperationCenterViewModel operationCenter,
        IAppPaths paths,
        ILocalizer localizer)
    {
        _home = home;
        _download = download;
        _mods = mods;
        _settings = settings;
        _instances = instances;
        _instanceDetailPage = new InstanceDetailPageViewModel(instances, mods);
        OperationCenter = operationCenter;

        NavItems =
        [
            new NavItemViewModel("home", localizer.Get("nav.home"), home),
            new NavItemViewModel("download", localizer.Get("nav.download"), download),
            new NavItemViewModel("instances", localizer.Get("nav.instances"), instances),
            new NavItemViewModel("settings", localizer.Get("nav.settings"), settings),
            new NavItemViewModel("about", localizer.Get("nav.about"), about),
        ];

        _selectedNavItem = NavItems[0];
        _home.Navigate = NavigateTo;
        _home.OpenInstanceWorkspace = OpenSelectedInstanceWorkspace;
        _instances.GetModHealthSummary = _mods.GetHealthSummaryAsync;
        _mods.SearchDependency = OpenDependencySearch;
        _instances.NavigateToDetails = OpenInstanceDetails;
        _instances.NavigateBackFromDetails = BackToInstances;
        VersionText = $"ECL {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";
        BuildTimeText = string.IsNullOrWhiteSpace(BuildInfo.BuildTime)
            ? string.Empty
            : localizer.Get("app.buildTime", BuildInfo.BuildTime);
        DataDirectory = paths.Root;
        GameDirectory = paths.GameRoot;
    }

    private void OpenDependencySearch(string dependencyId, string instanceId)
    {
        _ = OpenDependencySearchAsync(dependencyId, instanceId);
    }

    private async Task OpenDependencySearchAsync(string dependencyId, string instanceId)
    {
        await _download.SearchDependencyAsync(dependencyId, instanceId).ConfigureAwait(true);
        NavigateTo("download");
    }

    /// <summary>主页“已安装内容”直接进入选中实例的工作区；没有选中实例时回到实例库。</summary>
    private void OpenSelectedInstanceWorkspace()
    {
        if (_instances.SelectedInstance is { } instance)
        {
            OpenInstanceDetails(instance.Model.Id);
            return;
        }

        NavigateTo("instances");
    }

    private void OpenInstanceDetails(string instanceId)
    {
        _mods.SelectInstance(instanceId);
        _showingInstanceDetails = true;
        OnPropertyChanged(nameof(CurrentPage));

        // 工作区里的“模组与内容”使用最新的实例列表与扫描结果。
        _ = _mods.ActivateAsync();
    }

    private void BackToInstances()
    {
        _showingInstanceDetails = false;
        OnPropertyChanged(nameof(CurrentPage));
    }

    private void NavigateTo(string key)
    {
        var target = NavItems.FirstOrDefault(item => item.Key == key);
        if (target is not null)
        {
            SelectedNavItem = target;
        }
    }

    public async Task InitializeAsync()
    {
        await _home.Launcher.InitializeAsync().ConfigureAwait(true);
        await _download.InitializeAsync().ConfigureAwait(true);
        await _mods.InitializeAsync().ConfigureAwait(true);
        await _settings.InitializeAsync().ConfigureAwait(true);
    }

    partial void OnSelectedNavItemChanged(NavItemViewModel value)
    {
        _showingInstanceDetails = false;
        OnPropertyChanged(nameof(CurrentPage));

        switch (value.Page)
        {
            case DownloadViewModel download:
                _ = download.ActivateAsync();
                break;
        }
    }
}

public sealed record NavItemViewModel(string Key, string Title, object Page);

public sealed record InstanceDetailPageViewModel(InstancesViewModel Instances, ModsViewModel Mods);
