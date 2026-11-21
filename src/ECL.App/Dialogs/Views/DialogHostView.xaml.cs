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
///
/// 动效契约（交接 §3.3 / §3.4）：
/// - 进入：遮罩淡入 + 卡片淡入、轻微上移与缩放（<see cref="DialogMotion"/>）；
/// - 退出：卡片先完成短促退出过渡，然后才释放遮罩与宿主可见性；
/// - 层叠切换：旧卡片在 <c>ExitContent</c> 上交叉淡出，新卡片在 <c>TopContent</c> 淡入；
/// - 减少动画偏好：所有时长归零，直接进入终态，但状态、焦点与遮罩语义不变；
/// - 动画回调只处理表现层清理，并通过 <c>_version</c> 丢弃过期回调，
///   业务结果、模态与焦点恢复都不依赖动画完成。
/// </summary>
public partial class DialogHostView : UserControl
{
    private readonly ScaleTransform _topScale = new(1, 1);
    private readonly TranslateTransform _topOffset = new();
    private readonly ScaleTransform _exitScale = new(1, 1);
    private readonly TranslateTransform _exitOffset = new();

    private DialogLayerViewModel? _layer;
    private IInputElement? _focusBeforeDialog;
    private DialogEntryViewModel? _presented;
    private int _version;

    public DialogHostView()
    {
        InitializeComponent();

        TopContent.RenderTransformOrigin = new Point(0.5, 0.5);
        TopContent.RenderTransform = new TransformGroup { Children = { _topScale, _topOffset } };
        ExitContent.RenderTransformOrigin = new Point(0.5, 0.5);
        ExitContent.RenderTransform = new TransformGroup { Children = { _exitScale, _exitOffset } };

        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => ApplySizeConstraints();
        PreviewKeyDown += OnPreviewKeyDown;
        Unloaded += OnUnloaded;
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
        if (e.PropertyName is nameof(DialogLayerViewModel.Top)
            or nameof(DialogLayerViewModel.HasDialogs)
            or nameof(DialogLayerViewModel.HasModal))
        {
            UpdatePresentation();
        }
    }

    private void UpdatePresentation()
    {
        var top = _layer?.Top;
        if (ReferenceEquals(top, _presented))
        {
            // 只有模态状态变化（例如关闭模态弹窗后仍有非模态弹窗）时，遮罩需要跟随。
            UpdateScrim(animate: true, DialogMotion.ExitDuration, _version);
            return;
        }

        PresentTransition(top);
    }

    /// <summary>
    /// 弹窗栈变化的一次性呈现：先复位上一轮动画，再按“打开 / 关闭 / 层叠切换”三种路径
    /// 设置内容与动画。所有完成回调都校验 <see cref="_version"/>，过期回调直接丢弃。
    /// </summary>
    private void PresentTransition(DialogEntryViewModel? top)
    {
        var previous = _presented;
        _presented = top;
        var version = ++_version;

        ResetAnimations();

        if (top is not null)
        {
            Root.Visibility = Visibility.Visible;
            TopContent.Content = top;

            if (previous is not null)
            {
                // 层叠切换：旧卡片移到退出层，与新卡片同时做交叉过渡。
                ExitContent.Content = previous;
                ExitContent.Visibility = Visibility.Visible;
                ExitContent.Opacity = 1;
            }
            else if (_layer!.Entries.Count == 1)
            {
                // 只记录最外层弹窗打开前的焦点；嵌套弹窗关闭后回到父弹窗。
                _focusBeforeDialog = Keyboard.FocusedElement;
            }

            ApplySizeConstraints();
            AnimateEnter(version);
        }

        if (previous is not null)
        {
            AnimateExit(previous, top is not null, version);
        }

        UpdateScrim(animate: true, top is null ? DialogMotion.ExitDuration : DialogMotion.EnterDuration, version);

        if (top is not null)
        {
            var entry = top;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (version == _version)
                {
                    EnsureFocusInsideDialog(entry);
                }
            }));
        }
    }

    private void ApplySizeConstraints()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // 弹窗尺寸受所属窗口约束；内容较多时由弹窗内部滚动，不裁切主页面。
        var maxWidth = Math.Max(320, Math.Min(720, ActualWidth - 160));
        var maxHeight = Math.Max(240, ActualHeight - 120);
        TopContent.MaxWidth = maxWidth;
        TopContent.MaxHeight = maxHeight;
        ExitContent.MaxWidth = maxWidth;
        ExitContent.MaxHeight = maxHeight;
    }

    private void AnimateEnter(int version)
    {
        var duration = DialogMotion.EnterDuration;
        if (duration <= TimeSpan.Zero)
        {
            TopContent.Opacity = 1;
            return;
        }

        var easing = DialogMotion.CreateEnterEase();
        var fade = new DoubleAnimation(1, new Duration(duration)) { EasingFunction = easing };
        TopContent.Opacity = 0;
        TopContent.BeginAnimation(OpacityProperty, fade);

        _topScale.ScaleX = DialogMotion.EnterScale;
        _topScale.ScaleY = DialogMotion.EnterScale;
        _topOffset.Y = DialogMotion.EnterOffset;
        var scale = new DoubleAnimation(1, new Duration(duration)) { EasingFunction = easing };
        _topScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        _topScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        _topOffset.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, new Duration(duration)) { EasingFunction = easing });
    }

    /// <summary>
    /// 退出动画：<paramref name="stacked"/> 为 true 时驱动退出层的旧卡片（层叠切换），
    /// 否则驱动当前卡片（关闭到空）。关闭路径在淡出完成后才清空内容、收起遮罩与宿主。
    /// </summary>
    private void AnimateExit(DialogEntryViewModel previous, bool stacked, int version)
    {
        var target = stacked ? ExitContent : TopContent;
        var scale = stacked ? _exitScale : _topScale;
        var offset = stacked ? _exitOffset : _topOffset;
        var duration = stacked ? DialogMotion.SwitchDuration : DialogMotion.ExitDuration;

        void Finish()
        {
            if (version != _version)
            {
                return;
            }

            if (stacked)
            {
                ExitContent.Content = null;
                ExitContent.Visibility = Visibility.Collapsed;
                ExitContent.Opacity = 1;
                _exitScale.ScaleX = _exitScale.ScaleY = 1;
                _exitOffset.Y = 0;
                return;
            }

            // 关闭：先完成动画，再释放宿主状态与焦点。
            TopContent.Content = null;
            TopContent.Opacity = 1;
            _topScale.ScaleX = _topScale.ScaleY = 1;
            _topOffset.Y = 0;
            Scrim.BeginAnimation(OpacityProperty, null);
            Scrim.Opacity = 0;
            Scrim.Visibility = Visibility.Collapsed;
            Root.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                // 若在恢复排队的同一帧内又打开了新弹窗，焦点应留给新弹窗。
                if (version == _version)
                {
                    RestoreFocus();
                }
            }));
        }

        if (duration <= TimeSpan.Zero)
        {
            target.Opacity = 0;
            Finish();
            return;
        }

        var easing = DialogMotion.CreateExitEase();
        var fade = new DoubleAnimation(0, new Duration(duration)) { EasingFunction = easing };
        fade.Completed += (_, _) => Finish();
        target.BeginAnimation(OpacityProperty, fade);

        var scaleAnimation = new DoubleAnimation(DialogMotion.ExitScale, new Duration(duration)) { EasingFunction = easing };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
        offset.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(DialogMotion.ExitOffset, new Duration(duration)) { EasingFunction = easing });
    }

    /// <summary>
    /// 遮罩跟随模态状态：有模态时淡入并保持，无模态或关闭过程中淡出；
    /// 淡出完成后才隐藏，避免遮罩先消失造成闪烁。
    /// </summary>
    private void UpdateScrim(bool animate, TimeSpan duration, int version)
    {
        var modal = _layer?.HasModal == true;
        var target = modal ? 1d : 0d;

        if (modal)
        {
            Scrim.Visibility = Visibility.Visible;
        }

        if (!animate || duration <= TimeSpan.Zero)
        {
            Scrim.BeginAnimation(OpacityProperty, null);
            Scrim.Opacity = target;
            if (!modal)
            {
                Scrim.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var from = Scrim.Opacity;
        if (Math.Abs(from - target) < 0.001)
        {
            if (!modal)
            {
                Scrim.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var easing = modal ? DialogMotion.CreateEnterEase() : DialogMotion.CreateExitEase();
        var animation = new DoubleAnimation(from, target, new Duration(duration)) { EasingFunction = easing };
        animation.Completed += (_, _) =>
        {
            if (version != _version || modal)
            {
                return;
            }

            Scrim.Opacity = 0;
            Scrim.Visibility = Visibility.Collapsed;
        };
        Scrim.BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>复位所有表现层状态；任何进行中的动画都被取消，避免与新一轮呈现叠加。</summary>
    private void ResetAnimations()
    {
        TopContent.BeginAnimation(OpacityProperty, null);
        ExitContent.BeginAnimation(OpacityProperty, null);
        Scrim.BeginAnimation(OpacityProperty, null);

        _topScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _topScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _topOffset.BeginAnimation(TranslateTransform.YProperty, null);
        _exitScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _exitScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _exitOffset.BeginAnimation(TranslateTransform.YProperty, null);

        _topScale.ScaleX = _topScale.ScaleY = 1;
        _topOffset.Y = 0;
        _exitScale.ScaleX = _exitScale.ScaleY = 1;
        _exitOffset.Y = 0;

        TopContent.Opacity = 1;
        ExitContent.Opacity = 1;
        ExitContent.Content = null;
        ExitContent.Visibility = Visibility.Collapsed;
        Scrim.Opacity = 0;
    }

    private void EnsureFocusInsideDialog(DialogEntryViewModel entry)
    {
        if (Keyboard.FocusedElement is DependencyObject focused && IsInside(focused, TopContent))
        {
            return;
        }

        var target = entry.InitialFocus switch
        {
            DialogInitialFocus.Primary => FindNamed(TopContent, "PrimaryButton"),
            DialogInitialFocus.Secondary => FindNamed(TopContent, "SecondaryButton"),
            DialogInitialFocus.Cancel => FindNamed(TopContent, "CancelButton"),
            _ => null,
        };

        target ??= FindFocusable(TopContent);
        if (target is not null)
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

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 窗口关闭或宿主卸载：取消动画并清理表现层，避免残留遮罩、焦点锁定与退出层内容。
        _version++;
        ResetAnimations();
        Scrim.Visibility = Visibility.Collapsed;
        Root.Visibility = Visibility.Collapsed;
        TopContent.Content = null;
        _presented = null;
        _focusBeforeDialog = null;
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

    private static IInputElement? FindNamed(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement { IsVisible: true, IsEnabled: true } element
                && string.Equals(element.Name, name, StringComparison.Ordinal)
                && element.Focusable)
            {
                return element;
            }

            if (FindNamed(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
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
