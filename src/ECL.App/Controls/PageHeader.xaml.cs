// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;

namespace Ecl.App.Controls;

/// <summary>
/// 共享页面标题（迁移规划 §5.1、§5.11）：目的地页面用它显示标题与副标题，
/// 主操作放在标题行右侧。目标/工具工作区不重复大标题，因此不使用本控件。
///
/// 只做呈现：不读取业务状态、不发起命令。
/// </summary>
public partial class PageHeader : UserControl
{
    private static readonly DependencyPropertyKey SubtitleVisibilityPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(SubtitleVisibility),
            typeof(Visibility),
            typeof(PageHeader),
            new PropertyMetadata(Visibility.Collapsed));

    private static readonly DependencyPropertyKey ActionsVisibilityPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ActionsVisibility),
            typeof(Visibility),
            typeof(PageHeader),
            new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty SubtitleVisibilityProperty = SubtitleVisibilityPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ActionsVisibilityProperty = ActionsVisibilityPropertyKey.DependencyProperty;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(PageHeader),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle),
        typeof(string),
        typeof(PageHeader),
        new PropertyMetadata(null, OnSubtitleChanged));

    /// <summary>标题行右侧的操作区；通常是一个主按钮（PrimaryButton）或按钮组。</summary>
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions),
        typeof(object),
        typeof(PageHeader),
        new PropertyMetadata(null, OnActionsChanged));

    public PageHeader()
    {
        InitializeComponent();
    }

    public Visibility SubtitleVisibility => (Visibility)GetValue(SubtitleVisibilityProperty);

    public Visibility ActionsVisibility => (Visibility)GetValue(ActionsVisibilityProperty);

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PageHeader)d).SetValue(
            SubtitleVisibilityPropertyKey,
            string.IsNullOrWhiteSpace(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible);

    private static void OnActionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PageHeader)d).SetValue(
            ActionsVisibilityPropertyKey,
            e.NewValue is null ? Visibility.Collapsed : Visibility.Visible);
}
