// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ecl.App.Services;
using Ecl.App.ViewModels;

namespace Ecl.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPageTargetUpdated(object? sender, DataTransferEventArgs e)
    {
        if (PageHost.Content is null || !MotionSettings.TryBeginPageTransition(PageHost))
        {
            PageHost.Opacity = 1;
            PageHost.RenderTransform = Transform.Identity;
            return;
        }

        // 只做表现层转场：内容与 ViewModel 已由导航逻辑切换，动画不参与生命周期。
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            _ => -1,
        };

        if (index >= 0 && index < viewModel.NavItems.Count)
        {
            viewModel.SelectedNavItem = viewModel.NavItems[index];
            e.Handled = true;
        }
    }
}
