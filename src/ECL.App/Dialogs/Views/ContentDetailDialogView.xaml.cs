// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using Ecl.App.Dialogs.ViewModels;

namespace Ecl.App.Dialogs.Views;

public partial class ContentDetailDialogView : UserControl
{
    public ContentDetailDialogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => LoadIfPossible();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => LoadIfPossible();

    private void LoadIfPossible()
    {
        if (IsLoaded && DataContext is ContentDetailDialogViewModel viewModel)
        {
            _ = viewModel.LoadAsync();
        }
    }
}
