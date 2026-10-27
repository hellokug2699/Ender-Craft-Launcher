// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;

namespace Ecl.App.ViewModels;

public sealed partial class ModsViewModel : ObservableObject
{
    private readonly IInstanceService _instances;
    private readonly IModService _mods;
    private readonly IModpackService _modpacks;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private bool _initialized;

    public ObservableCollection<ModInstanceChoice> Instances { get; } = [];

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];

    [ObservableProperty]
    private ModInstanceChoice? _selectedInstance;

    [ObservableProperty]
    private ModItemViewModel? _selectedMod;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public ModsViewModel(
        IInstanceService instances,
        IModService mods,
        IModpackService modpacks,
        IDialogService dialogs,
        ILocalizer localizer)
    {
        _instances = instances;
        _mods = mods;
        _modpacks = modpacks;
        _dialogs = dialogs;
        _localizer = localizer;
    }

    public async Task InitializeAsync()
    {
        await ReloadInstancesAsync().ConfigureAwait(true);
        _initialized = true;
        if (SelectedInstance is not null)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ReloadInstancesAsync()
    {
        var selectedId = SelectedInstance?.Id;
        var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);

        Instances.Clear();
        foreach (var instance in instances)
        {
            Instances.Add(new ModInstanceChoice(instance.Id, instance.Name));
        }

        SelectedInstance = Instances.FirstOrDefault(item => item.Id == selectedId) ?? Instances.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedInstance is null)
        {
            Mods.Clear();
            Summary = _localizer.Get("mods.noInstance");
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _mods.ScanAsync(SelectedInstance.Id).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.ToString() ?? _localizer.Get("mods.scanFailed");
                return;
            }

            var report = result.Value!;
            Mods.Clear();
            foreach (var mod in report.Mods)
            {
                Mods.Add(new ModItemViewModel(mod, _localizer));
            }

            Summary = _localizer.Get(
                "mods.summary",
                report.EnabledCount,
                report.DisabledCount,
                report.IssueCount);
            StatusMessage = string.Empty;
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleSelectedAsync()
    {
        if (SelectedInstance is null || SelectedMod is null)
        {
            StatusMessage = _localizer.Get("mods.selectMod");
            return;
        }

        var result = await _mods
            .SetEnabledAsync(SelectedInstance.Id, SelectedMod.FileName, !SelectedMod.Enabled)
            .ConfigureAwait(true);

        if (!result.IsSuccess)
        {
            StatusMessage = result.Error?.ToString() ?? _localizer.Get("mods.updateFailed");
            return;
        }

        StatusMessage = _localizer.Get(
            SelectedMod.Enabled ? "mods.disabled" : "mods.enabled",
            SelectedMod.FileName);
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task EnableAllAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var result = await _mods.SetAllEnabledAsync(SelectedInstance.Id, true).ConfigureAwait(true);
        StatusMessage = result.IsSuccess
            ? _localizer.Get("mods.batchDone", result.Value!.Succeeded)
            : result.Error?.ToString() ?? _localizer.Get("mods.updateFailed");
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DisableAllAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var result = await _mods.SetAllEnabledAsync(SelectedInstance.Id, false).ConfigureAwait(true);
        StatusMessage = result.IsSuccess
            ? _localizer.Get("mods.batchDone", result.Value!.Succeeded)
            : result.Error?.ToString() ?? _localizer.Get("mods.updateFailed");
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ImportModsAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var files = _dialogs.PickOpenFiles(
            _localizer.Get("mods.importTitle"),
            _localizer.Get("mods.importFilter"));
        if (files.Count == 0)
        {
            return;
        }

        var result = await _mods.ImportAsync(SelectedInstance.Id, files).ConfigureAwait(true);
        StatusMessage = result.IsSuccess
            ? _localizer.Get("mods.importDone", result.Value!.Succeeded)
            : result.Error?.ToString() ?? _localizer.Get("mods.importFailed");
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ImportModpackAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var file = _dialogs.PickOpenFile(
            _localizer.Get("mods.modpackTitle"),
            _localizer.Get("mods.modpackFilter"));
        if (file is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _modpacks.ImportAsync(SelectedInstance.Id, file).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.ToString() ?? _localizer.Get("mods.modpackFailed");
                return;
            }

            StatusMessage = _localizer.Get(
                "mods.modpackDone",
                result.Value!.DownloadedFiles,
                result.Value.OverrideFiles);
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportModpackAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var output = _dialogs.PickSaveFile(
            _localizer.Get("mods.exportTitle"),
            _localizer.Get("mods.modpackFilter"),
            $"{SelectedInstance.Name}.mrpack");
        if (output is null)
        {
            return;
        }

        var result = await _modpacks.ExportAsync(SelectedInstance.Id, output).ConfigureAwait(true);
        StatusMessage = result.IsSuccess
            ? _localizer.Get("mods.exportDone", result.Value!.Files)
            : result.Error?.ToString() ?? _localizer.Get("mods.exportFailed");
    }

    partial void OnSelectedInstanceChanged(ModInstanceChoice? value)
    {
        if (_initialized)
        {
            _ = RefreshAsync();
        }
    }
}

public sealed record ModInstanceChoice(string Id, string Name);

public sealed class ModItemViewModel
{
    private readonly ModEntry _entry;
    private readonly ILocalizer _localizer;

    public ModItemViewModel(ModEntry entry, ILocalizer localizer)
    {
        _entry = entry;
        _localizer = localizer;
    }

    public string FileName => _entry.FileName;

    public bool Enabled => _entry.Enabled;

    public string StatusText => Enabled
        ? _localizer.Get("mods.enabledLabel")
        : _localizer.Get("mods.disabledLabel");

    public string Name => _entry.Metadata?.Name ?? _entry.FileName;

    public string Meta => _entry.Metadata is null
        ? _entry.FileName
        : $"{_entry.Metadata.ModId} · {_entry.Metadata.Version} · {_entry.Metadata.Platform}";

    public string IssuesText => string.Join(
        Environment.NewLine,
        _entry.Issues.Select(issue => Describe(issue)));

    public bool HasIssues => _entry.Issues.Count > 0;

    private string Describe(ModIssue issue) => issue.Kind switch
    {
        ModIssueKind.DuplicateModId => _localizer.Get("mod.issue.duplicate", issue.ModId ?? "?", issue.RelatedModId ?? "?"),
        ModIssueKind.MissingDependency => _localizer.Get("mod.issue.missingDependency", issue.ModId ?? "?", issue.RelatedModId ?? "?"),
        ModIssueKind.DisabledDependency => _localizer.Get("mod.issue.disabledDependency", issue.ModId ?? "?", issue.RelatedModId ?? "?"),
        ModIssueKind.Conflict => _localizer.Get("mod.issue.conflict", issue.ModId ?? "?", issue.RelatedModId ?? "?"),
        ModIssueKind.LoaderMismatch => _localizer.Get("mod.issue.loaderMismatch", issue.ModId ?? "?", issue.Detail ?? "?"),
        ModIssueKind.InvalidMetadata => _localizer.Get("mod.issue.invalidMetadata", issue.FileName, issue.Detail ?? "?"),
        _ => issue.Kind.ToString(),
    };
}
