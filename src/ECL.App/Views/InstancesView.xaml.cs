// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Controls;
using System.Windows.Input;
using Ecl.App.ViewModels;

namespace Ecl.App.Views;

public partial class InstancesView : UserControl
{
    public InstancesView()
    {
        InitializeComponent();
    }

    private void OnInstanceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is InstancesViewModel viewModel && viewModel.SelectedInstance is not null)
        {
            viewModel.OpenWorkspaceCommand.Execute(viewModel.SelectedInstance);
        }
    }
}
