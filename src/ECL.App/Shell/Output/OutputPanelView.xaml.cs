// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.Windows.Controls;

namespace Ecl.App.Shell.Output;

public partial class OutputPanelView : UserControl
{
    private OutputPanelViewModel? _viewModel;

    public OutputPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.OutputEntries.CollectionChanged -= OnOutputEntriesChanged;
        }

        _viewModel = DataContext as OutputPanelViewModel;
        if (_viewModel is not null)
        {
            _viewModel.OutputEntries.CollectionChanged += OnOutputEntriesChanged;
        }
    }

    /// <summary>输出行只在面板可见时滚动到末尾；不改变任何任务或日志状态。</summary>
    private void OnOutputEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && OutputScroll.IsVisible)
        {
            OutputScroll.ScrollToEnd();
        }
    }
}
