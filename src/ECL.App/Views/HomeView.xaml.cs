// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ecl.App.ViewModels;
using Ecl.App.ViewModels.Home;

namespace Ecl.App.Views;

public partial class HomeView : UserControl
{
    private Point _dragOrigin;
    private HomeInstanceSlotViewModel? _dragSource;

    public HomeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplyCompact(ActualWidth);
    }

    /// <summary>
    /// 窄窗口（侧栏与 Inspector 同时展开的 1080×720）切换到紧凑布局：
    /// 实例切换栏改为两列、下方三个区域纵向排列并保持等高，避免文字被裁切到不可读。
    /// </summary>
    private void OnHomeSizeChanged(object sender, SizeChangedEventArgs e) => ApplyCompact(e.NewSize.Width);

    private void ApplyCompact(double width)
    {
        if (DataContext is HomeViewModel home)
        {
            home.IsCompactLayout = width < 700;
        }
    }

    /// <summary>
    /// 编辑模式下拖动实例整理排列（home.md §4.3）：拖动到已有实例的位置即互换，
    /// 拖到空位即移动过去。拖动本身不改变实例的创建、复制或删除状态。
    /// </summary>
    private void OnLayoutPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not HomeViewModel { Switcher.IsEditMode: true } || sender is not ItemsControl list)
        {
            _dragSource = null;
            return;
        }

        _dragOrigin = e.GetPosition(list);
        _dragSource = SlotAt(e.OriginalSource as DependencyObject);
    }

    private void OnLayoutPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSource is not { } source
            || e.LeftButton != MouseButtonState.Pressed
            || DataContext is not HomeViewModel { Switcher.IsEditMode: true }
            || sender is not ItemsControl list)
        {
            return;
        }

        var position = e.GetPosition(list);
        if (Math.Abs(position.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragSource = null;
        DragDrop.DoDragDrop(
            list,
            new DataObject(typeof(HomeInstanceSlotViewModel), source),
            DragDropEffects.Move);
    }

    private void OnLayoutDragOver(object sender, DragEventArgs e)
    {
        var allowed = DataContext is HomeViewModel { Switcher.IsEditMode: true }
                      && e.Data.GetDataPresent(typeof(HomeInstanceSlotViewModel));
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnLayoutDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not HomeViewModel { Switcher.IsEditMode: true } home
            || e.Data.GetData(typeof(HomeInstanceSlotViewModel)) is not HomeInstanceSlotViewModel source
            || SlotAt(e.OriginalSource as DependencyObject) is not { } target)
        {
            return;
        }

        home.Switcher.MoveSlot(source.Index, target.Index);
        e.Handled = true;
    }

    /// <summary>键盘替代路径：编辑模式下 Ctrl+←/Ctrl+→ 移动当前实例的位置（键盘可达）。</summary>
    private void OnLayoutPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HomeViewModel { Switcher.IsEditMode: true } home
            || Keyboard.Modifiers != ModifierKeys.Control
            || e.Key is not (Key.Left or Key.Right)
            || home.Launcher.SelectedInstance is not { } selected)
        {
            return;
        }

        var index = -1;
        for (var i = 0; i < home.Switcher.Layout.Count; i++)
        {
            if (home.Switcher.Layout[i].Instance is { } instance
                && string.Equals(instance.Model.Id, selected.Model.Id, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var target = e.Key == Key.Left ? index - 1 : index + 1;
        if (target < 0 || target >= home.Switcher.Layout.Count)
        {
            return;
        }

        if (home.Switcher.MoveSlot(index, target))
        {
            e.Handled = true;
        }
    }

    private static HomeInstanceSlotViewModel? SlotAt(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: HomeInstanceSlotViewModel slot })
            {
                return slot;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
