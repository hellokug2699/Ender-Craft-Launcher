// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Ecl.App.Workspace.ViewModels;

namespace Ecl.App.Views;

/// <summary>
/// 资源包/光影包页面的拖动交互（resource-library-template.md §4）：
/// 同表内拖动调整顺序，跨表拖动启用或禁用。拖动只改页面工作副本，
/// 写盘仍由「应用」触发；按钮上的启用/禁用提供等价的键盘可达操作。
/// </summary>
public partial class InstanceContentPacksView : UserControl
{
    private Point _dragOrigin;
    private ContentPackRowViewModel? _dragRow;

    public InstanceContentPacksView()
    {
        InitializeComponent();
    }

    private void OnRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragRow = null;
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // 行内按钮（启用/禁用、删除）自己处理点击，不启动拖动。
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
        DragDrop.DoDragDrop(list, new DataObject(typeof(ContentPackRowViewModel), row), DragDropEffects.Move);
    }

    private void OnTableDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ContentPackRowViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTableDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list ||
            e.Data.GetData(typeof(ContentPackRowViewModel)) is not ContentPackRowViewModel row ||
            DataContext is not ContentPackPageViewModel page)
        {
            return;
        }

        var targetEnabled = IsEnabledTable(list);
        var index = DropIndex(list, e.GetPosition(list));
        if (row.IsEnabled == targetEnabled)
        {
            page.MoveWithin(row, targetEnabled, index);
        }
        else
        {
            page.MovePack(row, targetEnabled, index);
        }

        e.Handled = true;
    }

    private static bool IsEnabledTable(ListBox list) =>
        string.Equals(list.Tag as string, "enabled", StringComparison.Ordinal);

    private static ContentPackRowViewModel? RowAt(ListBox list, Point point) =>
        FindAncestor<ListBoxItem>(list.InputHitTest(point) as DependencyObject)?.DataContext
            as ContentPackRowViewModel;

    private static int DropIndex(ListBox list, Point point)
    {
        var item = FindAncestor<ListBoxItem>(list.InputHitTest(point) as DependencyObject);
        return item is null ? list.Items.Count : list.ItemContainerGenerator.IndexFromContainer(item);
    }

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
