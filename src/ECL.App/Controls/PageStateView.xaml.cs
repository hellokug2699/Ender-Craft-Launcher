// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ecl.App.Localization;

namespace Ecl.App.Controls;

/// <summary>页面四态（迁移规划 §5.3）：加载中 / 空 / 错误 / 不可用。</summary>
public enum PageState
{
    Loading = 0,
    Empty = 1,
    Error = 2,
    Unavailable = 3,
}

/// <summary>
/// 共享四态视图：状态标签、标题、说明与可选动作槽。
///
/// 默认文案来自本地化资源；页面可以覆盖标题与说明以给出具体原因与解除方式，
/// 但不得把“不可用”呈现成可用，也不得只靠颜色表达状态。
/// </summary>
public partial class PageStateView : UserControl
{
    private static readonly DependencyPropertyKey StateLabelPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(StateLabel),
            typeof(string),
            typeof(PageStateView),
            new PropertyMetadata(string.Empty));

    private static readonly DependencyPropertyKey DisplayTitlePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(DisplayTitle),
            typeof(string),
            typeof(PageStateView),
            new PropertyMetadata(string.Empty));

    private static readonly DependencyPropertyKey DisplayDescriptionPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(DisplayDescription),
            typeof(string),
            typeof(PageStateView),
            new PropertyMetadata(string.Empty));

    private static readonly DependencyPropertyKey DescriptionVisibilityPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(DescriptionVisibility),
            typeof(Visibility),
            typeof(PageStateView),
            new PropertyMetadata(Visibility.Collapsed));

    private static readonly DependencyPropertyKey ActionVisibilityPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ActionVisibility),
            typeof(Visibility),
            typeof(PageStateView),
            new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty StateLabelProperty = StateLabelPropertyKey.DependencyProperty;

    public static readonly DependencyProperty DisplayTitleProperty = DisplayTitlePropertyKey.DependencyProperty;

    public static readonly DependencyProperty DisplayDescriptionProperty = DisplayDescriptionPropertyKey.DependencyProperty;

    public static readonly DependencyProperty DescriptionVisibilityProperty = DescriptionVisibilityPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ActionVisibilityProperty = ActionVisibilityPropertyKey.DependencyProperty;

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(PageState),
        typeof(PageStateView),
        new PropertyMetadata(PageState.Loading, OnPresentationChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(PageStateView),
        new PropertyMetadata(null, OnPresentationChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(PageStateView),
        new PropertyMetadata(null, OnPresentationChanged));

    /// <summary>空态/错误态的可执行入口；通常是按钮，没有动作时留空。</summary>
    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent),
        typeof(object),
        typeof(PageStateView),
        new PropertyMetadata(null, OnActionChanged));

    public PageStateView()
    {
        InitializeComponent();
        UpdatePresentation();
    }

    public string StateLabel => (string)GetValue(StateLabelProperty);

    public string DisplayTitle => (string)GetValue(DisplayTitleProperty);

    public string DisplayDescription => (string)GetValue(DisplayDescriptionProperty);

    public Visibility DescriptionVisibility => (Visibility)GetValue(DescriptionVisibilityProperty);

    public Visibility ActionVisibility => (Visibility)GetValue(ActionVisibilityProperty);

    public PageState State
    {
        get => (PageState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>覆盖默认标题；为空时使用该状态的本地化默认标题。</summary>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>覆盖默认说明；应说明“为什么”和“怎样解除”。</summary>
    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    private static void OnPresentationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PageStateView)d).UpdatePresentation();

    private static void OnActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PageStateView)d).SetValue(
            ActionVisibilityPropertyKey,
            e.NewValue is null ? Visibility.Collapsed : Visibility.Visible);

    private void UpdatePresentation()
    {
        var key = State switch
        {
            PageState.Empty => "empty",
            PageState.Error => "error",
            PageState.Unavailable => "unavailable",
            _ => "loading",
        };

        SetValue(StateLabelPropertyKey, Localize($"page.state.{key}"));
        SetValue(DisplayTitlePropertyKey, Coalesce(Title, Localize($"page.state.{key}.title")));
        SetValue(DisplayDescriptionPropertyKey, Coalesce(Description, Localize($"page.state.{key}.description")));
        SetValue(
            DescriptionVisibilityPropertyKey,
            string.IsNullOrWhiteSpace(DisplayDescription) ? Visibility.Collapsed : Visibility.Visible);
        StateDot.Fill = StateDotBrush(State);
    }

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Localize(string key) => LocalizationProvider.Current?.Get(key) ?? key;

    private Brush StateDotBrush(PageState state)
    {
        var resourceKey = state switch
        {
            PageState.Loading => "AccentBrush",
            PageState.Error => "DangerBrush",
            PageState.Unavailable => "WarningBrush",
            _ => "TextMutedBrush",
        };

        return TryFindResource(resourceKey) as Brush ?? Brushes.Transparent;
    }
}
