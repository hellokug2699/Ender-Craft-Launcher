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
    private string? _pendingInstanceId;

    public Action<string, string>? SearchDependency { get; set; }

    public ObservableCollection<ModInstanceChoice> Instances { get; } = [];

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];

    public ObservableCollection<ModItemViewModel> FilteredMods { get; } = [];

    public ObservableCollection<ModFilterChoice> ModFilters { get; } = [];

    public ObservableCollection<PackTypeChoice> PackTypes { get; } = [];

    public ObservableCollection<ContentPackItemViewModel> Packs { get; } = [];

    [ObservableProperty]
    private ModInstanceChoice? _selectedInstance;

    [ObservableProperty]
    private ModItemViewModel? _selectedMod;

    [ObservableProperty]
    private string _modSearchText = string.Empty;

    [ObservableProperty]
    private ModFilterChoice? _selectedModFilter;

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
    private string _packWarning = string.Empty;

    [ObservableProperty]
    private bool _packHasWarning;

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

        ModFilters.Add(new ModFilterChoice("all", localizer.Get("mods.filter.all")));
        ModFilters.Add(new ModFilterChoice("enabled", localizer.Get("mods.filter.enabled")));
        ModFilters.Add(new ModFilterChoice("disabled", localizer.Get("mods.filter.disabled")));
        ModFilters.Add(new ModFilterChoice("issues", localizer.Get("mods.filter.issues")));
        _selectedModFilter = ModFilters[0];

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

    public async Task<string> GetHealthSummaryAsync(string instanceId)
    {
        var result = await _mods.ScanAsync(instanceId).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            return _localizer.Get("instances.healthModsFailed", result.Error?.Message ?? string.Empty);
        }

        var report = result.Value!;
        return report.IssueCount == 0
            ? _localizer.Get("instances.healthModsOk", report.EnabledCount, report.DisabledCount)
            : _localizer.Get("instances.healthModsIssues", report.EnabledCount, report.DisabledCount, report.IssueCount,
                string.Join(Environment.NewLine, report.Issues.Take(12).Select(issue => $"{issue.FileName}: {issue.Kind} ({issue.RelatedModId ?? issue.Detail ?? issue.ModId ?? string.Empty})")));
    }

    public void SelectInstance(string instanceId)
    {
        var match = Instances.FirstOrDefault(item => string.Equals(item.Id, instanceId, StringComparison.Ordinal));
        if (match is null)
        {
            _pendingInstanceId = instanceId;
            return;
        }

        _pendingInstanceId = null;
        SelectedInstance = match;
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
        var selectedId = _pendingInstanceId ?? SelectedInstance?.Id;
        var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);

        _suppressSelectionRefresh = true;
        try
        {
            Instances.Clear();
            foreach (var instance in instances)
            {
                Instances.Add(new ModInstanceChoice(
                    instance.Id,
                    instance.Name,
                    instance.GameVersion,
                    instance.Loader,
                    instance.LoaderVersion));
            }

            SelectedInstance = Instances.FirstOrDefault(item => item.Id == selectedId)
                               ?? Instances.FirstOrDefault();
            _pendingInstanceId = null;
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
            RefreshFilteredMods();
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

            RefreshFilteredMods();
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
            PackWarning = string.Empty;
            PackHasWarning = false;
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

            var list = result.Value!;
            var selectedFileName = SelectedPack?.FileName;
            Packs.Clear();
            foreach (var pack in list.Packs)
            {
                Packs.Add(new ContentPackItemViewModel(pack, _localizer));
            }

            SelectedPack = Packs.FirstOrDefault(pack => pack.FileName == selectedFileName);

            PackSummary = _localizer.Get("mods.packSummary", list.EnabledCount, list.Packs.Count);
            PackWarning = BuildPackWarning(list);
            PackHasWarning = PackWarning.Length > 0;
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private string BuildPackWarning(ContentPackList list)
    {
        var warnings = new List<string>();
        if (list.Status == PackOptionsStatus.Unreadable)
        {
            warnings.Add(_localizer.Get("mods.packWarningCorrupt"));
        }

        if (list.MissingEntries.Count > 0)
        {
            warnings.Add(_localizer.Get(
                "mods.packWarningMissing",
                list.MissingEntries.Count,
                SummarizeEntries(list.MissingEntries)));
        }

        if (list.UnmanagedEntries.Count > 0)
        {
            warnings.Add(_localizer.Get(
                "mods.packWarningUnmanaged",
                list.UnmanagedEntries.Count,
                SummarizeEntries(list.UnmanagedEntries)));
        }

        if (list.DuplicateEntries.Count > 0)
        {
            warnings.Add(_localizer.Get(
                "mods.packWarningDuplicate",
                list.DuplicateEntries.Count,
                SummarizeEntries(list.DuplicateEntries)));
        }

        return string.Join(" ", warnings);
    }

    private static string SummarizeEntries(IReadOnlyList<string> entries)
    {
        const int limit = 3;
        var names = string.Join("、", entries.Take(limit));
        return entries.Count > limit ? $"{names} …" : names;
    }

    [RelayCommand]
    private async Task TogglePackAsync()
    {
        if (SelectedInstance is null || SelectedPackType is null || SelectedPack is null)
        {
            StatusMessage = _localizer.Get("mods.selectPack");
            return;
        }

        var enable = !SelectedPack.Enabled;
        var result = await _contentPacks
            .SetEnabledAsync(SelectedInstance.Id, SelectedPackType.Kind, SelectedPack.FileName, enable)
            .ConfigureAwait(true);

        StatusMessage = result.IsSuccess
            ? _localizer.Get(enable ? "mods.packEnabled" : "mods.packDisabled", SelectedPack.FileName)
            : result.Error?.ToString() ?? _localizer.Get("mods.packToggleFailed");

        await RefreshPacksAsync().ConfigureAwait(true);
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
    private async Task EnableSelectedDependenciesAsync()
    {
        if (SelectedInstance is null || SelectedMod is null)
        {
            StatusMessage = _localizer.Get("mods.selectMod");
            return;
        }

        var dependencyIds = SelectedMod.Issues
            .Where(issue => issue.Kind == ModIssueKind.DisabledDependency && !string.IsNullOrWhiteSpace(issue.RelatedModId))
            .Select(issue => issue.RelatedModId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dependencies = dependencyIds
            .Select(id => Mods.FirstOrDefault(mod => string.Equals(mod.ModId, id, StringComparison.OrdinalIgnoreCase)))
            .Where(mod => mod is not null && !mod.Enabled)
            .Cast<ModItemViewModel>()
            .ToArray();

        if (dependencies.Length == 0)
        {
            StatusMessage = _localizer.Get("mods.dependenciesNone");
            return;
        }

        var names = string.Join(", ", dependencies.Select(mod => mod.Name));
        if (!_dialogs.Confirm(
                _localizer.Get("mods.enableDependenciesTitle"),
                _localizer.Get("mods.enableDependenciesConfirm", names)))
        {
            return;
        }

        var enabled = 0;
        var errors = new List<string>();
        foreach (var dependency in dependencies)
        {
            var result = await _mods.SetEnabledAsync(SelectedInstance.Id, dependency.FileName, true).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                enabled++;
            }
            else
            {
                errors.Add($"{dependency.FileName}: {result.Error?.Message}");
            }
        }

        StatusMessage = errors.Count == 0
            ? _localizer.Get("mods.dependenciesEnabled", enabled)
            : _localizer.Get("mods.dependenciesEnabledWithErrors", enabled, string.Join("; ", errors));
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void SearchSelectedDependency()
    {
        if (SelectedInstance is null || SelectedMod is null)
        {
            StatusMessage = _localizer.Get("mods.selectMod");
            return;
        }

        var dependencyId = SelectedMod.Issues
            .Where(issue => issue.Kind == ModIssueKind.MissingDependency)
            .Select(issue => issue.RelatedModId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (dependencyId is null)
        {
            StatusMessage = _localizer.Get("mods.noSearchableDependency");
            return;
        }

        SearchDependency?.Invoke(dependencyId, SelectedInstance.Id);
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
            var progress = new Progress<ModpackProgress>(update => StatusMessage = update.Message);
            var result = await _modpacks
                .ImportAsync(SelectedInstance.Id, file, progress)
                .ConfigureAwait(true);
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

    partial void OnModSearchTextChanged(string value) => RefreshFilteredMods();

    partial void OnSelectedModFilterChanged(ModFilterChoice? value) => RefreshFilteredMods();

    private void RefreshFilteredMods()
    {
        var selectedFileName = SelectedMod?.FileName;
        var search = ModSearchText.Trim();
        var filter = SelectedModFilter?.Key ?? "all";

        var matches = Mods.Where(mod =>
        {
            var matchesSearch = search.Length == 0
                || mod.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || mod.FileName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || mod.Meta.Contains(search, StringComparison.OrdinalIgnoreCase);
            var matchesFilter = filter switch
            {
                "enabled" => mod.Enabled,
                "disabled" => !mod.Enabled,
                "issues" => mod.HasIssues,
                _ => true,
            };
            return matchesSearch && matchesFilter;
        }).ToArray();

        FilteredMods.Clear();
        foreach (var mod in matches)
        {
            FilteredMods.Add(mod);
        }

        SelectedMod = FilteredMods.FirstOrDefault(mod => mod.FileName == selectedFileName);
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

public sealed record ModInstanceChoice(string Id, string Name, string GameVersion, string Loader, string? LoaderVersion)
{
    public string LoaderDisplay => Loader.ToLowerInvariant() switch
    {
        "vanilla" => "Vanilla",
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "cleanroom" => "Cleanroom",
        _ => Loader,
    };

    public string LoaderVersionDisplay => string.IsNullOrWhiteSpace(LoaderVersion)
        ? LoaderDisplay
        : $"{LoaderDisplay} {LoaderVersion}";
}

public sealed record PackTypeChoice(ContentPackKind Kind, string Title);

public sealed record ModFilterChoice(string Key, string Title);

public sealed class ContentPackItemViewModel
{
    private readonly ContentPackEntry _entry;
    private readonly ILocalizer _localizer;

    public ContentPackItemViewModel(ContentPackEntry entry, ILocalizer localizer)
    {
        _entry = entry;
        _localizer = localizer;
    }

    public string FileName => _entry.FileName;

    public bool Enabled => _entry.Enabled;

    public string Name => _entry.Name;

    public string StatusText
    {
        get
        {
            var state = _localizer.Get(Enabled ? "mods.packEnabledLabel" : "mods.packDisabledLabel");
            return _entry.Incompatible
                ? $"{state} · {_localizer.Get("mods.packIncompatibleLabel")}"
                : state;
        }
    }

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

    public string? ModId => _entry.Metadata?.ModId;

    public string PlatformText => _entry.Metadata?.Platform switch
    {
        ModPlatform.Fabric => _localizer.Get("mods.platform.fabric"),
        ModPlatform.Forge => _localizer.Get("mods.platform.forge"),
        ModPlatform.NeoForge => _localizer.Get("mods.platform.neoforge"),
        ModPlatform.LegacyForge => _localizer.Get("mods.platform.legacyForge"),
        _ => _localizer.Get("mods.platform.unknown"),
    };

    public IReadOnlyList<ModIssue> Issues => _entry.Issues;

    public bool Enabled => _entry.Enabled;

    public string RecommendationText => string.Join(Environment.NewLine, _entry.Issues.Select(Recommend));

    public string StatusText => Enabled
        ? _localizer.Get("mods.enabledLabel")
        : _localizer.Get("mods.disabledLabel");

    public string Name => _entry.Metadata?.Name ?? _entry.FileName;

    public string Meta => _entry.Metadata is null
        ? $"{_entry.FileName} · {PlatformText}"
        : $"{_entry.Metadata.ModId} · {_entry.Metadata.Version} · {PlatformText}";

    public string IssuesText => string.Join(
        Environment.NewLine,
        _entry.Issues.Select(issue => Describe(issue)));

    public bool HasIssues => _entry.Issues.Count > 0;

    private string Recommend(ModIssue issue) => issue.Kind switch
    {
        ModIssueKind.DisabledDependency => _localizer.Get("mod.recommend.disabledDependency", issue.RelatedModId ?? "?"),
        ModIssueKind.MissingDependency => _localizer.Get("mod.recommend.missingDependency", issue.RelatedModId ?? "?"),
        ModIssueKind.Conflict => _localizer.Get("mod.recommend.conflict", issue.RelatedModId ?? "?"),
        ModIssueKind.LoaderMismatch => _localizer.Get("mod.recommend.loaderMismatch", issue.Detail ?? "?"),
        ModIssueKind.InvalidMetadata => _localizer.Get("mod.recommend.invalidMetadata"),
        ModIssueKind.DuplicateModId => _localizer.Get("mod.recommend.duplicate"),
        _ => string.Empty,
    };

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
