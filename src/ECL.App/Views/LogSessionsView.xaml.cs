// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ecl.App.ViewModels;
using Ecl.App.ViewModels.LogSessions;

namespace Ecl.App.Views;

/// <summary>
/// 日志阅读器的视图协作：跟随滚动、搜索命中定位与窄窗口折叠。
/// 业务状态、会话生命周期与读取全部在 ViewModel 与领域服务中，视图只处理滚动锚点。
/// </summary>
public partial class LogSessionsView : UserControl
{
    private readonly List<INotifyCollectionChanged> _tracked = [];
    private LogSessionsViewModel? _viewModel;
    private bool _scrollToEndScheduled;

    public LogSessionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        SizeChanged += OnSizeChanged;
    }

    private void Attach()
    {
        if (DataContext is not LogSessionsViewModel viewModel || ReferenceEquals(viewModel, _viewModel))
        {
            return;
        }

        Detach();
        _viewModel = viewModel;
        _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
        _viewModel.ScrollToLineRequested += OnScrollToLineRequested;
        _viewModel.Lines.CollectionChanged += OnLinesChanged;
        _tracked.Add(_viewModel.Lines);
        LogList.ItemsSource = _viewModel.Lines;
    }

    private void Detach()
    {
        foreach (var collection in _tracked)
        {
            collection.CollectionChanged -= OnLinesChanged;
        }

        _tracked.Clear();

        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
            _viewModel.ScrollToLineRequested -= OnScrollToLineRequested;
            _viewModel = null;
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is { Follow: true })
        {
            ScheduleScrollToEnd();
        }
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e) => ScheduleScrollToEnd();

    private void OnScrollToLineRequested(object? sender, LogLineViewModel line) =>
        LogList.ScrollIntoView(line);

    /// <summary>
    /// 一轮增量追加可能产生多条 Add 通知；跟随滚动合并为一次，避免逐行
    /// <see cref="ItemsControl.ScrollIntoView"/> 触发重复布局（logs.md §6.3）。
    /// </summary>
    private void ScheduleScrollToEnd()
    {
        if (_scrollToEndScheduled)
        {
            return;
        }

        _scrollToEndScheduled = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _scrollToEndScheduled = false;
                ScrollToEnd();
            }));
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        _viewModel?.SetNarrow(ActualWidth < 760);

    /// <summary>
    /// F-09 S8：<c>SelectedItems</c> 不是可绑定属性，多选结果通过本事件推给 ViewModel；
    /// 视图不解释选择含义，也不复制日志内容。
    /// </summary>
    private void OnLogListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not LogSessionsViewModel viewModel)
        {
            return;
        }

        viewModel.UpdateLineSelection(
            LogList.SelectedItems.OfType<LogLineViewModel>().ToList());
    }
}
