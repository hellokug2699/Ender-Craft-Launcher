// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Input;
using Ecl.App.ViewModels;

namespace Ecl.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            _ => -1,
        };

        if (index >= 0 && index < viewModel.NavItems.Count)
        {
            viewModel.SelectedNavItem = viewModel.NavItems[index];
            e.Handled = true;
        }
    }
}
