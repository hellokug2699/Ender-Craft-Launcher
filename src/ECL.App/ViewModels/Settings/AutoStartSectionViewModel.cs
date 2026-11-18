// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Deployment.Startup;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 设置中心「常规 → Windows 登录启动」分区（F-12）。
///
/// 为什么单独成对象：
/// - “设置是意图、注册表是结果”：切换开关后必须真的对齐 HKCU Run，并把失败原因与回滚暴露出来；
/// - 注册表可能被任务管理器、安装目录迁移或其它工具改动，因此页面每次进入都重新读取真实状态，
///   并提供不会改写用户设置的“重新应用”；
/// - 重置默认值不能顺手删掉登录启动项：注册表清理必须由用户在明确的确认里单独决定。
/// </summary>
public sealed partial class AutoStartSectionViewModel : ObservableObject
{
    private readonly IAutoStartRegistration _registration;
    private readonly AutoStartReconciler _reconciler;
    private readonly ISettingsService _settings;
    private readonly ILocalizer _localizer;
    private readonly ILogger<AutoStartSectionViewModel>? _logger;

    private bool _suppressApply;
    private bool _lastKnownValue;
    private bool _refreshing;

    public AutoStartSectionViewModel(
        IAutoStartRegistration registration,
        AutoStartReconciler reconciler,
        ISettingsService settings,
        ILocalizer localizer,
        ILogger<AutoStartSectionViewModel>? logger = null)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _logger = logger;

        Option = new OptionEditorViewModel(
            settings.GetDefinition(LauncherKeys.StartWithWindows),
            settings,
            localizer,
            instanceId: null);
        _lastKnownValue = Option.BoolValue;
        Option.ValueChanged += OnOptionValueChanged;

        Refresh();
    }

    /// <summary>元数据驱动的开关；由页面放进“启动”分组渲染。</summary>
    public OptionEditorViewModel Option { get; }

    public string Title => _localizer.Get("settings.page.general.autostart.title");

    public string Description => _localizer.Get("settings.page.general.autostart.desc");

    public string RegistryPathText => _localizer.Get("settings.page.general.autostart.registryPath");

    public string ReapplyText => _localizer.Get("settings.page.general.autostart.reapply");

    /// <summary>注册表当前是否登记了 ECL 登录启动项（与设置值可能不一致，需如实展示）。</summary>
    [ObservableProperty]
    private bool _isRegistered;

    /// <summary>当前运行方式是否支持注册登录启动项（开发构建 / dotnet 宿主通常不支持）。</summary>
    [ObservableProperty]
    private bool _isSupported = true;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _commandText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasCommand => !string.IsNullOrEmpty(CommandText);

    public bool CanReapply => IsSupported && !IsBusy;

    /// <summary>读取真实注册表状态；异常只记录并显示原因，不让设置页崩溃。</summary>
    public void Refresh()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            AutoStartState state;
            try
            {
                state = _registration.Read();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger?.LogWarning(exception, "读取登录启动项状态失败。");
                IsSupported = false;
                IsRegistered = false;
                CommandText = string.Empty;
                StatusText = _localizer.Get("settings.page.general.autostart.status.readFailed");
                ErrorText = exception.Message;
                return;
            }

            IsSupported = state.IsSupported;
            IsRegistered = state.IsEnabled;
            CommandText = state.Command ?? string.Empty;
            OnPropertyChanged(nameof(HasCommand));
            OnPropertyChanged(nameof(CanReapply));

            StatusText = !state.IsSupported
                ? _localizer.Get("settings.page.general.autostart.status.unsupported")
                : state.ErrorCode is not null
                    ? _localizer.Get("settings.page.general.autostart.status.error")
                    : state.IsEnabled
                        ? _localizer.Get("settings.page.general.autostart.status.enabled")
                        : _localizer.Get("settings.page.general.autostart.status.disabled");

            if (state.ErrorCode is not null)
            {
                ErrorText = state.Error ?? string.Empty;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// 开关变化后的真实对齐。成功时记录新的已知值；失败时把设置回滚到上一次生效值，
    /// 避免界面显示“已开启”而注册表其实没写进去。
    /// </summary>
    public void ApplyDesiredValue()
    {
        if (_suppressApply)
        {
            return;
        }

        var desired = Option.BoolValue;
        if (desired == _lastKnownValue)
        {
            return;
        }

        if (!IsSupported)
        {
            // 不支持注册时保留用户意图，但明确说明它当前不会生效（领域层同样不写注册表）。
            ErrorText = _localizer.Get("settings.page.general.autostart.status.unsupported");
            OnPropertyChanged(nameof(HasError));
            Refresh();
            return;
        }

        IsBusy = true;
        try
        {
            var result = _reconciler.Reconcile();
            if (result.IsSuccess)
            {
                _lastKnownValue = desired;
                ErrorText = string.Empty;
                OnPropertyChanged(nameof(HasError));
                Refresh();
                return;
            }

            _logger?.LogWarning(
                "应用登录启动项失败（期望 {Desired}）：{Code} {Message}",
                desired,
                result.Error?.Code,
                result.Error?.Message);

            _suppressApply = true;
            try
            {
                _settings.TrySetRaw(
                    LauncherKeys.StartWithWindows,
                    _lastKnownValue ? "true" : "false",
                    null,
                    out _);
                Option.Reload();
            }
            finally
            {
                _suppressApply = false;
            }

            ErrorText = Describe(result.Error);
            OnPropertyChanged(nameof(HasError));
            Refresh();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>“重新应用”：只按当前设置对齐注册表，不改写设置。</summary>
    [RelayCommand]
    private void Reapply()
    {
        if (IsBusy)
        {
            return;
        }

        if (!IsSupported)
        {
            ErrorText = _localizer.Get("settings.page.general.autostart.status.unsupported");
            OnPropertyChanged(nameof(HasError));
            return;
        }

        IsBusy = true;
        try
        {
            var result = _reconciler.Reconcile();
            if (result.IsSuccess)
            {
                _lastKnownValue = Option.BoolValue;
                ErrorText = string.Empty;
                OnPropertyChanged(nameof(HasError));
            }
            else
            {
                ErrorText = Describe(result.Error);
                OnPropertyChanged(nameof(HasError));
            }

            Refresh();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 删除登录启动项（只由用户在“恢复默认值”确认里显式触发）。
    /// 返回是否成功；失败原因写入 <see cref="ErrorText"/>。
    /// </summary>
    public bool RemoveRegistryEntry()
    {
        var result = _registration.SetEnabled(false);
        if (!result.IsSuccess)
        {
            ErrorText = Describe(result.Error);
            OnPropertyChanged(nameof(HasError));
            _logger?.LogWarning("移除登录启动项失败：{Code}", result.Error?.Code);
            Refresh();
            return false;
        }

        ErrorText = string.Empty;
        OnPropertyChanged(nameof(HasError));
        _lastKnownValue = Option.BoolValue;
        Refresh();
        return true;
    }

    /// <summary>
    /// 重置默认值前调用：清空设置项会触发 <see cref="OptionEditorViewModel.ValueChanged"/>，
    /// 但重置本身不是“用户切换开关”，不能顺带去写/删注册表。
    /// </summary>
    public void BeginReset() => _suppressApply = true;

    /// <summary>重置完成后重新同步“已知值”，并恢复开关的自动应用。</summary>
    public void OnSettingsReset()
    {
        _suppressApply = true;
        try
        {
            Option.Reload();
            _lastKnownValue = Option.BoolValue;
        }
        finally
        {
            _suppressApply = false;
        }

        Refresh();
    }

    private void OnOptionValueChanged(object? sender, EventArgs e) => ApplyDesiredValue();

    private string Describe(EclError? error)
    {
        if (error is null)
        {
            return _localizer.Get("settings.page.general.autostart.error.unknown");
        }

        // 领域层返回稳定错误码；有对应本地化文案时用文案，否则退回原始消息。
        var key = $"settings.page.general.autostart.error.{error.Code}";
        var localized = _localizer.Get(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? error.Message
            : localized;
    }
}
