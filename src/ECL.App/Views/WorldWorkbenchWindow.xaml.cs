// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Media;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.Minecraft.World;

namespace Ecl.App.Views;

public partial class WorldWorkbenchWindow : Window
{
    private readonly WorldWorkbenchViewModel _viewModel;

    public WorldWorkbenchWindow(
        WorldWorkbenchService service,
        IDialogService dialogs,
        string instanceId,
        string instanceName)
    {
        InitializeComponent();
        _viewModel = new WorldWorkbenchViewModel(service, dialogs, instanceId, instanceName);
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private void OnMapZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MapImageElement is null)
        {
            return;
        }

        MapImageElement.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue);
    }
}
