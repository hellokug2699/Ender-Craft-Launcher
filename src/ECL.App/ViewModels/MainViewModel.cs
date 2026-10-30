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

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    [ObservableProperty]
    private NavItemViewModel _selectedNavItem;

    public string VersionText { get; }

    public string DataDirectory { get; }

    public string GameDirectory { get; }

    public MainViewModel(
        HomeViewModel home,
        DownloadViewModel download,
        InstancesViewModel instances,
        ModsViewModel mods,
        SettingsViewModel settings,
        AboutViewModel about,
        IAppPaths paths,
        ILocalizer localizer)
    {
        _home = home;
        _download = download;
        _mods = mods;
        _settings = settings;

        NavItems =
        [
            new NavItemViewModel("home", localizer.Get("nav.home"), home),
            new NavItemViewModel("download", localizer.Get("nav.download"), download),
            new NavItemViewModel("instances", localizer.Get("nav.instances"), instances),
            new NavItemViewModel("content", localizer.Get("nav.content"), mods),
            new NavItemViewModel("settings", localizer.Get("nav.settings"), settings),
            new NavItemViewModel("about", localizer.Get("nav.about"), about),
        ];

        _selectedNavItem = NavItems[0];
        _home.Navigate = NavigateTo;
        VersionText = $"ECL {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";
        DataDirectory = paths.Root;
        GameDirectory = paths.GameRoot;
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
        switch (value.Page)
        {
            case ModsViewModel mods:
                _ = mods.ActivateAsync();
                break;
            case DownloadViewModel download:
                _ = download.ActivateAsync();
                break;
        }
    }
}

public sealed record NavItemViewModel(string Key, string Title, object Page);
