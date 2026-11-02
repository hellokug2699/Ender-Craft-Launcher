// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ecl.App.Services;

namespace Ecl.App.Behaviors;

/// <summary>
/// 按钮按下的轻量缩放反馈。动效只改变 RenderTransform，不等待动画完成才执行命令；
/// Windows 减少动画偏好开启时立即跳到终态。键盘 Space/Enter 与鼠标共用同一状态。
/// </summary>
public static class InteractionMotion
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(MotionState),
        typeof(InteractionMotion),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(InteractionMotion),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Control control)
        {
            return;
        }

        if (args.NewValue is true)
        {
            if (control.GetValue(StateProperty) is MotionState)
            {
                return;
            }

            if (control.RenderTransform is not null &&
                control.RenderTransform is not ScaleTransform { ScaleX: 1, ScaleY: 1 } &&
                control.RenderTransform is not MatrixTransform { Matrix: { IsIdentity: true } })
            {
                // 页面自定义了 RenderTransform 时不要覆盖其布局/动画语义。
                return;
            }

            var scale = new ScaleTransform(1, 1);
            control.RenderTransform = scale;
            control.RenderTransformOrigin = new Point(0.5, 0.5);
            var state = new MotionState(control, scale);
            control.SetValue(StateProperty, state);
            state.Attach();
        }
        else if (control.GetValue(StateProperty) is MotionState state)
        {
            state.Detach();
            control.ClearValue(StateProperty);
        }
    }

    private sealed class MotionState(Control control, ScaleTransform scale)
    {
        private bool _pressed;

        public void Attach()
        {
            control.PreviewMouseLeftButtonDown += OnMouseDown;
            control.PreviewMouseLeftButtonUp += OnMouseUp;
            control.MouseLeave += OnMouseLeave;
            control.PreviewKeyDown += OnKeyDown;
            control.PreviewKeyUp += OnKeyUp;
            control.LostKeyboardFocus += OnLostFocus;
        }

        public void Detach()
        {
            control.PreviewMouseLeftButtonDown -= OnMouseDown;
            control.PreviewMouseLeftButtonUp -= OnMouseUp;
            control.MouseLeave -= OnMouseLeave;
            control.PreviewKeyDown -= OnKeyDown;
            control.PreviewKeyUp -= OnKeyUp;
            control.LostKeyboardFocus -= OnLostFocus;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs args)
        {
            if (args.ChangedButton == MouseButton.Left)
            {
                Press();
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs args)
        {
            if (args.ChangedButton == MouseButton.Left)
            {
                Release();
            }
        }

        private void OnMouseLeave(object sender, MouseEventArgs args) => Release();

        private void OnKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key is Key.Space or Key.Enter)
            {
                Press();
            }
        }

        private void OnKeyUp(object sender, KeyEventArgs args)
        {
            if (args.Key is Key.Space or Key.Enter)
            {
                Release();
            }
        }

        private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs args) => Release();

        private void Press()
        {
            if (_pressed || !control.IsEnabled)
            {
                return;
            }

            _pressed = true;
            Animate(0.985, MotionSettings.Instant);
        }

        private void Release()
        {
            if (!_pressed)
            {
                return;
            }

            _pressed = false;
            Animate(1, MotionSettings.Fast);
        }

        private void Animate(double target, TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = target;
                scale.ScaleY = target;
                return;
            }

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var animation = new DoubleAnimation(target, new Duration(duration))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd,
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
