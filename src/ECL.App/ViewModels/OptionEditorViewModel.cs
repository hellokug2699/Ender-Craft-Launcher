// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Microsoft.Win32;

namespace Ecl.App.ViewModels;

/// <summary>
/// 单个设置项的编辑状态。定义与默认值来自 <see cref="OptionDefinition"/>（ADR 0002），
/// 页面只负责呈现与把用户输入交给 <see cref="ISettingsService"/>。
///
/// 保存策略（<c>settings.md</c> §4.1）：
/// - Boolean/Select 这类单一且可独立生效的选项在值变化后即时校验并保存；
/// - Text/Number/Path 需要完整输入才能校验，提供明确的“应用/还原”操作；
/// - 非法值不写入配置，在设置项附近显示可定位的原因。
/// </summary>
public sealed partial class OptionEditorViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ILocalizer _localizer;
    private readonly string? _instanceId;
    private readonly IReadOnlyDictionary<string, string> _valueAliases;
    private string _savedRaw;
    private bool _suppressApply;

    public OptionDefinition Definition { get; }

    public string Key => Definition.Key;

    public string DisplayName { get; }

    public string Description { get; }

    public string Unit { get; }

    public string HintText { get; }

    public bool HasHint => !string.IsNullOrEmpty(HintText);

    public string SourceText { get; private set; } = string.Empty;

    /// <summary>元数据默认值；仅用于呈现“默认：xxx”，不会回写配置。</summary>
    public string DefaultText { get; }

    public bool RequiresRestart => Definition.RequiresRestart;

    public bool IsBoolean => Definition.Kind == OptionKind.Boolean;

    public bool IsSelect => Definition.Kind == OptionKind.Select;

    public bool IsPath => Definition.Kind == OptionKind.Path;

    public bool IsNumber => Definition.Kind is OptionKind.Integer or OptionKind.Decimal;

    public bool IsText => Definition.Kind is OptionKind.Text or OptionKind.Color or OptionKind.KeyBinding;

    /// <summary>值变化后是否立即保存；需要完整输入的控件为 <c>false</c>。</summary>
    public bool IsAutoApply => Definition.Kind is OptionKind.Boolean or OptionKind.Select;

    /// <summary>需要显式“应用”的控件才显示应用/还原按钮。</summary>
    public bool HasManualApply => !IsAutoApply;

    public IReadOnlyList<OptionChoiceItem> Choices { get; }

    [ObservableProperty]
    private string _textValue;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isHighlighted;

    [ObservableProperty]
    private string _errorText = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool IsDirty => !string.Equals(TextValue, _savedRaw, StringComparison.Ordinal);

    public event EventHandler? ValueChanged;

    public OptionEditorViewModel(
        OptionDefinition definition,
        ISettingsService settings,
        ILocalizer localizer,
        string? instanceId,
        IReadOnlyDictionary<string, string>? valueAliases = null)
    {
        Definition = definition;
        _settings = settings;
        _localizer = localizer;
        _instanceId = instanceId;
        _valueAliases = valueAliases ?? EmptyAliases;

        DisplayName = localizer.Get(definition.DisplayNameKey);
        Description = definition.DescriptionKey is null ? string.Empty : localizer.Get(definition.DescriptionKey);
        Unit = definition.UnitKey is null ? string.Empty : localizer.Get(definition.UnitKey);
        HintText = definition.EmptyIsNull ? localizer.Get("settings.inheritHint") : string.Empty;
        Choices = definition.Choices
            .Select(choice => new OptionChoiceItem(choice.Value, localizer.Get(choice.DisplayNameKey)))
            .ToArray();
        DefaultText = localizer.Get("settings.defaultValue", DescribeDefault(definition));

        _savedRaw = settings.GetRawString(definition.Key, instanceId);
        _textValue = _savedRaw;
        RefreshSource();
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool BoolValue
    {
        get => string.Equals(TextValue, "true", StringComparison.OrdinalIgnoreCase);
        set
        {
            TextValue = value ? "true" : "false";
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Select 的当前项。旧值/别名（例如 D-01 的 stable/beta）按同一显示项解析，
    /// 用户重新选择后才写入规范值；解析不到时下拉框保持空白而不是伪造一个选项。
    /// </summary>
    public OptionChoiceItem? SelectedChoice
    {
        get => ResolveChoice(TextValue);
        set
        {
            if (value is not null)
            {
                TextValue = value.Value;
            }
        }
    }

    public void SetVisible(bool visible) => IsVisible = visible;

    /// <summary>把输入交给配置服务。失败时保留用户输入并显示真实原因，不写入有效配置。</summary>
    public bool Apply(out string? error)
    {
        if (!_settings.TrySetRaw(Key, TextValue, _instanceId, out error))
        {
            ErrorText = error ?? _localizer.Get("settings.invalid");
            return false;
        }

        ErrorText = string.Empty;
        _savedRaw = _settings.GetRawString(Key, _instanceId);
        if (!string.Equals(TextValue, _savedRaw, StringComparison.Ordinal))
        {
            // 配置服务会规范化数值/布尔文本；界面显示规范化后的持久化值，避免“看起来仍未保存”。
            _suppressApply = true;
            try
            {
                TextValue = _savedRaw;
            }
            finally
            {
                _suppressApply = false;
            }
        }

        RefreshSource();
        NotifyState();
        return true;
    }

    /// <summary>把编辑值恢复成当前持久化值，不写配置。</summary>
    public void Reload()
    {
        _suppressApply = true;
        try
        {
            _savedRaw = _settings.GetRawString(Key, _instanceId);
            TextValue = _savedRaw;
        }
        finally
        {
            _suppressApply = false;
        }

        ErrorText = string.Empty;
        RefreshSource();
        NotifyState();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshSource()
    {
        var source = _settings.GetSource(Key, _instanceId);
        var sourceText = Definition.Scope == OptionScope.Instance && source == SettingsValueSource.Default
            ? _localizer.Get("common.inherit")
            : _localizer.Get($"settings.source.{source.ToString().ToLowerInvariant()}");

        SourceText = _localizer.Get("settings.sourceLabel", sourceText);
        OnPropertyChanged(nameof(SourceText));
    }

    partial void OnTextValueChanged(string value)
    {
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(SelectedChoice));
        NotifyState();

        if (_suppressApply)
        {
            return;
        }

        if (IsAutoApply && !Apply(out _))
        {
            // 保存失败（例如配置服务故障）时回到持久化值，不让界面显示未落盘的状态。
            Reload();
            return;
        }

        // 通知放在保存之后：依赖它的可见性判断必须读取已经持久化的新值。
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasError));
    }

    [RelayCommand]
    private void Commit() => Apply(out _);

    [RelayCommand]
    private void Revert() => Reload();

    private OptionChoiceItem? ResolveChoice(string raw)
    {
        var direct = Choices.FirstOrDefault(choice =>
            string.Equals(choice.Value, raw, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return direct;
        }

        return _valueAliases.TryGetValue(raw, out var canonical)
            ? Choices.FirstOrDefault(choice =>
                string.Equals(choice.Value, canonical, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private string DescribeDefault(OptionDefinition definition)
    {
        var raw = definition.Format(definition.DefaultValue);
        if (definition.Kind != OptionKind.Select)
        {
            return raw;
        }

        var choice = definition.Choices.FirstOrDefault(candidate =>
            string.Equals(candidate.Value, raw, StringComparison.OrdinalIgnoreCase));
        return choice is null ? raw : _localizer.Get(choice.DisplayNameKey);
    }

    [RelayCommand]
    private void Browse()
    {
        if (Definition.Key.Contains("customJvmPreset", StringComparison.OrdinalIgnoreCase))
        {
            var fileDialog = new OpenFileDialog
            {
                Title = _localizer.Get("common.browseFile"),
                Filter = "JVM preset (*.json)|*.json|All files (*.*)|*.*",
                Multiselect = false,
            };

            if (fileDialog.ShowDialog() == true)
            {
                TextValue = fileDialog.FileName;
            }

            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = _localizer.Get("common.browseFolder"),
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            TextValue = dialog.FolderName;
        }
    }
}

public sealed record OptionChoiceItem(string Value, string DisplayName);
