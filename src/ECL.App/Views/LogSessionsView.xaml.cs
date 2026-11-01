// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.Windows.Controls;
using Ecl.App.ViewModels;

namespace Ecl.App.Views;

public partial class LogSessionsView : UserControl
{
    private INotifyCollectionChanged? _tracked;

    public LogSessionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Track();
    }

    private void Track()
    {
        if (_tracked is not null)
        {
            _tracked.CollectionChanged -= OnLinesChanged;
        }

        _tracked = (DataContext as LogSessionsViewModel)?.Lines;
        if (_tracked is not null)
        {
            _tracked.CollectionChanged += OnLinesChanged;
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is LogSessionsViewModel { Follow: true })
        {
            LogScroller.ScrollToEnd();
        }
    }
}
