// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;

namespace Ecl.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IInstanceService _instances;
    private readonly ILocalizer _localizer;
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

    public SettingsViewModel(ISettingsService settings, IInstanceService instances, ILocalizer localizer)
    {
        _settings = settings;
        _instances = instances;
        _localizer = localizer;

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
        _ready = true;
        Rebuild();
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

        var definitions = _settings.GetDefinitions(scope);
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
