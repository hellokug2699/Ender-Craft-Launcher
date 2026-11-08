// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Ecl.App.ViewModels.Settings;

namespace Ecl.App.Views;

/// <summary>
/// Java 管理页的拖动排序（java-management.md §3.2.1）：
/// 行最左侧的拖动标志启动拖动，列表按放下的位置重排全局候选优先级；
/// 行内“移除/卸载”按钮自己处理点击，拖动不会误触这两个操作。
/// 排序只改变候选偏好，兼容性判断仍由启动选择流程负责。
/// </summary>
public partial class SettingsJavaPageView : UserControl
{
    private Point _dragOrigin;
    private JavaManagementRowViewModel? _dragRow;

    public SettingsJavaPageView()
    {
        InitializeComponent();

        // 切换二级页时 DataContext 变化：把滚动位置复位到页面顶部，
        // 避免上一页的滚动偏移把工具区截断。
        DataContextChanged += (_, _) => PageScroll?.ScrollToTop();
    }

    private void OnRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragRow = null;
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // 行内按钮自己处理点击，不启动拖动。
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or CheckBox)
            {
                return;
            }

            if (node is ListBoxItem)
            {
                break;
            }
        }

        _dragOrigin = e.GetPosition(this);
        _dragRow = RowAt(list, e.GetPosition(list));
    }

    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null || e.LeftButton != MouseButtonState.Pressed || sender is not ListBox list)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var row = _dragRow;
        _dragRow = null;
        DragDrop.DoDragDrop(
            list,
            new DataObject(typeof(JavaManagementRowViewModel), row),
            DragDropEffects.Move);
    }

    private void OnTableDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(JavaManagementRowViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTableDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list ||
            e.Data.GetData(typeof(JavaManagementRowViewModel)) is not JavaManagementRowViewModel row ||
            DataContext is not SettingsJavaPageViewModel page)
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(list.InputHitTest(e.GetPosition(list)) as DependencyObject);
        var index = item is null
            ? list.Items.Count
            : list.ItemContainerGenerator.IndexFromContainer(item);
        page.MoveRow(row, index);
        e.Handled = true;
    }

    private static JavaManagementRowViewModel? RowAt(ListBox list, Point point) =>
        FindAncestor<ListBoxItem>(list.InputHitTest(point) as DependencyObject)?.DataContext
            as JavaManagementRowViewModel;

    private static T? FindAncestor<T>(DependencyObject? node)
        where T : DependencyObject
    {
        while (node is not null && node is not T)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        return node as T;
    }
}
