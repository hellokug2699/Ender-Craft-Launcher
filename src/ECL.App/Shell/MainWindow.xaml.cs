// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ecl.App.Services;

namespace Ecl.App.Shell;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private ShellViewModel ViewModel => (ShellViewModel)DataContext;

    /// <summary>弹窗宿主附加到主窗口后，统一弹窗才可用；在此之前调用方使用兼容路径。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.Dialogs.AttachHost();

    /// <summary>
    /// 窗口关闭时先关停弹窗层：打开的弹窗按 Closed 完成，等待中的嵌套消息循环退出，
    /// 然后才解除宿主关联，避免关闭流程被未完成的弹窗任务阻塞。
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        ViewModel.Dialogs.Shutdown();
        ViewModel.Dialogs.DetachHost();
    }

    private void OnPageTargetUpdated(object? sender, DataTransferEventArgs e)
    {
        if (PageHost.Content is null || !MotionSettings.TryBeginPageTransition(PageHost))
        {
            PageHost.Opacity = 1;
            PageHost.RenderTransform = Transform.Identity;
            return;
        }

        // 只做表现层转场：内容与 ViewModel 已由工作区切换决定，动画不参与生命周期。
        PageHost.Opacity = 0;
        var translate = new TranslateTransform(0, 6);
        PageHost.RenderTransform = translate;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(MotionSettings.Normal)) { EasingFunction = easing });
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, new Duration(MotionSettings.Normal)) { EasingFunction = easing });
    }

    /// <summary>
    /// 全局拖拽导入（instances.md §5.2）：接收能力不限于实例页面，
    /// 主动选择与拖拽共用同一个解析流程。
    /// </summary>
    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (DataContext is not ShellViewModel viewModel
            || !e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        e.Handled = true;
        _ = viewModel.HandleDroppedFilesAsync(files);
    }

    /// <summary>Ctrl+1…9 直接激活对应导航目的地；其余快捷键由 XAML InputBindings 声明。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || DataContext is not ShellViewModel viewModel)
        {
            return;
        }

        var index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            Key.D8 or Key.NumPad8 => 7,
            Key.D9 or Key.NumPad9 => 8,
            _ => -1,
        };

        if (index >= 0 && index < viewModel.NavigationItems.Count)
        {
            viewModel.SelectedNavigationItem = viewModel.NavigationItems[index];
            e.Handled = true;
        }
    }
}
