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
    private readonly IContentPackService _contentPacks;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private bool _initialized;
    private bool _suppressSelectionRefresh;

    public ObservableCollection<ModInstanceChoice> Instances { get; } = [];

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];

    public ObservableCollection<PackTypeChoice> PackTypes { get; } = [];

    public ObservableCollection<ContentPackItemViewModel> Packs { get; } = [];

    [ObservableProperty]
    private ModInstanceChoice? _selectedInstance;

    [ObservableProperty]
    private ModItemViewModel? _selectedMod;

    [ObservableProperty]
    private PackTypeChoice? _selectedPackType;

    [ObservableProperty]
    private ContentPackItemViewModel? _selectedPack;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _packSummary = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public ModsViewModel(
        IInstanceService instances,
        IModService mods,
        IModpackService modpacks,
        IContentPackService contentPacks,
        IDialogService dialogs,
        ILocalizer localizer)
    {
        _instances = instances;
        _mods = mods;
        _modpacks = modpacks;
        _contentPacks = contentPacks;
        _dialogs = dialogs;
        _localizer = localizer;

        PackTypes.Add(new PackTypeChoice(ContentPackKind.ResourcePack, localizer.Get("mods.resourcePacks")));
        PackTypes.Add(new PackTypeChoice(ContentPackKind.ShaderPack, localizer.Get("mods.shaderPacks")));
        _selectedPackType = PackTypes[0];
    }

    public async Task InitializeAsync()
    {
        await ReloadInstancesAsync().ConfigureAwait(true);
        _initialized = true;
        if (SelectedInstance is not null)
        {
            await RefreshAsync().ConfigureAwait(true);
            await RefreshPacksAsync().ConfigureAwait(true);
        }
    }

    public async Task ActivateAsync()
    {
        if (!_initialized)
        {
            return;
        }

        await ReloadInstancesAsync().ConfigureAwait(true);
        if (SelectedInstance is not null)
        {
            await RefreshAsync().ConfigureAwait(true);
            await RefreshPacksAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task ReloadAsync() => ActivateAsync();

    [RelayCommand]
    private async Task ReloadInstancesAsync()
    {
        var selectedId = SelectedInstance?.Id;
        var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);

        _suppressSelectionRefresh = true;
        try
        {
            Instances.Clear();
            foreach (var instance in instances)
            {
                Instances.Add(new ModInstanceChoice(instance.Id, instance.Name));
            }

            SelectedInstance = Instances.FirstOrDefault(item => item.Id == selectedId)
                               ?? Instances.FirstOrDefault();
        }
        finally
        {
            _suppressSelectionRefresh = false;
        }
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
    private async Task RefreshPacksAsync()
    {
        if (SelectedInstance is null || SelectedPackType is null)
        {
            Packs.Clear();
            PackSummary = string.Empty;
            return;
        }

        try
        {
            var result = await _contentPacks
                .ListAsync(SelectedInstance.Id, SelectedPackType.Kind)
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.ToString() ?? _localizer.Get("mods.packScanFailed");
                return;
            }

            Packs.Clear();
            foreach (var pack in result.Value!)
            {
                Packs.Add(new ContentPackItemViewModel(pack));
            }

            PackSummary = _localizer.Get("mods.packCount", Packs.Count);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
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
    private async Task DeleteSelectedAsync()
    {
        if (SelectedInstance is null || SelectedMod is null)
        {
            StatusMessage = _localizer.Get("mods.selectMod");
            return;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("mods.deleteTitle"),
                _localizer.Get("mods.deleteConfirm", SelectedMod.FileName)))
        {
            return;
        }

        var result = await _mods.DeleteAsync(SelectedInstance.Id, SelectedMod.FileName).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            StatusMessage = result.Error?.ToString() ?? _localizer.Get("mods.deleteFailed");
            return;
        }

        StatusMessage = _localizer.Get("mods.deleted", SelectedMod.FileName);
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
    private async Task ImportPackAsync()
    {
        if (SelectedInstance is null || SelectedPackType is null)
        {
            return;
        }

        var file = _dialogs.PickOpenFile(
            _localizer.Get("mods.packImportTitle"),
            _localizer.Get("mods.packFilter"));
        if (file is null)
        {
            return;
        }

        var result = await _contentPacks
            .ImportAsync(SelectedInstance.Id, SelectedPackType.Kind, file)
            .ConfigureAwait(true);
        StatusMessage = result.IsSuccess
            ? _localizer.Get("mods.packImported", result.Value!.FileName)
            : result.Error?.ToString() ?? _localizer.Get("mods.packImportFailed");
        await RefreshPacksAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeletePackAsync()
    {
        if (SelectedInstance is null || SelectedPackType is null || SelectedPack is null)
        {
            StatusMessage = _localizer.Get("mods.selectPack");
            return;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("mods.packDeleteTitle"),
                _localizer.Get("mods.packDeleteConfirm", SelectedPack.FileName)))
        {
            return;
        }

        var result = await _contentPacks
            .DeleteAsync(SelectedInstance.Id, SelectedPackType.Kind, SelectedPack.FileName)
            .ConfigureAwait(true);
        StatusMessage = result.IsSuccess
            ? _localizer.Get("mods.packDeleted", SelectedPack.FileName)
            : result.Error?.ToString() ?? _localizer.Get("mods.packDeleteFailed");
        await RefreshPacksAsync().ConfigureAwait(true);
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
            await RefreshPacksAsync().ConfigureAwait(true);
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
        if (_initialized && !_suppressSelectionRefresh)
        {
            _ = RefreshAsync();
            _ = RefreshPacksAsync();
        }
    }

    partial void OnSelectedPackTypeChanged(PackTypeChoice? value)
    {
        if (_initialized)
        {
            _ = RefreshPacksAsync();
        }
    }
}

public sealed record ModInstanceChoice(string Id, string Name);

public sealed record PackTypeChoice(ContentPackKind Kind, string Title);

public sealed class ContentPackItemViewModel
{
    private readonly ContentPackEntry _entry;

    public ContentPackItemViewModel(ContentPackEntry entry)
    {
        _entry = entry;
    }

    public string FileName => _entry.FileName;

    public string Name => _entry.Name;

    public string Meta => _entry.PackFormat is null
        ? _entry.FileName
        : $"{_entry.FileName} · pack_format {_entry.PackFormat}";
}

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
