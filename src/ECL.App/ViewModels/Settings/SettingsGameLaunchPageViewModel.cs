// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Minecraft.Launch;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 设置中心「游戏与启动」页（settings-game-launch.md）。
///
/// 四个分组：
/// - 内存分配：全局策略（自动/手动，D-03）与手动数值；自动结果由启动服务计算，
///   页面只显示真实系统内存与领域结果，不自行实现第二套算法；
/// - JVM 参数：全局默认参数（实例专属参数优先）；
/// - 游戏窗口：宽度与高度作为一组校验并保存，另含默认全屏；
/// - 启动行为：ECL 既有启动选项；不提供跳过启动检查或忽略错误的开关。
///
/// 规格明确不提供的能力以「固定边界」分组说明；不放置不生效的控件。
/// </summary>
public sealed partial class SettingsGameLaunchPageViewModel : ObservableObject, ISettingsPage
{
    private const string WindowWidthKey = "game.windowWidth";
    private const string WindowHeightKey = "game.windowHeight";

    private readonly ISettingsService _settings;
    private readonly ILocalizer _localizer;
    private readonly IDialogService _dialogs;
    private readonly IMemoryAllocationService _memoryAllocation;

    private readonly OptionEditorViewModel _policyOption;
    private readonly OptionEditorViewModel _manualMemoryOption;
    private readonly OptionEditorViewModel _jvmOption;
    private readonly OptionEditorViewModel _widthOption;
    private readonly OptionEditorViewModel _heightOption;
    private readonly OptionEditorViewModel _fullscreenOption;
    private readonly OptionEditorViewModel _consoleOption;
    private readonly OptionEditorViewModel _gameArgumentsOption;

    public SettingsGameLaunchPageViewModel(
        ISettingsService settings,
        ILocalizer localizer,
        IDialogService dialogs,
        IMemoryAllocationService memoryAllocation)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(memoryAllocation);

        _settings = settings;
        _localizer = localizer;
        _dialogs = dialogs;
        _memoryAllocation = memoryAllocation;

        _policyOption = CreateOption(MemoryAllocationPolicy.PolicyKey);
        _manualMemoryOption = CreateOption(MemoryAllocationPolicy.ManualValueKey);
        _jvmOption = CreateOption("java.extraArguments");
        _widthOption = CreateOption(WindowWidthKey);
        _heightOption = CreateOption(WindowHeightKey);
        _fullscreenOption = CreateOption("game.fullscreen");
        _consoleOption = CreateOption("game.showConsole");
        _gameArgumentsOption = CreateOption("game.extraArguments");

        Options =
        [
            _policyOption,
            _manualMemoryOption,
            _jvmOption,
            _widthOption,
            _heightOption,
            _fullscreenOption,
            _consoleOption,
            _gameArgumentsOption,
        ];

        MemoryGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.game.group.memory"),
            [_policyOption, _manualMemoryOption],
            localizer.Get("settings.page.game.memory.groupNote"));

        JvmGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.game.group.jvm"),
            [_jvmOption],
            localizer.Get("settings.page.game.jvm.note"));

        LaunchGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.game.group.launch"),
            [_consoleOption, _gameArgumentsOption],
            localizer.Get("settings.page.game.launch.note"));

        Boundaries = new SettingsUnavailableSectionViewModel(
            localizer.Get("settings.page.game.boundaries.title"),
            [
                Boundary("skipChecks"),
                Boundary("eclWindow"),
                Boundary("systemDisplay"),
                Boundary("runningGame"),
            ]);

        SearchEntries = Options
            .Select(option => new SettingsSearchEntry(
                SettingsPageKeys.GameLaunch,
                Title,
                option.Key,
                option.DisplayName,
                option.Description))
            .ToArray();

        foreach (var option in new[] { _policyOption, _manualMemoryOption })
        {
            option.ValueChanged += (_, _) =>
            {
                RefreshVisibility();
                RefreshMemoryInfo();
            };
        }

        WindowWidthText = _widthOption.TextValue;
        WindowHeightText = _heightOption.TextValue;

        RefreshVisibility();
        RefreshMemoryInfo();
        NotifyWindowState();
    }

    public string Key => SettingsPageKeys.GameLaunch;

    public string Title => _localizer.Get("settings.nav.game");

    public string Subtitle => _localizer.Get("settings.page.game.subtitle");

    /// <summary>页面真实渲染的设置项；供设置搜索与恢复默认值使用。</summary>
    public IReadOnlyList<OptionEditorViewModel> Options { get; }

    public SettingsOptionGroupViewModel MemoryGroup { get; }

    public SettingsOptionGroupViewModel JvmGroup { get; }

    public SettingsOptionGroupViewModel LaunchGroup { get; }

    public SettingsUnavailableSectionViewModel Boundaries { get; }

    public IReadOnlyList<SettingsSearchEntry> SearchEntries { get; }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // ---- 内存分配（只呈现领域结果） ----

    public string MemoryInfoTitle => _localizer.Get("settings.page.game.memory.infoTitle");

    public string MemoryTotalLabel => _localizer.Get("settings.page.game.memory.total");

    public string MemoryAvailableLabel => _localizer.Get("settings.page.game.memory.available");

    [ObservableProperty]
    private string _memoryTotalText = string.Empty;

    [ObservableProperty]
    private string _memoryAvailableText = string.Empty;

    [ObservableProperty]
    private string _memoryAllocationText = string.Empty;

    [ObservableProperty]
    private string _memoryWarning = string.Empty;

    public bool HasMemoryWarning => !string.IsNullOrEmpty(MemoryWarning);

    /// <summary>自动算法说明：不使用物理内存总量固定比例，也不虚构游戏版本的规则。</summary>
    public string MemoryAlgorithmNote => _localizer.Get("settings.page.game.memory.algorithmNote");

    /// <summary>实例级内存覆盖优先于本页默认值。</summary>
    public string MemoryOverrideNote => _localizer.Get("settings.page.game.memory.overrideNote");

    [ObservableProperty]
    private bool _isMemoryHighlighted;

    // ---- 游戏窗口（宽度/高度联合校验，另有默认全屏） ----

    public string WindowTitle => _localizer.Get("settings.page.game.group.window");

    public string WindowWidthLabel => _localizer.Get("option.game.windowWidth.name");

    public string WindowHeightLabel => _localizer.Get("option.game.windowHeight.name");

    public string WindowUnit => _localizer.Get("unit.px");

    /// <summary>
    /// 窗口宽度/高度在页面上是联合编辑状态：文本直接写入对应的元数据编辑器，
    /// 保存时由页面一起校验、一起写入，不提供单边保存。
    /// </summary>
    public string WindowWidthText
    {
        get => _widthOption.TextValue;
        set
        {
            if (string.Equals(_widthOption.TextValue, value, StringComparison.Ordinal))
            {
                return;
            }

            _widthOption.TextValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanApplyWindow));
        }
    }

    public string WindowHeightText
    {
        get => _heightOption.TextValue;
        set
        {
            if (string.Equals(_heightOption.TextValue, value, StringComparison.Ordinal))
            {
                return;
            }

            _heightOption.TextValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanApplyWindow));
        }
    }

    [ObservableProperty]
    private string _windowError = string.Empty;

    [ObservableProperty]
    private string _windowStatus = string.Empty;

    [ObservableProperty]
    private bool _isWindowHighlighted;

    public bool HasWindowError => !string.IsNullOrEmpty(WindowError);

    public string WindowDefaultText => _localizer.Get(
        "settings.page.game.window.defaults",
        _widthOption.Definition.Format(_widthOption.Definition.DefaultValue),
        _heightOption.Definition.Format(_heightOption.Definition.DefaultValue));

    public string WindowSourceText => string.Equals(
        _widthOption.SourceText,
        _heightOption.SourceText,
        StringComparison.Ordinal)
        ? _widthOption.SourceText
        : $"{_widthOption.SourceText} · {_heightOption.SourceText}";

    public string WindowNote => _localizer.Get("settings.page.game.window.note");

    public bool CanApplyWindow => _widthOption.IsDirty || _heightOption.IsDirty;

    public string FullscreenLabel => _fullscreenOption.DisplayName;

    public string FullscreenDescription => _fullscreenOption.Description;

    public string FullscreenDefaultText => _fullscreenOption.DefaultText;

    public string FullscreenSourceText => _fullscreenOption.SourceText;

    public bool IsFullscreen
    {
        get => _fullscreenOption.BoolValue;
        set
        {
            if (_fullscreenOption.BoolValue == value)
            {
                return;
            }

            _fullscreenOption.BoolValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FullscreenSourceText));
            WindowStatus = string.Empty;
        }
    }

    public void Activate()
    {
        RefreshMemoryInfo();
        RefreshVisibility();
        NotifyWindowState();
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

        WindowWidthText = _widthOption.TextValue;
        WindowHeightText = _heightOption.TextValue;
        OnPropertyChanged(nameof(IsFullscreen));
        WindowError = string.Empty;
        WindowStatus = string.Empty;
        NotifyWindowState();
        RefreshVisibility();
        RefreshMemoryInfo();
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

        if (!target.IsVisible && target.Definition.VisibleWhenKey is { } dependencyKey)
        {
            var dependency = Options.FirstOrDefault(option =>
                string.Equals(option.Key, dependencyKey, StringComparison.Ordinal));
            if (dependency is not null)
            {
                HighlightOnly(dependency);
                var requiredChoice = dependency.Choices.FirstOrDefault(choice =>
                    string.Equals(
                        choice.Value,
                        target.Definition.VisibleWhenValue ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase));
                StatusMessage = _localizer.Get(
                    "settings.search.requires",
                    target.DisplayName,
                    dependency.DisplayName,
                    requiredChoice?.DisplayName ?? target.Definition.VisibleWhenValue ?? string.Empty);
                return true;
            }
        }

        // 窗口宽高/全屏不是通用设置行：高亮整块窗口卡片并说明定位目标。
        IsWindowHighlighted = target.Key is WindowWidthKey or WindowHeightKey or "game.fullscreen";
        IsMemoryHighlighted = target.Key is MemoryAllocationPolicy.PolicyKey or MemoryAllocationPolicy.ManualValueKey;
        HighlightOnly(target);
        StatusMessage = _localizer.Get("settings.search.located", target.DisplayName);
        return true;
    }

    private void HighlightOnly(OptionEditorViewModel target)
    {
        foreach (var option in Options)
        {
            option.IsHighlighted = ReferenceEquals(option, target);
        }
    }

    [RelayCommand]
    private void ApplyWindow()
    {
        WindowError = string.Empty;
        WindowStatus = string.Empty;

        var width = WindowWidthText.Trim();
        var height = WindowHeightText.Trim();

        if (!_widthOption.Definition.TryConvert(width, out _, out var widthError))
        {
            WindowError = _localizer.Get(
                "settings.page.game.window.invalid",
                WindowWidthLabel,
                widthError ?? string.Empty);
            return;
        }

        if (!_heightOption.Definition.TryConvert(height, out _, out var heightError))
        {
            WindowError = _localizer.Get(
                "settings.page.game.window.invalid",
                WindowHeightLabel,
                heightError ?? string.Empty);
            return;
        }

        var savedWidth = _settings.GetRawString(WindowWidthKey);
        if (!TryPersistWindowValue(WindowWidthKey, width, out var writeError))
        {
            WindowError = _localizer.Get(
                "settings.page.game.window.failed",
                writeError ?? string.Empty);
            return;
        }

        if (!TryPersistWindowValue(WindowHeightKey, height, out writeError))
        {
            // 宽度已写入：恢复为之前的有效值，避免只保存一半的新尺寸。
            TryPersistWindowValue(WindowWidthKey, savedWidth, out _);
            _widthOption.Reload();
            _heightOption.Reload();
            WindowWidthText = _widthOption.TextValue;
            WindowHeightText = _heightOption.TextValue;
            NotifyWindowState();
            WindowError = _localizer.Get(
                "settings.page.game.window.failed",
                writeError ?? string.Empty);
            return;
        }

        _widthOption.Reload();
        _heightOption.Reload();
        WindowWidthText = _widthOption.TextValue;
        WindowHeightText = _heightOption.TextValue;
        NotifyWindowState();
        WindowStatus = _localizer.Get("settings.page.game.window.applied");
    }

    [RelayCommand]
    private void RevertWindow()
    {
        _widthOption.Reload();
        _heightOption.Reload();
        WindowWidthText = _widthOption.TextValue;
        WindowHeightText = _heightOption.TextValue;
        WindowError = string.Empty;
        WindowStatus = string.Empty;
        NotifyWindowState();
    }

    partial void OnWindowErrorChanged(string value) => OnPropertyChanged(nameof(HasWindowError));

    private OptionEditorViewModel CreateOption(string key) => new(
        _settings.GetDefinition(key),
        _settings,
        _localizer,
        instanceId: null);

    private void RefreshVisibility()
    {
        foreach (var option in Options)
        {
            option.SetVisible(_settings.IsVisible(option.Definition));
        }

        // 旧配置兼容：策略键缺失但手动数值已显式保存时，运行时按“手动指定”解析，
        // 页面也必须显示该数值，否则用户会看到“自动”却在使用手动的值。
        if (_settings.GetSource(MemoryAllocationPolicy.PolicyKey) == SettingsValueSource.Default &&
            _settings.GetSource(MemoryAllocationPolicy.ManualValueKey) == SettingsValueSource.Global)
        {
            _manualMemoryOption.SetVisible(true);
        }
    }

    private void RefreshMemoryInfo()
    {
        var plan = _memoryAllocation.Resolve();

        MemoryTotalText = plan.TotalPhysicalMb > 0
            ? FormatMegabytes(plan.TotalPhysicalMb)
            : _localizer.Get("settings.page.game.memory.unknown");
        MemoryAvailableText = plan.AvailablePhysicalMb > 0
            ? FormatMegabytes(plan.AvailablePhysicalMb)
            : _localizer.Get("settings.page.game.memory.unknown");
        MemoryAllocationText = plan.Mode == MemoryAllocationMode.Automatic
            ? _localizer.Get("settings.page.game.memory.autoResult", FormatMegabytes(plan.EffectiveMb))
            : _localizer.Get("settings.page.game.memory.manualResult", FormatMegabytes(plan.EffectiveMb));

        if (plan.Mode == MemoryAllocationMode.Automatic && !plan.IsEstimateReliable)
        {
            MemoryWarning = _localizer.Get(
                "settings.page.game.memory.autoUnavailable",
                FormatMegabytes(plan.EffectiveMb));
        }
        else if (plan.Mode == MemoryAllocationMode.Manual &&
                 plan.AvailablePhysicalMb > 0 &&
                 plan.EffectiveMb > plan.AvailablePhysicalMb)
        {
            MemoryWarning = _localizer.Get(
                "settings.page.game.memory.manualPressure",
                FormatMegabytes(plan.EffectiveMb),
                FormatMegabytes(plan.AvailablePhysicalMb));
        }
        else
        {
            MemoryWarning = string.Empty;
        }
    }

    private bool TryPersistWindowValue(string key, string value, out string? error)
    {
        try
        {
            return _settings.TrySetRaw(key, value, null, out error);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private string FormatMegabytes(long megabytes) =>
        _localizer.Get("settings.page.game.memory.megabytes", megabytes);

    private void NotifyWindowState()
    {
        OnPropertyChanged(nameof(CanApplyWindow));
        OnPropertyChanged(nameof(WindowDefaultText));
        OnPropertyChanged(nameof(WindowSourceText));
    }

    private SettingsUnavailableItemViewModel Boundary(string name) => new(
        _localizer.Get($"settings.page.game.boundary.{name}.title"),
        _localizer.Get($"settings.page.game.boundary.{name}.reason"));
}
