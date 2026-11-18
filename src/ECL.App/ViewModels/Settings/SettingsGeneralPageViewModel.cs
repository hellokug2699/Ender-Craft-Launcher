// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.App.Services.Lifecycle;
using Ecl.App.Services.Updates;
using Ecl.App.Shell.Lifecycle;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Deployment;
using Microsoft.Extensions.Logging;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 设置中心「常规」页（F-12）：把桌面生命周期相关的 15 个隐藏键显式渲染出来。
///
/// 为什么需要专用页面：
/// - 这些键被登记为 <c>IsHidden</c>，通用元数据编辑器不会显示它们，但用户必须能改；
/// - “启动 / 窗口与关闭 / 更新 / 通知 / 确认与结果”是真实行为，不是占位开关：
///   每一项都对应托盘、窗口状态机、更新调度器、通知中心或结果弹窗的真实接线；
/// - 自动启动还需要展示真实注册表状态与失败回滚，通用分组做不到；
/// - 代码签名信任卡从原通用页迁移过来，仍是本页唯一的安全相关卡片。
/// </summary>
public sealed partial class SettingsGeneralPageViewModel : ObservableObject, ISettingsPage
{
    private readonly ISettingsService _settings;
    private readonly ILocalizer _localizer;
    private readonly IDialogService _dialogs;
    private readonly IUpdateCheckScheduler _scheduler;
    private readonly ITrayHost _tray;
    private readonly ILogger<SettingsGeneralPageViewModel>? _logger;

    private readonly OptionEditorViewModel _closeButtonAction;
    private readonly OptionEditorViewModel _minimizeAction;
    private readonly OptionEditorViewModel _confirmOnClose;
    private readonly OptionEditorViewModel _checkUpdates;
    private readonly OptionEditorViewModel _updateInterval;

    public SettingsGeneralPageViewModel(
        ISettingsService settings,
        ILocalizer localizer,
        IDialogService dialogs,
        CodeSigningTrustService codeSigning,
        AutoStartSectionViewModel autoStart,
        IUpdateCheckScheduler scheduler,
        ITrayHost tray,
        ILogger<SettingsGeneralPageViewModel>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _logger = logger;
        AutoStart = autoStart ?? throw new ArgumentNullException(nameof(autoStart));

        var startMinimized = Create(LauncherKeys.StartMinimizedToTray);
        var showHome = Create(LauncherKeys.ShowHomeOnStartup);
        _closeButtonAction = Create(LauncherKeys.CloseButtonAction);
        _minimizeAction = Create(LauncherKeys.MinimizeAction);
        _confirmOnClose = Create(LauncherKeys.ConfirmOnClose);
        _checkUpdates = Create(LauncherKeys.CheckUpdates);
        var updateChannel = Create(LauncherKeys.UpdateChannel, SettingsPageCatalog.UpdateChannelAliases);
        _updateInterval = Create(LauncherKeys.UpdateCheckInterval);
        var notifyCompleted = Create(LauncherKeys.NotifyTaskCompleted);
        var notifyFailed = Create(LauncherKeys.NotifyTaskFailed);
        var notifyBackground = Create(LauncherKeys.NotifyBackgroundTasks);
        var showHints = Create(LauncherKeys.ShowOperationHints);
        var confirmOrdinary = Create(LauncherKeys.ConfirmOrdinaryActions);
        // 既有键：保持“删除范围选择”行为不变，不把它混进普通操作确认。
        var confirmBeforeDelete = Create("launcher.confirmBeforeDelete");
        var autoCloseSuccess = Create(LauncherKeys.AutoCloseSuccessDialogs);

        Options =
        [
            AutoStart.Option,
            startMinimized,
            showHome,
            _closeButtonAction,
            _minimizeAction,
            _confirmOnClose,
            _checkUpdates,
            updateChannel,
            _updateInterval,
            notifyCompleted,
            notifyFailed,
            notifyBackground,
            showHints,
            confirmOrdinary,
            confirmBeforeDelete,
            autoCloseSuccess,
        ];

        StartGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.general.group.startup"),
            [AutoStart.Option, startMinimized, showHome],
            localizer.Get("settings.page.general.group.startup.desc"));
        WindowGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.general.group.window"),
            [_closeButtonAction, _minimizeAction, _confirmOnClose],
            localizer.Get("settings.page.general.group.window.desc"));
        UpdateGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.general.group.update"),
            [_checkUpdates, updateChannel, _updateInterval],
            localizer.Get("settings.page.general.group.update.desc"));
        NotificationGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.general.group.notification"),
            [notifyCompleted, notifyFailed, notifyBackground, showHints],
            localizer.Get("settings.page.general.notification.note"));
        ConfirmationGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.general.group.confirmation"),
            [confirmOrdinary, confirmBeforeDelete, autoCloseSuccess],
            localizer.Get("settings.page.general.confirmation.note"));

        CodeSigning = new CodeSigningSectionViewModel(codeSigning, localizer, dialogs);

        // Sections 只承载由模板渲染的分组；自动启动卡与状态行由本页视图显式绑定。
        Sections =
        [
            StartGroup,
            WindowGroup,
            UpdateGroup,
            NotificationGroup,
            ConfirmationGroup,
            CodeSigning,
        ];

        SearchEntries = Options
            .Select(option => new SettingsSearchEntry(
                SettingsPageKeys.General,
                Title,
                option.Key,
                option.DisplayName,
                option.Description))
            .ToArray();

        foreach (var option in Options)
        {
            option.ValueChanged += OnOptionValueChanged;
        }

        RefreshVisibility();
        RefreshUpdateState();
    }

    public string Key => SettingsPageKeys.General;

    public string Title => _localizer.Get("settings.nav.general");

    public string Subtitle => _localizer.Get("settings.page.general.subtitle");

    public IReadOnlyList<OptionEditorViewModel> Options { get; }

    public IReadOnlyList<object> Sections { get; }

    public SettingsOptionGroupViewModel StartGroup { get; }

    public SettingsOptionGroupViewModel WindowGroup { get; }

    public SettingsOptionGroupViewModel UpdateGroup { get; }

    public SettingsOptionGroupViewModel NotificationGroup { get; }

    public SettingsOptionGroupViewModel ConfirmationGroup { get; }

    public AutoStartSectionViewModel AutoStart { get; }

    public CodeSigningSectionViewModel CodeSigning { get; }

    public IReadOnlyList<SettingsSearchEntry> SearchEntries { get; }

    public string TrayStatusTitle => _localizer.Get("settings.page.general.tray.title");

    public bool IsTrayAvailable => _tray.IsAvailable;

    public bool ShowTrayUnavailable => !_tray.IsAvailable;

    public string TrayStatusText => IsTrayAvailable
        ? _localizer.Get("settings.page.general.tray.available")
        : _localizer.Get("settings.page.general.tray.unavailable.title");

    /// <summary>托盘不可用的真实原因（来自实现，而不是猜测）。</summary>
    public string TrayReasonText => IsTrayAvailable
        ? string.Empty
        : _localizer.Get(
            (_tray as ITrayHostStatus)?.UnavailableReasonKey ??
            "settings.page.general.tray.unavailable.reason");

    /// <summary>最近一次更新检查时间（来自调度状态键）；从未成功检查时明确说明。</summary>
    [ObservableProperty]
    private string _lastCheckedText = string.Empty;

    /// <summary>最近一次更新检查结论；只在本次运行真的检查过时非空。</summary>
    [ObservableProperty]
    private string _lastResultText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>进入页面时重新读取自动启动注册表与更新调度状态；不发起网络请求。</summary>
    public void Activate()
    {
        AutoStart.Refresh();
        CodeSigning.Activate();
        RefreshUpdateState();
        RefreshVisibility();
    }

    public void ResetToDefaults()
    {
        var title = _localizer.Get("settings.reset.title");
        var message = _localizer.Get("settings.reset.message", Title);
        if (!_dialogs.Confirm(title, message, acknowledgementToken: null))
        {
            return;
        }

        // 注册表不是设置项：重置前必须单独确认，且默认不删除登录启动项。
        var removeAutoStartEntry = false;
        if (AutoStart.IsRegistered)
        {
            removeAutoStartEntry = _dialogs.Confirm(
                _localizer.Get("settings.page.general.autostart.reset.title"),
                _localizer.Get("settings.page.general.autostart.reset.message"),
                acknowledgementToken: null);
        }

        var errors = new List<string>();
        // 清空设置会触发开关的 ValueChanged；重置不是用户切换，必须先抑制注册表写入。
        AutoStart.BeginReset();
        foreach (var option in Options)
        {
            if (!_settings.TryClear(option.Key, null, out var error))
            {
                errors.Add($"{option.DisplayName}: {error}");
                continue;
            }

            option.Reload();
        }

        if (removeAutoStartEntry && !AutoStart.RemoveRegistryEntry())
        {
            errors.Add(AutoStart.ErrorText);
        }

        AutoStart.OnSettingsReset();
        RefreshUpdateState();
        RefreshVisibility();
        StatusMessage = errors.Count == 0
            ? _localizer.Get("settings.reset.done", Title)
            : _localizer.Get("settings.reset.partial", string.Join(';', errors));

        if (!removeAutoStartEntry && AutoStart.IsRegistered)
        {
            // 不清注册表是默认行为，但用户必须知道它仍然存在。
            StatusMessage = _localizer.Get("settings.page.general.autostart.reset.kept", StatusMessage);
        }
    }

    public bool Highlight(string optionKey)
    {
        var target = Options.FirstOrDefault(option =>
            string.Equals(option.Key, optionKey, StringComparison.Ordinal));
        if (target is null)
        {
            return false;
        }

        if (!target.IsVisible && ReferenceEquals(target, _confirmOnClose))
        {
            // 可见性由页面叠加（只有“关闭按钮=退出”才有意义），元数据里没有 VisibleWhenKey；
            // 搜索仍应定位到控制它的选项，而不是指向一个看不见的行。
            HighlightOnly(_closeButtonAction);
            var exitChoice = _closeButtonAction.Choices.FirstOrDefault(choice =>
                string.Equals(
                    choice.Value,
                    LauncherKeys.CloseButtonActions.Exit,
                    StringComparison.OrdinalIgnoreCase));
            StatusMessage = _localizer.Get(
                "settings.search.requires",
                target.DisplayName,
                _closeButtonAction.DisplayName,
                exitChoice?.DisplayName ?? LauncherKeys.CloseButtonActions.Exit);
            return true;
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

        HighlightOnly(target);
        StatusMessage = _localizer.Get("settings.search.located", target.DisplayName);
        return true;
    }

    [RelayCommand]
    private void RefreshUpdateState()
    {
        var channel = _settings.Get<string>(LauncherKeys.UpdateChannel) ?? string.Empty;
        var lastChecked = TryReadLastCheckedAt();
        if (lastChecked is null)
        {
            LastCheckedText = _localizer.Get("settings.page.general.update.lastCheckedNever");
        }
        else
        {
            LastCheckedText = _localizer.Get(
                "settings.page.general.update.lastChecked",
                lastChecked.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                ChannelDisplay(string.IsNullOrWhiteSpace(channel) ? "main" : channel));
        }

        var report = _scheduler.LastReport;
        LastResultText = report is null
            ? _localizer.Get("settings.page.general.update.resultNone")
            : DescribeReport(report);
    }

    private OptionEditorViewModel Create(
        string key,
        IReadOnlyDictionary<string, string>? aliases = null) => new(
            _settings.GetDefinition(key),
            _settings,
            _localizer,
            instanceId: null,
            aliases);

    private void OnOptionValueChanged(object? sender, EventArgs e)
    {
        RefreshVisibility();
        if (ReferenceEquals(sender, _checkUpdates))
        {
            // 开关本身已由调度器在到期判断时读取；这里只刷新展示，不触发网络。
            RefreshUpdateState();
        }
    }

    /// <summary>
    /// 可见性由页面判断而不是元数据：<c>confirmOnClose</c> 只在关闭按钮行为为“直接退出”时才有意义。
    /// 元数据仍是隐藏项的权威来源，页面只叠加这一条依赖关系。
    /// </summary>
    private void RefreshVisibility()
    {
        foreach (var option in Options)
        {
            if (ReferenceEquals(option, _confirmOnClose))
            {
                option.SetVisible(
                    string.Equals(
                        _closeButtonAction.TextValue,
                        LauncherKeys.CloseButtonActions.Exit,
                        StringComparison.OrdinalIgnoreCase)
                    && IsOptionVisible(option));
                continue;
            }

            option.SetVisible(IsOptionVisible(option));
        }
    }

    /// <summary>
    /// 这些键在元数据里标记为 <c>IsHidden</c>（避免出现在通用编辑器与深链接里），
    /// 本页是它们的正式呈现入口，因此不能直接复用 <see cref="ISettingsService.IsVisible"/>：
    /// 隐藏只表示“不在通用列表里”，不表示“页面上也不可见”。这里只按依赖键求值。
    /// </summary>
    private bool IsOptionVisible(OptionEditorViewModel option)
    {
        var definition = option.Definition;
        if (string.IsNullOrEmpty(definition.VisibleWhenKey))
        {
            return true;
        }

        return string.Equals(
            _settings.GetRawString(definition.VisibleWhenKey, null),
            definition.VisibleWhenValue,
            StringComparison.OrdinalIgnoreCase);
    }

    private void HighlightOnly(OptionEditorViewModel target)
    {
        foreach (var option in Options)
        {
            option.IsHighlighted = ReferenceEquals(option, target);
        }
    }

    private DateTimeOffset? TryReadLastCheckedAt()
    {
        var raw = _settings.Get<string>(LauncherKeys.UpdateLastCheckedAt);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    private string DescribeReport(UpdateCheckReport report) => report.Outcome switch
    {
        UpdateCheckOutcome.Disabled => _localizer.Get("about.update.disabled"),
        UpdateCheckOutcome.Unavailable => _localizer.Get(
            "about.update.unavailable",
            report.Message ?? report.ErrorCode ?? string.Empty),
        UpdateCheckOutcome.UpToDate => _localizer.Get(
            "about.update.upToDate",
            report.CurrentVersion ?? string.Empty),
        UpdateCheckOutcome.UpdateAvailable => _localizer.Get(
            "about.update.available",
            report.AvailableVersion ?? string.Empty,
            ChannelDisplay(report.Channel)),
        UpdateCheckOutcome.Failed => _localizer.Get(
            "about.update.failed",
            report.Message ?? report.ErrorCode ?? string.Empty),
        _ => _localizer.Get("about.update.idle"),
    };

    private string ChannelDisplay(string channel)
    {
        var key = $"option.launcher.updateChannel.{channel}";
        var label = _localizer.Get(key);
        return string.Equals(label, key, StringComparison.Ordinal) ? channel : label;
    }
}
