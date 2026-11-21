// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private bool _scrollToStartScheduled;

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
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
        _viewModel.ScrollToStartRequested += OnScrollToStartRequested;
        _viewModel.ScrollToLineRequested += OnScrollToLineRequested;
        _viewModel.Lines.CollectionChanged += OnLinesChanged;
        _tracked.Add(_viewModel.Lines);
        LogList.ItemsSource = _viewModel.Lines;
        if (ActualWidth > 0)
        {
            ApplyResponsiveLayout();
        }
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
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
            _viewModel.ScrollToStartRequested -= OnScrollToStartRequested;
            _viewModel.ScrollToLineRequested -= OnScrollToLineRequested;
            _viewModel = null;
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 按开头阅读证据时（崩溃报告/hs_err），跟随滚动不能把视图拽到末尾。
        if (_viewModel is { FollowsTail: true })
        {
            ScheduleScrollToEnd();
        }
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e) => ScheduleScrollToEnd();

    private void OnScrollToStartRequested(object? sender, EventArgs e) => ScheduleScrollToStart();

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

    /// <summary>证据按开头锚点打开时，把视图定位到第一行而不是末尾（错因通常在开头）。</summary>
    private void ScheduleScrollToStart()
    {
        if (_scrollToStartScheduled)
        {
            return;
        }

        _scrollToStartScheduled = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _scrollToStartScheduled = false;
                if (LogList.Items.Count > 0)
                {
                    LogList.ScrollIntoView(LogList.Items[0]);
                }
            }));
    }

    /// <summary>“更多”只展开与阅读器相关的低频操作；菜单本身不改变正文布局。</summary>
    private void OnReaderMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        // 从按钮下方展开，而不是跟随鼠标位置（点击后鼠标可能已经移开）。
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.SetNarrow(ActualWidth < 760);
        // 来源页签与工具栏共用中央阅读区的一行：按阅读区的实际宽度决定是否改用单行选择器，
        // 而不是只看页面宽度（会话栏是否展开会直接改变可用宽度）。
        var sessionListWidth = _viewModel.IsSessionListOpen && !_viewModel.IsNarrow ? 270d : 0d;
        var readerWidth = Math.Max(0d, ActualWidth - 48d - sessionListWidth);
        _viewModel.SetCompactSources(readerWidth < 880d);
    }

    /// <summary>会话栏展开/收起会改变阅读区宽度，必须重新判断来源选择器的形态。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogSessionsViewModel.IsSessionListOpen))
        {
            ApplyResponsiveLayout();
        }
    }

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
