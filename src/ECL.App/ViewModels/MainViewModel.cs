// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core;
using Ecl.Core.Localization;

namespace Ecl.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly InstancesViewModel _instances;
    private readonly SettingsViewModel _settings;

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    [ObservableProperty]
    private NavItemViewModel _selectedNavItem;

    public string VersionText { get; }

    public string DataDirectory { get; }

    public MainViewModel(
        InstancesViewModel instances,
        SettingsViewModel settings,
        AboutViewModel about,
        IAppPaths paths,
        ILocalizer localizer)
    {
        _instances = instances;
        _settings = settings;

        NavItems =
        [
            new NavItemViewModel(localizer.Get("nav.instances"), instances),
            new NavItemViewModel(localizer.Get("nav.settings"), settings),
            new NavItemViewModel(localizer.Get("nav.about"), about),
        ];

        _selectedNavItem = NavItems[0];
        VersionText = $"ECL {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";
        DataDirectory = paths.Root;
    }

    public async Task InitializeAsync()
    {
        await _instances.InitializeAsync().ConfigureAwait(true);
        await _settings.InitializeAsync().ConfigureAwait(true);
    }
}

public sealed record NavItemViewModel(string Title, object Page);
