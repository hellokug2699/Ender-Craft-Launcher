// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ecl.App.Workspace.ViewModels;

namespace Ecl.App.Views;

/// <summary>
/// 截图页视图代码只处理纯交互桥接：缩略图按需加载、行点击打开预览、预览覆盖层焦点与
/// 键盘进入。截图读取、删除与状态判断全部在 ViewModel/领域服务中。
/// </summary>
public partial class InstanceScreenshotsView : UserControl
{
    public InstanceScreenshotsView()
    {
        InitializeComponent();
    }

    private async void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScreenshotItemViewModel item } &&
            DataContext is ScreenshotPageViewModel page)
        {
            await page.EnsureThumbnailAsync(item);
        }
    }

    private void ScreenshotRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 复选框点击属于选择操作，不应同时打开预览。
        if (e.OriginalSource is DependencyObject source && FindAncestor<CheckBox>(source) is not null)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: ScreenshotItemViewModel item } &&
            DataContext is ScreenshotPageViewModel page)
        {
            page.OpenPreviewCommand.Execute(item);
        }
    }

    private void ScreenshotList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ListBox { SelectedItem: ScreenshotItemViewModel item })
        {
            return;
        }

        if (DataContext is ScreenshotPageViewModel page)
        {
            page.OpenPreviewCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void PreviewOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            PreviewOverlay.Focus();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return source as T;
    }
}
