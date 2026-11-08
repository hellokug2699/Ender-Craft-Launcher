// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Threading;

namespace Ecl.App.Behaviors;

/// <summary>
/// 高亮设置项时把对应行滚入可视区域。只作用于表现层，不参与设置值或页面状态。
/// 用于设置搜索“定位到具体设置项”的交互（settings.md §3.1）。
/// </summary>
public static class HighlightScrollBehavior
{
    public static readonly DependencyProperty IsHighlightedProperty = DependencyProperty.RegisterAttached(
        "IsHighlighted",
        typeof(bool),
        typeof(HighlightScrollBehavior),
        new PropertyMetadata(false, OnIsHighlightedChanged));

    public static void SetIsHighlighted(DependencyObject element, bool value) =>
        element.SetValue(IsHighlightedProperty, value);

    public static bool GetIsHighlighted(DependencyObject element) =>
        (bool)element.GetValue(IsHighlightedProperty);

    private static void OnIsHighlightedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
        {
            return;
        }

        // 等待布局完成后再滚动，避免刚切换页面时命中尚未测量的行。
        element.Dispatcher.BeginInvoke(
            () => element.BringIntoView(),
            DispatcherPriority.Background);
    }
}
