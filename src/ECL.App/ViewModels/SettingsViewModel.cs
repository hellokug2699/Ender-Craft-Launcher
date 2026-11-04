// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Deployment;
using Ecl.Minecraft.Instances;

namespace Ecl.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IInstanceService _instances;
    private readonly ILocalizer _localizer;
    private readonly CodeSigningTrustService _codeSigning;
    private readonly IDialogService _dialogs;
    private readonly List<OptionEditorViewModel> _editors = [];
    private bool _ready;

    public ObservableCollection<SettingsScopeOption> Scopes { get; }

    public ObservableCollection<InstanceChoice> InstanceChoices { get; } = [];

    public ObservableCollection<SettingsCategoryViewModel> Categories { get; } = [];

    [ObservableProperty]
    private SettingsScopeOption _selectedScope;

    [ObservableProperty]
    private InstanceChoice? _selectedInstance;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isInstanceScope;

    [ObservableProperty]
    private bool _hasInstances;

    [ObservableProperty]
    private string _codeSigningStatus = "正在读取代码签名证书…";

    [ObservableProperty]
    private string _codeSigningFingerprint = string.Empty;

    [ObservableProperty]
    private bool _isCodeSigningTrusted;

    [ObservableProperty]
    private bool _canTrustCodeSigning;

    [ObservableProperty]
    private string _codeSigningActionStatus = string.Empty;

    private string _codeSigningExpectedFingerprint = string.Empty;

    public SettingsViewModel(
        ISettingsService settings,
        IInstanceService instances,
        ILocalizer localizer,
        CodeSigningTrustService codeSigning,
        IDialogService dialogs)
    {
        _settings = settings;
        _instances = instances;
        _localizer = localizer;
        _codeSigning = codeSigning;
        _dialogs = dialogs;

        Scopes =
        [
            new SettingsScopeOption(OptionScope.Global, localizer.Get("settings.scope.global")),
            new SettingsScopeOption(OptionScope.Instance, localizer.Get("settings.scope.instance")),
        ];

        _selectedScope = Scopes[0];
    }

    public async Task InitializeAsync()
    {
        await RefreshInstancesAsync().ConfigureAwait(true);
        RefreshCodeSigning();
        _ready = true;
        Rebuild();
    }

    [RelayCommand]
    private void TrustCodeSigning()
    {
        if (!CanTrustCodeSigning)
        {
            return;
        }

        var accepted = _dialogs.Confirm(
            "信任 ECL 代码签名证书",
            "这是 ECL 自行生成的自签名证书，不是公开 CA 对开发者现实身份的背书。\n\n" +
            $"SHA-256 指纹：\n{_codeSigningExpectedFingerprint}\n\n" +
            "导入后 Windows 将在 TrustedPeople 中信任该证书签署的代码。此操作需要管理员权限，且仅用于本次信任导入；" +
            "ECL 的常规启动、更新和游戏启动不会请求管理员权限。\n\n" +
            "你可以拒绝，ECL 仍可运行并使用更新 Manifest 验签。是否继续？",
            "code-signing.trust.v1");
        if (!accepted)
        {
            return;
        }

        var result = _codeSigning.Trust(_codeSigningExpectedFingerprint);
        CodeSigningActionStatus = result.IsSuccess ? "证书信任已导入。" : result.Error!.Message;
        RefreshCodeSigning();
    }

    [RelayCommand]
    private void RevokeCodeSigning()
    {
        var accepted = _dialogs.Confirm(
            "撤销 ECL 代码签名信任",
            "将从本机 TrustedPeople 中移除 ECL 的自签名代码证书。更新 Manifest 验签不受影响。是否继续？",
            "code-signing.revoke.v1");
        if (!accepted)
        {
            return;
        }

        var result = _codeSigning.Revoke(_codeSigningExpectedFingerprint);
        CodeSigningActionStatus = result.IsSuccess ? "已撤销本机信任。" : result.Error!.Message;
        RefreshCodeSigning();
    }

    private void RefreshCodeSigning()
    {
        var inspected = _codeSigning.Inspect();
        if (!inspected.IsSuccess)
        {
            CodeSigningStatus = inspected.Error!.Message;
            CodeSigningFingerprint = string.Empty;
            IsCodeSigningTrusted = false;
            CanTrustCodeSigning = false;
            return;
        }

        var info = inspected.Value!;
        CodeSigningStatus = info.IsTrusted
            ? "本机已信任该 ECL 代码签名证书。"
            : "本机尚未信任该证书；不影响 ECL 运行与更新 Manifest 验签。";
        CodeSigningFingerprint = info.Sha256Fingerprint;
        _codeSigningExpectedFingerprint = info.Sha256Fingerprint;
        IsCodeSigningTrusted = info.IsTrusted;
        CanTrustCodeSigning = !info.IsTrusted;
    }

    [RelayCommand]
    private async Task RefreshInstancesAsync()
    {
        var selectedId = SelectedInstance?.Id;
        var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);

        InstanceChoices.Clear();
        foreach (var instance in instances)
        {
            InstanceChoices.Add(new InstanceChoice(instance.Id, instance.Name));
        }

        HasInstances = InstanceChoices.Count > 0;
        SelectedInstance = InstanceChoices.FirstOrDefault(item => item.Id == selectedId) ?? InstanceChoices.FirstOrDefault();
    }

    [RelayCommand]
    private void Save()
    {
        foreach (var editor in _editors.Where(editor => editor.IsVisible))
        {
            if (!editor.Apply(out var error))
            {
                StatusMessage = error ?? _localizer.Get("settings.invalid");
                return;
            }
        }

        foreach (var editor in _editors)
        {
            editor.RefreshSource();
        }

        StatusMessage = _localizer.Get("settings.saved");
    }

    partial void OnSelectedScopeChanged(SettingsScopeOption value)
    {
        IsInstanceScope = value.Scope == OptionScope.Instance;
        if (_ready)
        {
            Rebuild();
        }
    }

    partial void OnSelectedInstanceChanged(InstanceChoice? value)
    {
        if (_ready && IsInstanceScope)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Categories.Clear();
        _editors.Clear();
        StatusMessage = string.Empty;

        var scope = SelectedScope.Scope;
        var instanceId = scope == OptionScope.Instance ? SelectedInstance?.Id : null;

        if (scope == OptionScope.Instance && instanceId is null)
        {
            StatusMessage = _localizer.Get("settings.noInstances");
            return;
        }

        var definitions = _settings.GetDefinitions(scope)
            .Where(definition => !definition.IsHidden);
        var groups = definitions
            .GroupBy(definition => definition.CategoryKey)
            .OrderBy(group => group.Min(definition => definition.Order));

        foreach (var group in groups)
        {
            var editors = group
                .OrderBy(definition => definition.Order)
                .Select(definition => new OptionEditorViewModel(definition, _settings, _localizer, instanceId))
                .ToArray();

            foreach (var editor in editors)
            {
                editor.ValueChanged += OnEditorValueChanged;
                _editors.Add(editor);
            }

            Categories.Add(new SettingsCategoryViewModel(_localizer.Get(group.Key), editors));
        }

        RefreshVisibility();

        if (scope == OptionScope.Instance)
        {
            StatusMessage = _localizer.Get("settings.inheritHint");
        }
        else if (_editors.Any(editor => editor.RequiresRestart))
        {
            StatusMessage = _localizer.Get("settings.restartHint");
        }
    }

    private void OnEditorValueChanged(object? sender, EventArgs e) => RefreshVisibility();

    private void RefreshVisibility()
    {
        var instanceId = SelectedScope.Scope == OptionScope.Instance ? SelectedInstance?.Id : null;
        var values = _editors.ToDictionary(editor => editor.Key, editor => editor.TextValue, StringComparer.Ordinal);

        foreach (var editor in _editors)
        {
            var definition = editor.Definition;
            if (definition.VisibleWhenKey is null)
            {
                editor.SetVisible(true);
                continue;
            }

            var dependency = values.TryGetValue(definition.VisibleWhenKey, out var current)
                ? current
                : _settings.GetRawString(definition.VisibleWhenKey, instanceId);

            editor.SetVisible(string.Equals(dependency, definition.VisibleWhenValue, StringComparison.OrdinalIgnoreCase));
        }
    }
}

public sealed record SettingsScopeOption(OptionScope Scope, string Title);

public sealed record InstanceChoice(string Id, string Name);

public sealed class SettingsCategoryViewModel(string title, IReadOnlyList<OptionEditorViewModel> options)
{
    public string Title { get; } = title;

    public IReadOnlyList<OptionEditorViewModel> Options { get; } = options;
}
