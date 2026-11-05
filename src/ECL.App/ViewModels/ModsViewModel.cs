// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Mods;

namespace Ecl.App.ViewModels;

/// <summary>
/// 一个实例工作区自有的模组与资源包页面状态（B2 步骤 1c、3b-3）。
///
/// 本对象在创建时绑定唯一稳定 <see cref="InstanceId"/>，所有命令只作用于该实例，
/// 不再依赖共享的“当前选中实例”：两个实例工作区同时打开时，各自的模组列表、
/// 搜索/筛选与状态文案互不影响。关闭工作区时由页面释放。
/// </summary>
public sealed partial class ModsViewModel : ObservableObject, IDisposable
{
    private readonly IModService _mods;
    private readonly IModpackService _modpacks;
    private readonly IModDependencyInstaller _dependencies;
    private readonly IContentPackService _contentPacks;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;

    public Action<string, string>? SearchDependency { get; set; }

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];

    public ObservableCollection<ModItemViewModel> FilteredMods { get; } = [];

    public ObservableCollection<ModStatusFilterChoice> StatusFilters { get; } = [];

    public ObservableCollection<ModSortChoice> SortOrders { get; } = [];

    public ObservableCollection<PackTypeChoice> PackTypes { get; } = [];

    public ObservableCollection<ContentPackItemViewModel> Packs { get; } = [];

    [ObservableProperty]
    private ModItemViewModel? _selectedMod;

    [ObservableProperty]
    private string _modSearchText = string.Empty;

    [ObservableProperty]
    private string _appliedSearchText = string.Empty;

    [ObservableProperty]
    private bool _isSearchInvalid;

    [ObservableProperty]
    private ModStatusFilterChoice? _selectedStatusFilter;

    [ObservableProperty]
    private ModSortChoice? _selectedSortOrder;

    [ObservableProperty]
    private PackTypeChoice? _selectedPackType;

    [ObservableProperty]
    private ContentPackItemViewModel? _selectedPack;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    private string _emptyMessage = string.Empty;

    [ObservableProperty]
    private string _capabilityHint = string.Empty;

    [ObservableProperty]
    private bool _hasResults;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _isFilterActive;

    [ObservableProperty]
    private string _packSummary = string.Empty;

    [ObservableProperty]
    private string _packWarning = string.Empty;

    [ObservableProperty]
    private bool _packHasWarning;

    [ObservableProperty]
    private bool _isBusy;

    public ModsViewModel(
        IModService mods,
        IModpackService modpacks,
        IModDependencyInstaller dependencies,
        IContentPackService contentPacks,
        IDialogService dialogs,
        ILocalizer localizer,
        string instanceId,
        string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        _mods = mods;
        _modpacks = modpacks;
        _dependencies = dependencies;
        _contentPacks = contentPacks;
        _dialogs = dialogs;
        _localizer = localizer;
        InstanceId = instanceId;
        InstanceName = instanceName;

        SortOrders.Add(new ModSortChoice(ModSortOrder.NameAscending, localizer.Get("mods.sort.nameAsc")));
        SortOrders.Add(new ModSortChoice(ModSortOrder.NameDescending, localizer.Get("mods.sort.nameDesc")));
        SortOrders.Add(new ModSortChoice(ModSortOrder.TimeAscending, localizer.Get("mods.sort.timeAsc")));
        SortOrders.Add(new ModSortChoice(ModSortOrder.TimeDescending, localizer.Get("mods.sort.timeDesc")));
        _selectedSortOrder = SortOrders[0];

        StatusFilters.Add(new ModStatusFilterChoice(ModStatusFilter.Available, localizer.Get("mods.filter.available")));
        StatusFilters.Add(new ModStatusFilterChoice(ModStatusFilter.Disabled, localizer.Get("mods.filter.disabled")));
        StatusFilters.Add(new ModStatusFilterChoice(
            ModStatusFilter.Dangerous,
            localizer.Get("mods.filter.danger"),
            localizer.Get("mods.capability.dangerFilter")));
        StatusFilters.Add(new ModStatusFilterChoice(ModStatusFilter.Abnormal, localizer.Get("mods.filter.abnormal")));
        StatusFilters.Add(new ModStatusFilterChoice(
            ModStatusFilter.Updatable,
            localizer.Get("mods.filter.updatable"),
            localizer.Get("mods.capability.updateFilter")));

        PackTypes.Add(new PackTypeChoice(ContentPackKind.ResourcePack, localizer.Get("mods.resourcePacks")));
        PackTypes.Add(new PackTypeChoice(ContentPackKind.ShaderPack, localizer.Get("mods.shaderPacks")));
        _selectedPackType = PackTypes[0];

        EmptyMessage = localizer.Get("mods.empty.none");
        ResultSummary = localizer.Get("mods.count.total", 0);
    }

    /// <summary>本页面绑定的稳定实例 ID；模组、资源包与整合包命令都只作用于它。</summary>
    public string InstanceId { get; }

    /// <summary>实例显示名只用于导出文件名，不参与身份判断。</summary>
    public string InstanceName { get; }

    public bool IsDisposed { get; private set; }

    public string UpdateUnavailableHint => _localizer.Get("mods.tool.updateUnavailable");

    public Task InitializeAsync() => ReloadAsync();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ReloadAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
        await RefreshPacksAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _mods.ScanAsync(InstanceId).ConfigureAwait(true);
            if (IsDisposed)
            {
                // 扫描返回时页面可能已关闭：不得把结果写回已释放的页面状态。
                return;
            }

            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.ToString() ?? _localizer.Get("mods.scanFailed");
                return;
            }

            var report = result.Value!;
            var selectedFileName = SelectedMod?.FileName;
            foreach (var mod in Mods)
            {
                mod.PropertyChanged -= OnModPropertyChanged;
            }

            Mods.Clear();
            foreach (var mod in report.Mods)
            {
                var item = new ModItemViewModel(mod, _localizer);
                item.PropertyChanged += OnModPropertyChanged;
                Mods.Add(item);
            }

            SelectedMod = Mods.FirstOrDefault(mod => mod.FileName == selectedFileName);
            RefreshFilteredMods();
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

    /// <summary>
    /// 写操作后的重新扫描：扫描成功会清空上一条提示，因此保留命令刚写入的结果文案；
    /// 扫描本身失败时以扫描错误为准，不覆盖为成功文案。
    /// </summary>
    private async Task RefreshAndKeepStatusAsync()
    {
        var message = StatusMessage;
        await RefreshAsync().ConfigureAwait(true);
        if (StatusMessage.Length == 0)
        {
            StatusMessage = message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshPacksAsync()
    {
        if (IsDisposed || SelectedPackType is null)
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
                .ListAsync(InstanceId, SelectedPackType.Kind)
                .ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

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

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task TogglePackAsync()
    {
        if (IsDisposed || SelectedPackType is null || SelectedPack is null)
        {
            StatusMessage = _localizer.Get("mods.selectPack");
            return;
        }

        var enable = !SelectedPack.Enabled;
        var result = await _contentPacks
            .SetEnabledAsync(InstanceId, SelectedPackType.Kind, SelectedPack.FileName, enable)
            .ConfigureAwait(true);

        StatusMessage = result.IsSuccess
            ? _localizer.Get(enable ? "mods.packEnabled" : "mods.packDisabled", SelectedPack.FileName)
            : result.Error?.ToString() ?? _localizer.Get("mods.packToggleFailed");

        await RefreshPacksAsync().ConfigureAwait(true);
    }

    /// <summary>行内切换单个模组的启用状态（mods.md §5.5「禁用」与启用复选框共用）。</summary>
    [RelayCommand(CanExecute = nameof(CanActOnMod))]
    private async Task ToggleModAsync(ModItemViewModel? mod)
    {
        if (IsDisposed || mod is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _mods
                .SetEnabledAsync(InstanceId, mod.FileName, !mod.Enabled)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                mod.NotifyEnabledChanged();
                StatusMessage = result.Error?.ToString() ?? _localizer.Get("mods.updateFailed");
                return;
            }

            StatusMessage = _localizer.Get(
                mod.Enabled ? "mods.disabled" : "mods.enabled",
                mod.FileName);
            await RefreshAndKeepStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>行内删除：逐项确认后走领域服务，成功后重新扫描。</summary>
    [RelayCommand(CanExecute = nameof(CanActOnMod))]
    private async Task DeleteModAsync(ModItemViewModel? mod)
    {
        if (IsDisposed || mod is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("mods.deleteTitle"),
                _localizer.Get("mods.deleteConfirm", mod.FileName)))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _mods.DeleteAsync(InstanceId, mod.FileName).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.ToString() ?? _localizer.Get("mods.deleteFailed");
                return;
            }

            StatusMessage = _localizer.Get("mods.deleted", mod.FileName);
            await RefreshAndKeepStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 逐模组更新：领域没有“检查更新/下载新版本”的 API（HANDOFF §5.2），
    /// 命令保持不可用，不得用空实现伪装成功。
    /// </summary>
    [RelayCommand(CanExecute = nameof(NeverAvailable))]
    private void UpdateMod(ModItemViewModel? mod) => StatusMessage = UpdateUnavailableHint;

    [RelayCommand(CanExecute = nameof(NeverAvailable))]
    private void UpdateSelected() => StatusMessage = UpdateUnavailableHint;

    /// <summary>全选/反选：当前筛选结果全部选中时清空，否则全选当前结果。</summary>
    [RelayCommand(CanExecute = nameof(CanSelectAllOrInvert))]
    private void SelectAllOrInvert()
    {
        var visible = FilteredMods.ToArray();
        var allSelected = visible.Length > 0 && visible.All(mod => mod.IsSelected);
        foreach (var mod in visible)
        {
            mod.IsSelected = !allSelected;
        }

        UpdateSelectionState();
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task EnableSelectedAsync() => SetSelectedEnabledAsync(true);

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task DisableSelectedAsync() => SetSelectedEnabledAsync(false);

    /// <summary>旧版“全部启用/全部禁用”入口；B2 步骤 3b-3b 由“全选 + 启用/禁用所选”取代。</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task EnableAllAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _mods.SetAllEnabledAsync(InstanceId, true).ConfigureAwait(true);
            StatusMessage = result.IsSuccess
                ? _localizer.Get("mods.batchDone", result.Value!.Succeeded)
                : result.Error?.ToString() ?? _localizer.Get("mods.updateFailed");
            await RefreshAndKeepStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task DisableAllAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _mods.SetAllEnabledAsync(InstanceId, false).ConfigureAwait(true);
            StatusMessage = result.IsSuccess
                ? _localizer.Get("mods.batchDone", result.Value!.Succeeded)
                : result.Error?.ToString() ?? _localizer.Get("mods.updateFailed");
            await RefreshAndKeepStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearStatusFilter))]
    private void ClearStatusFilter() => SelectedStatusFilter = null;

    private bool CanClearStatusFilter() => !IsBusy && SelectedStatusFilter is not null;

    private async Task SetSelectedEnabledAsync(bool enabled)
    {
        if (IsDisposed)
        {
            return;
        }

        var targets = Mods.Where(mod => mod.IsSelected && mod.Enabled != enabled).ToArray();
        if (targets.Length == 0)
        {
            StatusMessage = _localizer.Get("mods.selectionNothingToDo");
            return;
        }

        IsBusy = true;
        var succeeded = 0;
        var failed = 0;
        try
        {
            foreach (var mod in targets)
            {
                var result = await _mods.SetEnabledAsync(InstanceId, mod.FileName, enabled).ConfigureAwait(true);
                if (result.IsSuccess)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        StatusMessage = _localizer.Get(
            enabled ? "mods.bulkEnabled" : "mods.bulkDisabled",
            succeeded,
            failed);
        await RefreshAndKeepStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task DownloadMissingDependenciesAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<ModDependencyProgress>(update => StatusMessage = update.Message);
            var result = await _dependencies
                .InstallMissingAsync(InstanceId, progress)
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.ToString() ?? _localizer.Get("mods.dependencies.failedMessage");
                return;
            }

            var report = result.Value!;
            StatusMessage = _localizer.Get(
                "mods.dependencies.done",
                report.Installed.Count,
                report.Unresolved.Count,
                report.Failed.Count);
            await RefreshAndKeepStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedMod))]
    private async Task EnableSelectedDependenciesAsync()
    {
        if (IsDisposed || SelectedMod is null)
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
            var result = await _mods.SetEnabledAsync(InstanceId, dependency.FileName, true).ConfigureAwait(true);
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
        await RefreshAndKeepStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedMod))]
    private void SearchSelectedDependency()
    {
        if (IsDisposed || SelectedMod is null)
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

        SearchDependency?.Invoke(dependencyId, InstanceId);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ImportModsAsync()
    {
        if (IsDisposed)
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

        IsBusy = true;
        try
        {
            var result = await _mods.ImportAsync(InstanceId, files).ConfigureAwait(true);
            StatusMessage = result.IsSuccess
                ? DescribeImport(result.Value!)
                : result.Error?.ToString() ?? _localizer.Get("mods.importFailed");
            await RefreshAndKeepStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string DescribeImport(ModBatchResult result)
    {
        var message = _localizer.Get("mods.importDone", result.Succeeded);
        return result.Errors.Count == 0
            ? message
            : $"{message} {string.Join(" ", result.Errors)}";
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ImportPackAsync()
    {
        if (IsDisposed || SelectedPackType is null)
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
            .ImportAsync(InstanceId, SelectedPackType.Kind, file)
            .ConfigureAwait(true);
        StatusMessage = result.IsSuccess
            ? _localizer.Get("mods.packImported", result.Value!.FileName)
            : result.Error?.ToString() ?? _localizer.Get("mods.packImportFailed");
        await RefreshPacksAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task DeletePackAsync()
    {
        if (IsDisposed || SelectedPackType is null || SelectedPack is null)
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
            .DeleteAsync(InstanceId, SelectedPackType.Kind, SelectedPack.FileName)
            .ConfigureAwait(true);
        StatusMessage = result.IsSuccess
            ? _localizer.Get("mods.packDeleted", SelectedPack.FileName)
            : result.Error?.ToString() ?? _localizer.Get("mods.packDeleteFailed");
        await RefreshPacksAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ImportModpackAsync()
    {
        if (IsDisposed)
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
                .ImportAsync(InstanceId, file, progress)
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
            await RefreshAndKeepStatusAsync().ConfigureAwait(true);
            await RefreshPacksAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ExportModpackAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        var output = _dialogs.PickSaveFile(
            _localizer.Get("mods.exportTitle"),
            _localizer.Get("mods.modpackFilter"),
            $"{InstanceName}.mrpack");
        if (output is null)
        {
            return;
        }

        var result = await _modpacks.ExportAsync(InstanceId, output).ConfigureAwait(true);
        StatusMessage = result.IsSuccess
            ? _localizer.Get("mods.exportDone", result.Value!.Files)
            : result.Error?.ToString() ?? _localizer.Get("mods.exportFailed");
    }

    /// <summary>搜索提交：只接受英文，空条件表示不按搜索词过滤（mods.md §4）。</summary>
    [RelayCommand]
    private void Search()
    {
        if (IsDisposed)
        {
            return;
        }

        var text = ModSearchText.Trim();
        if (text.Any(character => character > 0x7F))
        {
            IsSearchInvalid = true;
            StatusMessage = _localizer.Get("mods.search.invalidEnglish");
            return;
        }

        IsSearchInvalid = false;
        StatusMessage = string.Empty;
        AppliedSearchText = text;
        RefreshFilteredMods();
    }

    partial void OnModSearchTextChanged(string value)
    {
        if (value.All(character => character <= 0x7F))
        {
            IsSearchInvalid = false;
        }
    }

    partial void OnSelectedStatusFilterChanged(ModStatusFilterChoice? value)
    {
        IsFilterActive = value is not null;
        CapabilityHint = value?.CapabilityHint ?? string.Empty;
        ClearStatusFilterCommand.NotifyCanExecuteChanged();
        RefreshFilteredMods();
    }

    partial void OnSelectedModChanged(ModItemViewModel? value)
    {
        EnableSelectedDependenciesCommand.NotifyCanExecuteChanged();
        SearchSelectedDependencyCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSortOrderChanged(ModSortChoice? value) => RefreshFilteredMods();

    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();

    private void NotifyCommandStates()
    {
        ReloadCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        RefreshPacksCommand.NotifyCanExecuteChanged();
        ToggleModCommand.NotifyCanExecuteChanged();
        DeleteModCommand.NotifyCanExecuteChanged();
        EnableSelectedCommand.NotifyCanExecuteChanged();
        DisableSelectedCommand.NotifyCanExecuteChanged();
        SelectAllOrInvertCommand.NotifyCanExecuteChanged();
        ClearStatusFilterCommand.NotifyCanExecuteChanged();
        EnableAllCommand.NotifyCanExecuteChanged();
        DisableAllCommand.NotifyCanExecuteChanged();
        ImportModsCommand.NotifyCanExecuteChanged();
        ImportPackCommand.NotifyCanExecuteChanged();
        DeletePackCommand.NotifyCanExecuteChanged();
        ImportModpackCommand.NotifyCanExecuteChanged();
        ExportModpackCommand.NotifyCanExecuteChanged();
        DownloadMissingDependenciesCommand.NotifyCanExecuteChanged();
        EnableSelectedDependenciesCommand.NotifyCanExecuteChanged();
        SearchSelectedDependencyCommand.NotifyCanExecuteChanged();
        ClearStatusFilterCommand.NotifyCanExecuteChanged();
    }

    private bool CanRefresh() => !IsBusy;

    private bool CanActOnMod(ModItemViewModel? mod) => !IsBusy && mod is not null;

    private bool CanUseSelectedMod() => !IsBusy && SelectedMod is not null;

    private bool CanActOnSelection() => !IsBusy && HasSelection;

    private bool CanSelectAllOrInvert() => !IsBusy && FilteredMods.Count > 0;

    private static bool NeverAvailable() => false;

    private void OnModPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ModItemViewModel.IsSelected))
        {
            UpdateSelectionState();
        }
    }

    private void UpdateSelectionState()
    {
        var hasSelection = Mods.Any(mod => mod.IsSelected);
        if (hasSelection == HasSelection)
        {
            return;
        }

        HasSelection = hasSelection;
        EnableSelectedCommand.NotifyCanExecuteChanged();
        DisableSelectedCommand.NotifyCanExecuteChanged();
    }

    private void RefreshFilteredMods()
    {
        if (IsDisposed)
        {
            return;
        }

        var filter = SelectedStatusFilter?.Filter;
        var search = AppliedSearchText;
        var selectedFileName = SelectedMod?.FileName;

        var matches = Mods.Where(mod =>
            (search.Length == 0 || mod.MatchesSearch(search))
            && (filter is null || mod.MatchesFilter(filter.Value)));

        matches = Sort(matches, SelectedSortOrder?.Order ?? ModSortOrder.NameAscending);

        FilteredMods.Clear();
        foreach (var mod in matches)
        {
            FilteredMods.Add(mod);
        }

        HasResults = FilteredMods.Count > 0;
        EmptyMessage = Mods.Count == 0
            ? _localizer.Get("mods.empty.none")
            : _localizer.Get("mods.empty.filtered");
        ResultSummary = search.Length > 0 || filter is not null
            ? _localizer.Get("mods.count.results", FilteredMods.Count)
            : _localizer.Get("mods.count.total", FilteredMods.Count);

        SelectedMod = FilteredMods.FirstOrDefault(mod => mod.FileName == selectedFileName);
        SelectAllOrInvertCommand.NotifyCanExecuteChanged();
    }

    private static IEnumerable<ModItemViewModel> Sort(
        IEnumerable<ModItemViewModel> source,
        ModSortOrder order) => order switch
        {
            ModSortOrder.NameDescending => source
                .OrderByDescending(mod => mod.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mod => mod.FileName, StringComparer.Ordinal),
            ModSortOrder.TimeAscending => source
                .OrderBy(mod => mod.LastWriteTimeUtc ?? DateTime.MinValue)
                .ThenBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase),
            ModSortOrder.TimeDescending => source
                .OrderByDescending(mod => mod.LastWriteTimeUtc ?? DateTime.MinValue)
                .ThenBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase),
            _ => source
                .OrderBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mod => mod.FileName, StringComparer.Ordinal),
        };

    partial void OnSelectedPackTypeChanged(PackTypeChoice? value)
    {
        if (!IsDisposed)
        {
            _ = RefreshPacksAsync();
        }
    }

    /// <summary>关闭工作区时释放本页面持有的列表与选择状态。</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        foreach (var mod in Mods)
        {
            mod.PropertyChanged -= OnModPropertyChanged;
        }

        Mods.Clear();
        FilteredMods.Clear();
        Packs.Clear();
        SelectedMod = null;
        SelectedPack = null;
    }
}

public sealed record PackTypeChoice(ContentPackKind Kind, string Title);

