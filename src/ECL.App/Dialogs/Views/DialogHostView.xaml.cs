// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ecl.App.Services;

namespace Ecl.App.Dialogs.Views;

/// <summary>
/// 弹窗宿主的视图职责：遮罩与弹窗动画、尺寸约束、焦点进入/恢复和 Escape 关闭。
/// 弹窗的创建、层级与结果由 <see cref="Ecl.App.Dialogs.DialogEngine"/> 负责。
/// </summary>
public partial class DialogHostView : UserControl
{
    private DialogLayerViewModel? _layer;
    private IInputElement? _focusBeforeDialog;

    public DialogHostView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => ApplySizeConstraints();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_layer is not null)
        {
            _layer.PropertyChanged -= OnLayerPropertyChanged;
        }

        _layer = DataContext as DialogLayerViewModel;
        if (_layer is not null)
        {
            _layer.PropertyChanged += OnLayerPropertyChanged;
        }

        UpdatePresentation();
    }

    private void OnLayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DialogLayerViewModel.Top) or nameof(DialogLayerViewModel.HasDialogs))
        {
            UpdatePresentation();
        }
    }

    private void UpdatePresentation()
    {
        var top = _layer?.Top;
        if (top is null)
        {
            // 关闭弹窗会先解除模态（背景重新启用）再移除弹窗层；等绑定完成后再恢复焦点，
            // 否则此刻原焦点元素仍处于禁用状态，会被误判为不可恢复。
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (_layer?.Top is null)
                    {
                        RestoreFocus();
                    }
                }));
            return;
        }

        // 只记录最外层弹窗打开前的焦点；嵌套弹窗关闭后回到父弹窗。
        if (_layer!.Entries.Count == 1)
        {
            _focusBeforeDialog = Keyboard.FocusedElement;
        }

        ApplySizeConstraints();
        AnimateIn();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(EnsureFocusInsideDialog));
    }

    private void ApplySizeConstraints()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // 弹窗尺寸受所属窗口约束；内容较多时由弹窗内部滚动，不裁切主页面。
        TopContent.MaxWidth = Math.Max(320, Math.Min(720, ActualWidth - 160));
        TopContent.MaxHeight = Math.Max(240, ActualHeight - 120);
    }

    private void AnimateIn()
    {
        if (MotionSettings.IsReducedMotion)
        {
            Scrim.Opacity = 1;
            TopContent.Opacity = 1;
            return;
        }

        var duration = new Duration(MotionSettings.Fast);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
        TopContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
    }

    private void EnsureFocusInsideDialog()
    {
        if (_layer?.Top is not { } top)
        {
            return;
        }

        if (Keyboard.FocusedElement is DependencyObject focused && IsInside(focused, TopContent))
        {
            return;
        }

        if (FindFocusable(TopContent) is { } target)
        {
            Keyboard.Focus(target);
        }
    }

    private void RestoreFocus()
    {
        if (_focusBeforeDialog is UIElement element && element.IsVisible && element.IsEnabled)
        {
            Keyboard.Focus(element);
        }

        _focusBeforeDialog = null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _layer?.Top is not { } top)
        {
            return;
        }

        if (top.RequestClose())
        {
            e.Handled = true;
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject root)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    private static IInputElement? FindFocusable(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is UIElement element
                && element.Focusable
                && element.IsVisible
                && element.IsEnabled
                && child is not ScrollViewer
                && child is Control { IsTabStop: true })
            {
                return element;
            }

            if (FindFocusable(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
