// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using Ecl.App.ViewModels;

namespace Ecl.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
