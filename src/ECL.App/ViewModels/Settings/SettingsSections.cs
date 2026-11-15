// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Deployment;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 一组设置项：标题 + 元数据驱动的编辑行。
/// 可选的 <paramref name="note"/> 用于说明一组偏好的生效范围或固定边界
/// （例如字体族只随包提供、语言切换需要重启），不占用设置项本身。
/// </summary>
public sealed class SettingsOptionGroupViewModel(
    string title,
    IReadOnlyList<OptionEditorViewModel> options,
    string? note = null)
{
    public string Title { get; } = title;

    public IReadOnlyList<OptionEditorViewModel> Options { get; } = options;

    public string Note { get; } = note ?? string.Empty;

    public bool HasNote => !string.IsNullOrEmpty(Note);
}

/// <summary>
/// 规格要求但当前版本尚不具备底层能力的分组。这里只如实说明缺什么，
/// 不放置不生效的开关或伪造的成功路径（总 Prompt §14.6）。
/// </summary>
public sealed class SettingsUnavailableSectionViewModel(
    string title,
    IReadOnlyList<SettingsUnavailableItemViewModel> items)
{
    public string Title { get; } = title;

    public IReadOnlyList<SettingsUnavailableItemViewModel> Items { get; } = items;
}

public sealed record SettingsUnavailableItemViewModel(string Title, string Reason)
{
    /// <summary>UIA/读屏只应看到条目名称，不暴露 record 的默认 ToString。</summary>
    public override string ToString() => Title;
}

/// <summary>
/// 常规页的 ECL 代码签名信任卡（ADR 0024）。只信任固定指纹的叶证书到 TrustedPeople。
/// 拒绝、取消 UAC 或没有管理员权限不影响 ECL 使用与更新 Manifest 验签；
/// 这里不提供“跳过签名验证/信任所有证书”的入口。
/// </summary>
public sealed partial class CodeSigningSectionViewModel : ObservableObject
{
    private readonly CodeSigningTrustService _codeSigning;
    private readonly ILocalizer _localizer;
    private readonly IDialogService _dialogs;
    private bool _activated;
    private string _expectedFingerprint = string.Empty;

    public CodeSigningSectionViewModel(
        CodeSigningTrustService codeSigning,
        ILocalizer localizer,
        IDialogService dialogs)
    {
        _codeSigning = codeSigning;
        _localizer = localizer;
        _dialogs = dialogs;
        _statusText = localizer.Get("settings.general.codeSigning.notRead");
    }

    public string Title => _localizer.Get("settings.general.codeSigning.title");

    public string Description => _localizer.Get("settings.general.codeSigning.description");

    public string StatusLabel => _localizer.Get("settings.general.codeSigning.statusLabel");

    public string FingerprintLabel => _localizer.Get("settings.general.codeSigning.fingerprintLabel");

    public string SubjectLabel => _localizer.Get("settings.general.codeSigning.subjectLabel");

    public string ValidityLabel => _localizer.Get("settings.general.codeSigning.validityLabel");

    public string TrustText => _localizer.Get("settings.general.codeSigning.trust");

    public string RevokeText => _localizer.Get("settings.general.codeSigning.revoke");

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _subjectText = string.Empty;

    [ObservableProperty]
    private string _fingerprintText = string.Empty;

    [ObservableProperty]
    private string _validityText = string.Empty;

    [ObservableProperty]
    private string _actionStatus = string.Empty;

    [ObservableProperty]
    private bool _isTrusted;

    [ObservableProperty]
    private bool _canTrust;

    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>首次显示常规页时才读取证书状态；打开设置中心不提前触碰证书存储。</summary>
    public void Activate()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        Refresh();
    }

    [RelayCommand]
    private void Trust()
    {
        if (!CanTrust || string.IsNullOrEmpty(_expectedFingerprint))
        {
            return;
        }

        var accepted = _dialogs.Confirm(
            _localizer.Get("settings.general.codeSigning.trustTitle"),
            _localizer.Get(
                "settings.general.codeSigning.trustMessage",
                _expectedFingerprint),
            acknowledgementToken: null);
        if (!accepted)
        {
            return;
        }

        var result = _codeSigning.Trust(_expectedFingerprint);
        ActionStatus = result.IsSuccess
            ? _localizer.Get("settings.general.codeSigning.trusted")
            : Describe(result.Error);
        Refresh();
    }

    [RelayCommand]
    private void Revoke()
    {
        if (!IsTrusted || string.IsNullOrEmpty(_expectedFingerprint))
        {
            return;
        }

        var accepted = _dialogs.Confirm(
            _localizer.Get("settings.general.codeSigning.revokeTitle"),
            _localizer.Get("settings.general.codeSigning.revokeMessage"),
            acknowledgementToken: null);
        if (!accepted)
        {
            return;
        }

        var result = _codeSigning.Revoke(_expectedFingerprint);
        ActionStatus = result.IsSuccess
            ? _localizer.Get("settings.general.codeSigning.revoked")
            : Describe(result.Error);
        Refresh();
    }

    private void Refresh()
    {
        var inspected = _codeSigning.Inspect();
        if (!inspected.IsSuccess)
        {
            IsAvailable = false;
            IsTrusted = false;
            CanTrust = false;
            SubjectText = string.Empty;
            ValidityText = string.Empty;
            FingerprintText = string.Empty;
            StatusText = Describe(inspected.Error);
            return;
        }

        var info = inspected.Value!;
        IsAvailable = true;
        IsTrusted = info.IsTrusted;
        CanTrust = !info.IsTrusted;
        _expectedFingerprint = info.Sha256Fingerprint;
        SubjectText = info.Subject;
        FingerprintText = info.Sha256Fingerprint;
        ValidityText = info.NotAfter is { } notAfter
            ? notAfter.ToLocalTime().ToString("yyyy-MM-dd")
            : _localizer.Get("settings.general.codeSigning.validityUnknown");
        StatusText = info.IsTrusted
            ? _localizer.Get("settings.general.codeSigning.statusTrusted")
            : _localizer.Get("settings.general.codeSigning.statusUntrusted");
    }

    private string Describe(Ecl.Core.EclError? error)
    {
        if (error is null)
        {
            return _localizer.Get("settings.general.codeSigning.error.unknown");
        }

        var key = $"settings.general.codeSigning.error.{error.Code}";
        var localized = _localizer.Get(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? error.Message
            : localized;
    }
}

/// <summary>
/// 通用元数据设置页：按目录给定的分组渲染 <see cref="OptionEditorViewModel"/>，
/// 不硬编码设置项、不复制默认值；每个分类只有一个纵向滚动容器。
/// </summary>
public sealed partial class SettingsOptionsPageViewModel : ObservableObject, ISettingsPage
{
    private readonly ISettingsService _settings;
    private readonly ILocalizer _localizer;
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyList<OptionEditorViewModel> _options;

    public SettingsOptionsPageViewModel(
        string key,
        string title,
        string subtitle,
        IReadOnlyList<object> sections,
        IReadOnlyList<OptionEditorViewModel> options,
        ISettingsService settings,
        ILocalizer localizer,
        IDialogService dialogs)
    {
        Key = key;
        Title = title;
        Subtitle = subtitle;
        Sections = new ObservableCollection<object>(sections);
        _options = options;
        _settings = settings;
        _localizer = localizer;
        _dialogs = dialogs;

        SearchEntries = options
            .Select(option => new SettingsSearchEntry(
                key,
                title,
                option.Key,
                option.DisplayName,
                option.Description))
            .ToArray();

        foreach (var option in options)
        {
            option.ValueChanged += OnOptionValueChanged;
        }

        RefreshVisibility();
    }

    public string Key { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public ObservableCollection<object> Sections { get; }

    public IReadOnlyList<SettingsSearchEntry> SearchEntries { get; }

    /// <summary>本页渲染的设置项；供测试与页面内定位使用，视图仍通过分组呈现。</summary>
    public IReadOnlyList<OptionEditorViewModel> Options => _options;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public void Activate()
    {
        foreach (var section in Sections.OfType<CodeSigningSectionViewModel>())
        {
            section.Activate();
        }
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
        foreach (var option in _options)
        {
            if (!_settings.TryClear(option.Key, null, out var error))
            {
                errors.Add($"{option.DisplayName}: {error}");
                continue;
            }

            option.Reload();
        }

        RefreshVisibility();
        StatusMessage = errors.Count == 0
            ? _localizer.Get("settings.reset.done", Title)
            : _localizer.Get("settings.reset.partial", string.Join(';', errors));
    }

    public bool Highlight(string optionKey)
    {
        var target = _options.FirstOrDefault(option =>
            string.Equals(option.Key, optionKey, StringComparison.Ordinal));
        if (target is null)
        {
            return false;
        }

        if (!target.IsVisible && target.Definition.VisibleWhenKey is { } dependencyKey)
        {
            // 依赖项未满足时目标行不在界面上：改为定位控制它的选项并说明解锁条件，
            // 而不是报告“已定位”却指向一个看不见的行。
            var dependency = _options.FirstOrDefault(option =>
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

    private void HighlightOnly(OptionEditorViewModel target)
    {
        foreach (var option in _options)
        {
            option.IsHighlighted = ReferenceEquals(option, target);
        }
    }

    private void OnOptionValueChanged(object? sender, EventArgs e) => RefreshVisibility();

    private void RefreshVisibility()
    {
        foreach (var option in _options)
        {
            option.SetVisible(_settings.IsVisible(option.Definition));
        }
    }
}
