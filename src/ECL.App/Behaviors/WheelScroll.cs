// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Ecl.App.Behaviors;

/// <summary>
/// WPF 的 ListBox / 内层 ScrollViewer 会吞掉鼠标滚轮：即使它没有可滚动内容，
/// 外层页面的 ScrollViewer 也收不到事件。把本行为附加到页面根 ScrollViewer 后，
/// 滚轮会优先交给指针下最内层可滚动的 ScrollViewer；当它已到边界或不可滚动时，
/// 再把事件转给页面根 ScrollViewer。
/// </summary>
public static class WheelScroll
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(WheelScroll),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ScrollViewer outer)
        {
            return;
        }

        outer.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (args.NewValue is true)
        {
            outer.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (sender is not ScrollViewer outer || args.Handled)
        {
            return;
        }

        var nested = FindNestedScrollViewer(args.OriginalSource as DependencyObject, outer);
        if (nested is null || CanNestedScroll(nested, args.Delta))
        {
            return;
        }

        Scroll(outer, args.Delta);
        args.Handled = true;
    }

    private static bool CanNestedScroll(ScrollViewer nested, int delta)
    {
        if (nested.ScrollableHeight <= 0)
        {
            return false;
        }

        // 留 1 像素余量，避免内层已经到底但差一点时把滚轮吞掉。
        return delta > 0
            ? nested.VerticalOffset > 1
            : nested.VerticalOffset < nested.ScrollableHeight - 1;
    }

    private static void Scroll(ScrollViewer outer, int delta)
    {
        var lines = SystemParameters.WheelScrollLines;
        if (lines < 0)
        {
            if (delta > 0)
            {
                outer.PageUp();
            }
            else
            {
                outer.PageDown();
            }

            return;
        }

        for (var index = 0; index < Math.Max(1, lines); index++)
        {
            if (delta > 0)
            {
                outer.LineUp();
            }
            else
            {
                outer.LineDown();
            }
        }
    }

    private static ScrollViewer? FindNestedScrollViewer(DependencyObject? source, ScrollViewer outer)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ScrollViewer viewer)
            {
                return ReferenceEquals(viewer, outer) ? null : viewer;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject source) => source switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(source),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(source),
    };
}
