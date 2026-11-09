// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Configuration.Network;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Download;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 设置中心「网络」页（settings-network.md，B3 步骤 5 / D-04）。
///
/// - 代理模式沿用元数据项并即时保存；
/// - 手动代理的协议 / 主机 / 端口作为一组校验并写入，任何失败都不覆盖最后一次有效配置；
/// - 凭据只写入 Windows 用户范围保护存储，页面不回显密码、也不把密码放进可绑定状态；
/// - 测试连接使用与下载链路相同的端点/凭据构造方式，只验证指定目标；
/// - 旧版 network.proxyAddress 只作为迁移输入；无法无歧义解析时保留原值并提示修复；
/// - 手动代理配置无效时下载链路显式失败，不静默回退系统代理。
///
/// D-02（新安装默认不使用代理）仍受安装代际信号工程阻断：本页不改写默认值，
/// 在页面上如实说明，而不是放一个不生效的默认值开关。
/// </summary>
public sealed partial class SettingsNetworkPageViewModel : ObservableObject, ISettingsPage
{
    private readonly ISettingsService _settings;
    private readonly ILocalizer _localizer;
    private readonly IDialogService _dialogs;
    private readonly IProxyCredentialStore _credentials;
    private readonly IProxyConnectionTester _tester;

    private string _savedProtocol = ProxyProtocols.Http;
    private string _savedHost = string.Empty;
    private string _savedPort = string.Empty;

    public SettingsNetworkPageViewModel(
        ISettingsService settings,
        ILocalizer localizer,
        IDialogService dialogs,
        IProxyCredentialStore? credentials = null,
        IProxyConnectionTester? tester = null)
    {
        _settings = settings;
        _localizer = localizer;
        _dialogs = dialogs;
        _credentials = credentials ?? new NullProxyCredentialStore();
        _tester = tester ?? new ProxyConnectionTester();

        Title = localizer.Get("settings.nav.network");
        Subtitle = localizer.Get("settings.page.network.subtitle");

        ModeEditor = new OptionEditorViewModel(
            settings.GetDefinition(ProxySettings.ModeKey),
            settings,
            localizer,
            instanceId: null);
        ModeEditor.ValueChanged += OnModeEditorValueChanged;
        ModeGroup = new SettingsOptionGroupViewModel(
            localizer.Get("settings.page.network.group.proxy"),
            [ModeEditor],
            localizer.Get("settings.page.network.group.proxy.note"));

        ProtocolChoices = settings.GetDefinition(ProxySettings.ProtocolKey).Choices
            .Select(choice => new OptionChoiceItem(choice.Value, localizer.Get(choice.DisplayNameKey)))
            .ToArray();

        SearchEntries =
        [
            Entry(ProxySettings.ModeKey),
            Entry(ProxySettings.ProtocolKey),
            Entry(ProxySettings.HostKey),
            Entry(ProxySettings.PortKey),
        ];

        Boundaries = new SettingsUnavailableSectionViewModel(
            localizer.Get("settings.page.network.boundaries.title"),
            [
                Boundary("tls"),
                Boundary("system"),
                Boundary("ua"),
                Boundary("tokens"),
                Boundary("testScope"),
            ]);

        DefaultModeNote = localizer.Get("settings.page.network.defaultMode.note");
        CredentialUserNameLabel = localizer.Get("settings.page.network.credential.username");
        CredentialPasswordLabel = localizer.Get("settings.page.network.credential.password");
        CredentialSaveText = localizer.Get("settings.page.network.credential.save");
        CredentialClearText = localizer.Get("settings.page.network.credential.clear");
        TestTargetLabel = localizer.Get("settings.page.network.test.target");
        TestButtonText = localizer.Get("settings.page.network.test.button");

        LoadFromSettings();
    }

    public string Key => SettingsPageKeys.Network;

    public string Title { get; }

    public string Subtitle { get; }

    public OptionEditorViewModel ModeEditor { get; }

    public SettingsOptionGroupViewModel ModeGroup { get; }

    public IReadOnlyList<OptionChoiceItem> ProtocolChoices { get; }

    public IReadOnlyList<SettingsSearchEntry> SearchEntries { get; }

    public SettingsUnavailableSectionViewModel Boundaries { get; }

    public string DefaultModeNote { get; }

    public string CredentialUserNameLabel { get; }

    public string CredentialPasswordLabel { get; }

    public string CredentialSaveText { get; }

    public string CredentialClearText { get; }

    public string TestTargetLabel { get; }

    public string TestButtonText { get; }

    [ObservableProperty]
    private string _protocol = ProxyProtocols.Http;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private string _portText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasChanges;

    [ObservableProperty]
    private bool _isManualHighlighted;

    [ObservableProperty]
    private bool _isTestHighlighted;

    [ObservableProperty]
    private string _credentialUserName = string.Empty;

    [ObservableProperty]
    private string _credentialStatus = string.Empty;

    [ObservableProperty]
    private bool _hasCredential;

    [ObservableProperty]
    private string _testTarget = "https://launchermeta.mojang.com/";

    [ObservableProperty]
    private string _testStatus = string.Empty;

    [ObservableProperty]
    private bool _testSucceeded;

    [ObservableProperty]
    private bool _hasTestResult;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _hasLegacyAddress;

    [ObservableProperty]
    private string _legacyMessage = string.Empty;

    [ObservableProperty]
    private bool _canMigrateLegacy;

    [ObservableProperty]
    private string _legacyActionStatus = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool IsManualMode => string.Equals(
        ModeEditor.TextValue,
        ProxySettings.ManualMode,
        StringComparison.OrdinalIgnoreCase);

    public bool ShowManualCard => IsManualMode;

    public bool ShowCredentialCard =>
        IsManualMode && ProxyProtocols.SupportsCredentials(Protocol);

    public bool CanApplyManual => IsManualMode && HasChanges;

    public bool CanTest => !IsTesting;

    public bool CanClearCredential => HasCredential;

    /// <summary>最后一次已保存的有效端点；为空表示手动代理尚未配置完成。</summary>
    public string SavedEndpointText => TryGetSavedEndpoint()?.ToString() ?? string.Empty;

    public void Activate()
    {
        // 只刷新已保存状态；不覆盖用户尚未应用的手动端点编辑。
        ModeEditor.Reload();
        NotifyModeState();
        RefreshCredentialState();
        RefreshLegacyState();
    }

    public void ResetToDefaults()
    {
        if (!_dialogs.Confirm(
                _localizer.Get("settings.reset.title"),
                _localizer.Get("settings.reset.message", Title),
                acknowledgementToken: null))
        {
            return;
        }

        var errors = new List<string>();
        foreach (var key in new[]
                 {
                     ProxySettings.ModeKey,
                     ProxySettings.ProtocolKey,
                     ProxySettings.HostKey,
                     ProxySettings.PortKey,
                 })
        {
            if (!TryClear(key, out var error))
            {
                errors.Add(error ?? key);
            }
        }

        LoadFromSettings();
        StatusMessage = errors.Count == 0
            ? _localizer.Get("settings.page.network.reset.done")
            : _localizer.Get("settings.reset.partial", string.Join(';', errors));
    }

    public bool Highlight(string optionKey)
    {
        ClearHighlights();
        if (string.Equals(optionKey, ProxySettings.ModeKey, StringComparison.Ordinal))
        {
            ModeEditor.IsHighlighted = true;
            StatusMessage = _localizer.Get("settings.search.located", ModeEditor.DisplayName);
            return true;
        }

        if (optionKey is not (ProxySettings.ProtocolKey or ProxySettings.HostKey or ProxySettings.PortKey))
        {
            return false;
        }

        var targetName = _localizer.Get(optionKey switch
        {
            ProxySettings.ProtocolKey => "option.network.proxyProtocol.name",
            ProxySettings.HostKey => "option.network.proxyHost.name",
            _ => "option.network.proxyPort.name",
        });

        if (!IsManualMode)
        {
            ModeEditor.IsHighlighted = true;
            var manualChoice = ModeEditor.Choices.FirstOrDefault(choice =>
                string.Equals(choice.Value, ProxySettings.ManualMode, StringComparison.OrdinalIgnoreCase));
            StatusMessage = _localizer.Get(
                "settings.search.requires",
                targetName,
                ModeEditor.DisplayName,
                manualChoice?.DisplayName ?? ProxySettings.ManualMode);
            return true;
        }

        IsManualHighlighted = true;
        StatusMessage = _localizer.Get("settings.search.located", targetName);
        return true;
    }

    [RelayCommand]
    private void ApplyManual()
    {
        if (!IsManualMode)
        {
            return;
        }

        if (!TryCreateEndpoint(Protocol, Host, PortText, out var endpoint, out var endpointError))
        {
            ErrorText = DescribeEndpointError(endpointError);
            return;
        }

        var previous = new[]
        {
            Snapshot(ProxySettings.ProtocolKey),
            Snapshot(ProxySettings.HostKey),
            Snapshot(ProxySettings.PortKey),
        };

        if (!TryWrite(ProxySettings.ProtocolKey, endpoint!.Protocol, out var error)
            || !TryWrite(ProxySettings.HostKey, endpoint.Host, out error)
            || !TryWrite(ProxySettings.PortKey, endpoint.Port.ToString(CultureInfo.InvariantCulture), out error))
        {
            Restore(previous);
            ErrorText = _localizer.Get("settings.page.network.error.saveFailed", error ?? string.Empty);
            return;
        }

        _savedProtocol = endpoint.Protocol;
        _savedHost = endpoint.Host;
        _savedPort = endpoint.Port.ToString(CultureInfo.InvariantCulture);
        Protocol = _savedProtocol;
        Host = _savedHost;
        PortText = _savedPort;
        ErrorText = string.Empty;
        UpdateDirty();
        StatusMessage = _localizer.Get("settings.page.network.applied");
        if (!string.IsNullOrWhiteSpace(_settings.GetRawString(ProxySettings.LegacyAddressKey)))
        {
            if (!TryClear(ProxySettings.LegacyAddressKey, out var legacyError))
            {
                StatusMessage = _localizer.Get(
                    "settings.page.network.legacy.clearFailed",
                    legacyError ?? string.Empty);
            }
        }

        RefreshCredentialState();
        RefreshLegacyState();
    }

    [RelayCommand]
    private void RevertManual()
    {
        LoadFromSettings();
        StatusMessage = _localizer.Get("settings.page.network.reverted");
    }

    /// <summary>保存凭据。密码只作为参数传入，不写入任何可绑定属性。</summary>
    public bool SaveCredential(string password)
    {
        if (!ShowCredentialCard)
        {
            CredentialStatus = _localizer.Get("settings.page.network.credential.notSupported");
            return false;
        }

        if (HasChanges)
        {
            CredentialStatus = _localizer.Get("settings.page.network.credential.applyFirst");
            return false;
        }

        var endpoint = TryGetSavedEndpoint();
        if (endpoint is null)
        {
            CredentialStatus = _localizer.Get("settings.page.network.credential.applyFirst");
            return false;
        }

        var userName = CredentialUserName.Trim();
        if (userName.Length == 0)
        {
            CredentialStatus = _localizer.Get("settings.page.network.credential.usernameRequired");
            return false;
        }

        var effectivePassword = password;
        if (effectivePassword.Length == 0)
        {
            if (!HasCredential)
            {
                CredentialStatus = _localizer.Get("settings.page.network.credential.passwordRequired");
                return false;
            }

            var existing = _credentials.Read(endpoint);
            if (!existing.IsSuccess || existing.Value is null)
            {
                CredentialStatus = _localizer.Get(
                    "settings.page.network.credential.readFailed",
                    existing.Error?.Message ?? string.Empty);
                return false;
            }

            effectivePassword = existing.Value.Password;
        }

        var saved = _credentials.Save(endpoint, new ProxyCredential(userName, effectivePassword));
        if (!saved.IsSuccess)
        {
            CredentialStatus = _localizer.Get(
                "settings.page.network.credential.saveFailed",
                saved.Error?.Message ?? string.Empty);
            return false;
        }

        HasCredential = true;
        CredentialUserName = userName;
        CredentialStatus = _localizer.Get("settings.page.network.credential.saved", userName);
        return true;
    }

    [RelayCommand]
    private void ClearCredential()
    {
        if (!HasCredential)
        {
            return;
        }

        var endpoint = TryGetSavedEndpoint();
        if (endpoint is null)
        {
            CredentialStatus = _localizer.Get("settings.page.network.credential.applyFirst");
            return;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("settings.page.network.credential.clearTitle"),
                _localizer.Get("settings.page.network.credential.clearMessage", endpoint.ToString()),
                acknowledgementToken: null))
        {
            return;
        }

        var deleted = _credentials.Delete(endpoint);
        if (!deleted.IsSuccess)
        {
            CredentialStatus = _localizer.Get(
                "settings.page.network.credential.clearFailed",
                deleted.Error?.Message ?? string.Empty);
            return;
        }

        HasCredential = false;
        CredentialUserName = string.Empty;
        CredentialStatus = _localizer.Get("settings.page.network.credential.cleared");
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTesting)
        {
            return;
        }

        HasTestResult = false;
        TestSucceeded = false;
        TestStatus = string.Empty;

        var rawTarget = TestTarget.Trim();
        if (!Uri.TryCreate(rawTarget, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            HasTestResult = true;
            TestStatus = _localizer.Get("settings.page.network.test.invalidTarget");
            return;
        }

        ProxyEndpoint? endpoint = null;
        if (IsManualMode)
        {
            if (!TryCreateEndpoint(Protocol, Host, PortText, out endpoint, out var endpointError))
            {
                HasTestResult = true;
                TestStatus = DescribeEndpointError(endpointError);
                return;
            }
        }

        ProxyCredential? credential = null;
        string? credentialNote = null;
        if (endpoint is not null)
        {
            var savedEndpoint = TryGetSavedEndpoint();
            if (savedEndpoint == endpoint)
            {
                if (!_credentials.IsSupported)
                {
                    credentialNote = _localizer.Get("settings.page.network.test.credentialsUnsupported");
                }
                else
                {
                    var stored = _credentials.Read(endpoint);
                    if (stored.IsSuccess && stored.Value is not null)
                    {
                        credential = stored.Value;
                    }
                    else if (!stored.IsSuccess)
                    {
                        credentialNote = _localizer.Get(
                            "settings.page.network.test.credentialsReadFailed",
                            stored.Error?.Message ?? string.Empty);
                    }
                }
            }
            else
            {
                credentialNote = _localizer.Get("settings.page.network.test.credentialsNotApplied");
            }
        }

        IsTesting = true;
        HasTestResult = true;
        TestStatus = _localizer.Get("settings.page.network.test.running");
        try
        {
            var result = await _tester.TestAsync(new ProxyTestRequest
            {
                Mode = ProxyProtocols.Normalize(ModeEditor.TextValue),
                Endpoint = endpoint,
                Credential = credential,
                Target = target,
                Timeout = TimeSpan.FromSeconds(10),
            });

            TestSucceeded = result.Reached;
            if (result.Reached)
            {
                var status = result.StatusCode == 407
                    ? _localizer.Get("settings.page.network.test.proxyAuthRequired")
                    : _localizer.Get(
                        "settings.page.network.test.reached",
                        result.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "?",
                        result.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.CurrentCulture));
                TestStatus = credentialNote is null ? status : $"{status} {credentialNote}";
            }
            else
            {
                var status = _localizer.Get(
                    "settings.page.network.test.failed",
                    result.Detail ?? result.FailureCode ?? string.Empty);
                TestStatus = credentialNote is null ? status : $"{status} {credentialNote}";
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            TestSucceeded = false;
            TestStatus = _localizer.Get("settings.page.network.test.failed", exception.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task MigrateLegacyAsync()
    {
        var result = ProxySettingsMigration.TryMigrate(_settings, _credentials);
        switch (result.Status)
        {
            case ProxyMigrationStatus.Migrated:
                LoadFromSettings();
                StatusMessage = _localizer.Get("settings.page.network.legacy.migrated");
                break;
            case ProxyMigrationStatus.NothingToDo:
                LoadFromSettings();
                StatusMessage = _localizer.Get("settings.page.network.legacy.nothing");
                break;
            case ProxyMigrationStatus.Unresolved:
                LegacyActionStatus = _localizer.Get(
                    "settings.page.network.legacy.unresolved",
                    DescribeProxyErrorCode(result.ErrorCode, result.Detail));
                break;
            case ProxyMigrationStatus.Failed:
                LegacyActionStatus = _localizer.Get(
                    "settings.page.network.legacy.failed",
                    DescribeProxyErrorCode(result.ErrorCode, result.Detail));
                break;
        }

        await Task.CompletedTask;
    }

    partial void OnProtocolChanged(string value)
    {
        UpdateDirty();
        NotifyModeState();
        RefreshCredentialState();
    }

    partial void OnHostChanged(string value) => UpdateDirty();

    partial void OnPortTextChanged(string value) => UpdateDirty();

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnHasChangesChanged(bool value) => OnPropertyChanged(nameof(CanApplyManual));

    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(CanTest));

    private void OnModeEditorValueChanged(object? sender, EventArgs e)
    {
        ErrorText = string.Empty;
        NotifyModeState();
        RefreshCredentialState();
        RefreshLegacyState();
    }

    private void LoadFromSettings()
    {
        _savedProtocol = NormalizeSavedProtocol(_settings.GetRawString(ProxySettings.ProtocolKey));
        _savedHost = _settings.GetRawString(ProxySettings.HostKey);
        _savedPort = _settings.GetRawString(ProxySettings.PortKey);
        Protocol = _savedProtocol;
        Host = _savedHost;
        PortText = _savedPort;
        ModeEditor.Reload();
        ErrorText = string.Empty;
        UpdateDirty();
        NotifyModeState();
        RefreshCredentialState();
        RefreshLegacyState();
    }

    private static string NormalizeSavedProtocol(string? raw)
    {
        var normalized = ProxyProtocols.Normalize(raw);
        return normalized.Length == 0 ? ProxyProtocols.Http : normalized;
    }

    private void UpdateDirty()
    {
        HasChanges = !string.Equals(Protocol.Trim(), _savedProtocol, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Host.Trim(), _savedHost.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(PortText.Trim(), _savedPort.Trim(), StringComparison.Ordinal);
    }

    private void NotifyModeState()
    {
        OnPropertyChanged(nameof(IsManualMode));
        OnPropertyChanged(nameof(ShowManualCard));
        OnPropertyChanged(nameof(ShowCredentialCard));
        OnPropertyChanged(nameof(CanApplyManual));
        OnPropertyChanged(nameof(SavedEndpointText));
    }

    private void ClearHighlights()
    {
        ModeEditor.IsHighlighted = false;
        IsManualHighlighted = false;
        IsTestHighlighted = false;
    }

    private void RefreshCredentialState()
    {
        HasCredential = false;
        CredentialUserName = string.Empty;

        if (!IsManualMode)
        {
            CredentialStatus = _localizer.Get("settings.page.network.credential.modeHint");
            return;
        }

        if (!ProxyProtocols.SupportsCredentials(Protocol))
        {
            CredentialStatus = _localizer.Get("settings.page.network.credential.protocolUnsupported");
            return;
        }

        if (!_credentials.IsSupported)
        {
            CredentialStatus = _localizer.Get("settings.page.network.credential.notSupported");
            return;
        }

        var endpoint = TryGetSavedEndpoint();
        if (endpoint is null)
        {
            CredentialStatus = _localizer.Get("settings.page.network.credential.applyFirst");
            return;
        }

        var stored = _credentials.Read(endpoint);
        if (!stored.IsSuccess)
        {
            CredentialStatus = _localizer.Get(
                "settings.page.network.credential.readFailed",
                stored.Error?.Message ?? string.Empty);
            return;
        }

        if (stored.Value is null)
        {
            CredentialStatus = _localizer.Get("settings.page.network.credential.none");
            return;
        }

        HasCredential = true;
        CredentialUserName = stored.Value.UserName;
        CredentialStatus = _localizer.Get("settings.page.network.credential.saved", stored.Value.UserName);
    }

    private void RefreshLegacyState()
    {
        HasLegacyAddress = false;
        CanMigrateLegacy = false;
        LegacyMessage = string.Empty;
        LegacyActionStatus = string.Empty;

        var legacy = _settings.GetRawString(ProxySettings.LegacyAddressKey);
        if (string.IsNullOrWhiteSpace(legacy))
        {
            return;
        }

        HasLegacyAddress = true;
        var hasStructured = _settings.GetSource(ProxySettings.ProtocolKey) != SettingsValueSource.Default
            || _settings.GetSource(ProxySettings.HostKey) != SettingsValueSource.Default
            || _settings.GetSource(ProxySettings.PortKey) != SettingsValueSource.Default;
        if (hasStructured)
        {
            CanMigrateLegacy = true;
            LegacyMessage = _localizer.Get("settings.page.network.legacy.structured");
            return;
        }

        if (ProxySettings.TryParseLegacy(
                legacy,
                out _,
                out _,
                out _,
                out var error))
        {
            CanMigrateLegacy = true;
            LegacyMessage = _localizer.Get("settings.page.network.legacy.canMigrate");
        }
        else
        {
            LegacyMessage = _localizer.Get(
                "settings.page.network.legacy.unresolved",
                DescribeEndpointError(error));
        }
    }

    private ProxyEndpoint? TryGetSavedEndpoint() =>
        TryCreateEndpoint(_savedProtocol, _savedHost, _savedPort, out var endpoint, out _)
            ? endpoint
            : null;

    private static bool TryCreateEndpoint(
        string protocol,
        string host,
        string portText,
        out ProxyEndpoint? endpoint,
        out ProxyEndpointError error) =>
        ProxyEndpoint.TryCreate(protocol, host, portText, out endpoint, out error);

    private SettingsSnapshot Snapshot(string key) => new(
        key,
        _settings.GetRawString(key),
        _settings.GetSource(key));

    private bool TryWrite(string key, string value, out string? error)
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

    private bool TryClear(string key, out string? error)
    {
        try
        {
            return _settings.TryClear(key, null, out error);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private void Restore(IReadOnlyList<SettingsSnapshot> previous)
    {
        foreach (var snapshot in previous)
        {
            if (snapshot.Source == SettingsValueSource.Default)
            {
                TryClear(snapshot.Key, out _);
            }
            else
            {
                TryWrite(snapshot.Key, snapshot.Raw, out _);
            }
        }
    }

    private SettingsSearchEntry Entry(string key)
    {
        var definition = _settings.GetDefinition(key);
        return new SettingsSearchEntry(
            Key,
            Title,
            key,
            _localizer.Get(definition.DisplayNameKey),
            definition.DescriptionKey is null
                ? string.Empty
                : _localizer.Get(definition.DescriptionKey));
    }

    private SettingsUnavailableItemViewModel Boundary(string name) => new(
        _localizer.Get($"settings.page.network.boundary.{name}.title"),
        _localizer.Get($"settings.page.network.boundary.{name}.reason"));

    private string DescribeEndpointError(ProxyEndpointError error) => error switch
    {
        ProxyEndpointError.UnsupportedProtocol =>
            _localizer.Get("settings.page.network.error.protocol"),
        ProxyEndpointError.HostRequired =>
            _localizer.Get("settings.page.network.error.hostRequired"),
        ProxyEndpointError.HostInvalid =>
            _localizer.Get("settings.page.network.error.hostInvalid"),
        ProxyEndpointError.PortRequired =>
            _localizer.Get("settings.page.network.error.portRequired"),
        ProxyEndpointError.PortInvalid =>
            _localizer.Get("settings.page.network.error.portInvalid"),
        ProxyEndpointError.PortOutOfRange =>
            _localizer.Get("settings.page.network.error.portRange"),
        _ => _localizer.Get("settings.page.network.error.invalid"),
    };

    private string DescribeProxyErrorCode(string? code, string? fallback)
    {
        var key = code switch
        {
            "network.proxy.unsupported_protocol" => "settings.page.network.error.protocol",
            "network.proxy.host_required" => "settings.page.network.error.hostRequired",
            "network.proxy.host_invalid" => "settings.page.network.error.hostInvalid",
            "network.proxy.port_required" => "settings.page.network.error.portRequired",
            "network.proxy.port_invalid" => "settings.page.network.error.portInvalid",
            "network.proxy.port_range" => "settings.page.network.error.portRange",
            "network.proxy.invalid_mode" => "settings.page.network.error.mode",
            "network.proxy.manual_missing" => "settings.page.network.error.missing",
            _ => null,
        };

        return key is null
            ? fallback ?? string.Empty
            : _localizer.Get(key);
    }

    private sealed record SettingsSnapshot(
        string Key,
        string Raw,
        SettingsValueSource Source);
}
