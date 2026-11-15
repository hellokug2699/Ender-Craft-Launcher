// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ecl.App.Controls;

/// <summary>
/// 安全中心与状态中心共用的顶部圆形状态区域（hints/ui/pages/security-center.md §3、
/// runtime-status.md §3）：白色实心圆 + 仅描边的盾牌，边框与盾牌颜色由 <see cref="StatusBrush"/> 决定；
/// 状态中心通过 <see cref="Glyph"/> 在盾牌内显示 ✓ / ? / X，安全中心留空。
///
/// 控件不判断页面状态，也不执行扫描或修复；按钮内容与命令全部由页面提供。
/// </summary>
public partial class SecurityStatusCircle : UserControl
{
    public static readonly DependencyProperty StatusBrushProperty = DependencyProperty.Register(
        nameof(StatusBrush),
        typeof(Brush),
        typeof(SecurityStatusCircle),
        new PropertyMetadata(Brushes.Gray));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(SecurityStatusCircle),
        new PropertyMetadata(string.Empty, OnGlyphChanged));

    public static readonly DependencyPropertyKey GlyphVisibilityPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(GlyphVisibility),
        typeof(Visibility),
        typeof(SecurityStatusCircle),
        new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty GlyphVisibilityProperty = GlyphVisibilityPropertyKey.DependencyProperty;

    public static readonly DependencyProperty PrimaryTextProperty = DependencyProperty.Register(
        nameof(PrimaryText),
        typeof(string),
        typeof(SecurityStatusCircle),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PrimaryCommandProperty = DependencyProperty.Register(
        nameof(PrimaryCommand),
        typeof(ICommand),
        typeof(SecurityStatusCircle),
        new PropertyMetadata(null));

    public static readonly DependencyProperty PrimaryEnabledProperty = DependencyProperty.Register(
        nameof(PrimaryEnabled),
        typeof(bool),
        typeof(SecurityStatusCircle),
        new PropertyMetadata(true));

    public static readonly DependencyProperty PrimaryVisibilityProperty = DependencyProperty.Register(
        nameof(PrimaryVisibility),
        typeof(Visibility),
        typeof(SecurityStatusCircle),
        new PropertyMetadata(Visibility.Visible));

    public static readonly DependencyProperty SecondaryTextProperty = DependencyProperty.Register(
        nameof(SecondaryText),
        typeof(string),
        typeof(SecurityStatusCircle),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SecondaryCommandProperty = DependencyProperty.Register(
        nameof(SecondaryCommand),
        typeof(ICommand),
        typeof(SecurityStatusCircle),
        new PropertyMetadata(null));

    public static readonly DependencyProperty SecondaryVisibilityProperty = DependencyProperty.Register(
        nameof(SecondaryVisibility),
        typeof(Visibility),
        typeof(SecurityStatusCircle),
        new PropertyMetadata(Visibility.Collapsed));

    public SecurityStatusCircle() => InitializeComponent();

    public Brush StatusBrush
    {
        get => (Brush)GetValue(StatusBrushProperty);
        set => SetValue(StatusBrushProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public Visibility GlyphVisibility => (Visibility)GetValue(GlyphVisibilityProperty);

    public string PrimaryText
    {
        get => (string)GetValue(PrimaryTextProperty);
        set => SetValue(PrimaryTextProperty, value);
    }

    public ICommand? PrimaryCommand
    {
        get => (ICommand?)GetValue(PrimaryCommandProperty);
        set => SetValue(PrimaryCommandProperty, value);
    }

    public bool PrimaryEnabled
    {
        get => (bool)GetValue(PrimaryEnabledProperty);
        set => SetValue(PrimaryEnabledProperty, value);
    }

    public Visibility PrimaryVisibility
    {
        get => (Visibility)GetValue(PrimaryVisibilityProperty);
        set => SetValue(PrimaryVisibilityProperty, value);
    }

    public string SecondaryText
    {
        get => (string)GetValue(SecondaryTextProperty);
        set => SetValue(SecondaryTextProperty, value);
    }

    public ICommand? SecondaryCommand
    {
        get => (ICommand?)GetValue(SecondaryCommandProperty);
        set => SetValue(SecondaryCommandProperty, value);
    }

    public Visibility SecondaryVisibility
    {
        get => (Visibility)GetValue(SecondaryVisibilityProperty);
        set => SetValue(SecondaryVisibilityProperty, value);
    }

    private static void OnGlyphChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var circle = (SecurityStatusCircle)dependencyObject;
        circle.SetValue(
            GlyphVisibilityPropertyKey,
            string.IsNullOrWhiteSpace(args.NewValue as string) ? Visibility.Collapsed : Visibility.Visible);
    }
}
