// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Microsoft.Win32;

namespace Ecl.App.ViewModels;

public sealed partial class OptionEditorViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ILocalizer _localizer;
    private readonly string? _instanceId;

    public OptionDefinition Definition { get; }

    public string Key => Definition.Key;

    public string DisplayName { get; }

    public string Description { get; }

    public string Unit { get; }

    public string HintText { get; }

    public bool HasHint => !string.IsNullOrEmpty(HintText);

    public string SourceText { get; private set; } = string.Empty;

    public bool RequiresRestart => Definition.RequiresRestart;

    public bool IsBoolean => Definition.Kind == OptionKind.Boolean;

    public bool IsSelect => Definition.Kind == OptionKind.Select;

    public bool IsPath => Definition.Kind == OptionKind.Path;

    public bool IsNumber => Definition.Kind is OptionKind.Integer or OptionKind.Decimal;

    public bool IsText => Definition.Kind is OptionKind.Text or OptionKind.Color or OptionKind.KeyBinding;

    public IReadOnlyList<OptionChoiceItem> Choices { get; }

    [ObservableProperty]
    private string _textValue;

    [ObservableProperty]
    private bool _isVisible = true;

    public event EventHandler? ValueChanged;

    public OptionEditorViewModel(
        OptionDefinition definition,
        ISettingsService settings,
        ILocalizer localizer,
        string? instanceId)
    {
        Definition = definition;
        _settings = settings;
        _localizer = localizer;
        _instanceId = instanceId;

        DisplayName = localizer.Get(definition.DisplayNameKey);
        Description = definition.DescriptionKey is null ? string.Empty : localizer.Get(definition.DescriptionKey);
        Unit = definition.UnitKey is null ? string.Empty : localizer.Get(definition.UnitKey);
        HintText = definition.EmptyIsNull ? localizer.Get("settings.inheritHint") : string.Empty;
        Choices = definition.Choices
            .Select(choice => new OptionChoiceItem(choice.Value, localizer.Get(choice.DisplayNameKey)))
            .ToArray();

        _textValue = settings.GetRawString(definition.Key, instanceId);
        RefreshSource();
    }

    public bool BoolValue
    {
        get => string.Equals(TextValue, "true", StringComparison.OrdinalIgnoreCase);
        set
        {
            TextValue = value ? "true" : "false";
            OnPropertyChanged();
        }
    }

    public void SetVisible(bool visible) => IsVisible = visible;

    public bool Apply(out string? error)
    {
        if (!_settings.TrySetRaw(Key, TextValue, _instanceId, out error))
        {
            return false;
        }

        RefreshSource();
        return true;
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
        ValueChanged?.Invoke(this, EventArgs.Empty);
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
