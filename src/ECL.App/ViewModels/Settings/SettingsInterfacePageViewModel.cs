// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 设置中心「界面偏好」页（页面规格 <c>settings-interface-preferences.md</c>）。
///
/// 页面只有四组偏好：字体与文字、界面缩放、语言、动效。
/// - 字体族 / 字号档位 / 语言 / 动效是元数据驱动的普通设置项（ADR 0002），
///   Boolean/Select 修改后即时保存；
/// - 界面缩放需要滑块，保存后再由 <see cref="IUserInterfacePreferences"/> 应用到窗口；
/// - 不提供主题色、主题包、浅色模式或逐控件字号/动画参数编辑（规格 §2/§8），
///   这些边界作为「固定边界」分组如实列出，不放不生效的控件。
///
/// 保存失败时回到最后一次有效值并显示真实原因，不把未落盘的状态留在界面上。
/// </summary>
public sealed partial class SettingsInterfacePageViewModel : ObservableObject, ISettingsPage
{
    private readonly ISettingsService _settings;
    private readonly ILocalizer _localizer;
    private readonly IDialogService _dialogs;

    private readonly OptionEditorViewModel _fontOption;
    private readonly OptionEditorViewModel _fontSizeOption;
    private readonly OptionEditorViewModel _scaleOption;
    private readonly OptionEditorViewModel _languageOption;
    private readonly OptionEditorViewModel _motionOption;

    private bool _suppressScaleWrite;

    public SettingsInterfacePageViewModel(
        ISettingsService settings,
        ILocalizer localizer,
        IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(dialogs);

        _settings = settings;
        _localizer = localizer;
        _dialogs = dialogs;

        _fontOption = CreateOption(UiTypography.FontFamilyKey);
        _fontSizeOption = CreateOption(UiTypography.FontSizeKey);
        _scaleOption = CreateOption(UiTypography.UiScaleKey);
        _languageOption = CreateOption("launcher.language");
        _motionOption = CreateOption(UiTypography.MotionKey);

        Options = [_fontOption, _fontSizeOption, _scaleOption, _languageOption, _motionOption];

        FontGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.interface.group.font"),
            [_fontOption, _fontSizeOption],
            localizer.Get("settings.page.interface.font.bundledNote"));
        LanguageGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.interface.group.language"),
            [_languageOption],
            localizer.Get("settings.page.interface.language.restartNote"));
        MotionGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.interface.group.motion"),
            [_motionOption],
            localizer.Get("settings.page.interface.motion.note"));

        Boundaries = new SettingsUnavailableSectionViewModel(
            localizer.Get("settings.page.interface.boundaries.title"),
            [
                Boundary("theme"),
                Boundary("fontSizePerControl"),
                Boundary("fontFile"),
                Boundary("displaySettings"),
                Boundary("gameLanguage"),
                Boundary("motionParameters"),
            ]);

        SearchEntries = Options
            .Select(option => new SettingsSearchEntry(
                SettingsPageKeys.Interface,
                Title,
                option.Key,
                option.DisplayName,
                option.Description))
            .ToArray();

        _suppressScaleWrite = true;
        try
        {
            _uiScalePercent = ReadUiScale();
        }
        finally
        {
            _suppressScaleWrite = false;
        }

        RefreshVisibility();
    }

    public string Key => SettingsPageKeys.Interface;

    public string Title => _localizer.Get("settings.nav.interface");

    public string Subtitle => _localizer.Get("settings.page.interface.subtitle");

    /// <summary>页面真实渲染的设置项；界面缩放的控件是滑块，但仍由同一元数据驱动。</summary>
    public IReadOnlyList<OptionEditorViewModel> Options { get; }

    public SettingsOptionGroupViewModel FontGroup { get; }

    public SettingsOptionGroupViewModel LanguageGroup { get; }

    public SettingsOptionGroupViewModel MotionGroup { get; }

    /// <summary>规格明确不提供的界面能力（主题色、逐控件字号、字体导入等）。</summary>
    public SettingsUnavailableSectionViewModel Boundaries { get; }

    public IReadOnlyList<SettingsSearchEntry> SearchEntries { get; }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>界面缩放百分比（80–150，步进 5）；修改即保存并立即应用。</summary>
    [ObservableProperty]
    private int _uiScalePercent;

    [ObservableProperty]
    private bool _isUiScaleHighlighted;

    public string UiScaleTitle => _localizer.Get("option.launcher.uiScale.name");

    public string UiScaleDescription => _localizer.Get("option.launcher.uiScale.desc");

    public string UiScaleValueText => _localizer.Get(
        "settings.page.interface.scale.value",
        UiScalePercent,
        _scaleOption.Unit);

    public string UiScaleNote => _localizer.Get("settings.page.interface.scale.note");

    public string UiScaleDefaultText => _localizer.Get(
        "settings.defaultValue",
        _localizer.Get(
            "settings.page.interface.scale.value",
            UiTypography.DefaultUiScalePercent,
            _scaleOption.Unit));

    public bool IsUiScaleDefault => UiScalePercent == UiTypography.DefaultUiScalePercent;

    public bool CanResetUiScale => !IsUiScaleDefault;

    /// <summary>当前实际使用的界面语言（设置可能只是“跟随系统”）。</summary>
    public string EffectiveLanguageText => _localizer.Get(
        "settings.page.interface.language.effective",
        _localizer.Culture.DisplayName);

    /// <summary>Windows 已要求减少动画时，即使 ECL 动画开关开启也不会播放动效。</summary>
    public bool HasSystemMotionLimit => !SystemParameters.ClientAreaAnimation;

    public string SystemMotionNote => _localizer.Get("settings.page.interface.motion.systemNote");

    /// <summary>进入页面时同步持久化值；不触发扫描或写入。</summary>
    public void Activate()
    {
        _languageOption.Reload();
        SetUiScale(ReadUiScale());
        OnPropertyChanged(nameof(EffectiveLanguageText));
        OnPropertyChanged(nameof(HasSystemMotionLimit));
    }

    public void ResetToDefaults()
    {
        var title = _localizer.Get("settings.reset.title");
        var message = _localizer.Get("settings.reset.message", Title);
        if (!_dialogs.Confirm(title, message, acknowledgementToken: null))
        {
            return;
        }

        var errors = new List<string>();
        foreach (var option in Options)
        {
            if (!_settings.TryClear(option.Key, null, out var error))
            {
                errors.Add($"{option.DisplayName}: {error}");
                continue;
            }

            option.Reload();
        }

        SetUiScale(ReadUiScale());
        RefreshVisibility();
        StatusMessage = errors.Count == 0
            ? _localizer.Get("settings.reset.done", Title)
            : _localizer.Get("settings.reset.partial", string.Join(';', errors));
    }

    public bool Highlight(string optionKey)
    {
        var target = Options.FirstOrDefault(option =>
            string.Equals(option.Key, optionKey, StringComparison.Ordinal));
        if (target is null)
        {
            return false;
        }

        foreach (var option in Options)
        {
            option.IsHighlighted = ReferenceEquals(option, target);
        }

        IsUiScaleHighlighted = string.Equals(optionKey, UiTypography.UiScaleKey, StringComparison.Ordinal);
        StatusMessage = _localizer.Get("settings.search.located", target.DisplayName);
        return true;
    }

    [RelayCommand]
    private void ResetUiScale()
    {
        // “恢复默认”只恢复界面缩放：清除键而不是写入 100，
        // 这样默认值以后调整时仍然生效，也不影响字体、语言或动效。
        if (!_settings.TryClear(UiTypography.UiScaleKey, null, out var error))
        {
            StatusMessage = _localizer.Get("settings.page.interface.scale.failed", error ?? string.Empty);
            return;
        }

        _scaleOption.Reload();
        SetUiScale(ReadUiScale());
        StatusMessage = string.Empty;
    }

    partial void OnUiScalePercentChanged(int value)
    {
        OnPropertyChanged(nameof(UiScaleValueText));
        OnPropertyChanged(nameof(IsUiScaleDefault));
        OnPropertyChanged(nameof(CanResetUiScale));

        if (_suppressScaleWrite)
        {
            return;
        }

        var normalized = UiTypography.ClampUiScale(value);
        if (normalized != value)
        {
            // 滑块本身是整档的，但外部输入（例如自动化/脚本）可能落在档位之间：
            // 先回到整档，再按整档保存。
            SetUiScale(normalized);
            PersistUiScale(normalized);
            return;
        }

        PersistUiScale(value);
    }

    private void PersistUiScale(int percent)
    {
        if (!_settings.TrySetRaw(
                UiTypography.UiScaleKey,
                percent.ToString(CultureInfo.InvariantCulture),
                null,
                out var error))
        {
            // 保存失败：恢复最后一次有效值并说明原因，不把未落盘的缩放留在界面上。
            StatusMessage = _localizer.Get("settings.page.interface.scale.failed", error ?? string.Empty);
            SetUiScale(ReadUiScale());
            return;
        }

        _scaleOption.Reload();
        StatusMessage = string.Empty;
    }

    private OptionEditorViewModel CreateOption(string key) => new(
        _settings.GetDefinition(key),
        _settings,
        _localizer,
        instanceId: null);

    private SettingsUnavailableItemViewModel Boundary(string name) => new(
        _localizer.Get($"settings.page.interface.boundary.{name}.title"),
        _localizer.Get($"settings.page.interface.boundary.{name}.reason"));

    private int ReadUiScale() => UiTypography.ClampUiScale(_settings.Get<int>(UiTypography.UiScaleKey));

    private void SetUiScale(int percent)
    {
        _suppressScaleWrite = true;
        try
        {
            UiScalePercent = percent;
        }
        finally
        {
            _suppressScaleWrite = false;
        }
    }

    private void RefreshVisibility()
    {
        foreach (var option in Options)
        {
            option.SetVisible(_settings.IsVisible(option.Definition));
        }
    }
}
