// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Additions;
using Ecl.App.Services;
using Ecl.Core;
using Ecl.Core.Localization;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 设置中心「扩展管理」页（页面规格 <c>settings-additions.md</c>）。
///
/// 页面只做呈现与确认，扩展生命周期仍由 <see cref="IAdditionsService"/> 背后的
/// <c>AdditionStateStore</c> / <c>AdditionInstaller</c> / <c>AdditionPlanner</c> 承担：
/// - 已安装列表读取真实安装记录；名称与用途只在官方目录提供条目时才有来源，否则只显示扩展 ID；
/// - 状态只用能准确判断的四种：已启用 / 已禁用 / 依赖缺失 / Core API 不兼容；
/// - 启用前检查必需依赖与 Core API 范围，禁用不删除任何文件或用户数据；
/// - 安装与更新先展示计划、再次确认后执行，不使用假的进度或成功路径；
/// - Markdown 规格要求的“声明能力 / 宿主授予能力 / 权限撤销 / 沙箱隔离”属于 ADR 0053 与
///   ADR 0054，实现均未开始，因此本页如实列出这些缺失，不显示权限清单、不提供“完全信任”开关。
///
/// 进入页面只读取本地安装记录（无网络、无扫描）；官方目录只在用户点「检查目录更新」时读取。
/// </summary>
public sealed partial class SettingsAdditionsPageViewModel : ObservableObject, ISettingsPage
{
    private readonly IAdditionsService _additions;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly AdditionPageText _text;

    private readonly List<InstalledAdditionRowViewModel> _allInstalled = [];
    private readonly List<AvailableAdditionRowViewModel> _allAvailable = [];
    private readonly List<AdditionStatusFilter> _statusFilters;

    private bool _activated;
    private AdditionCatalogSnapshot? _catalog;
    private EclError? _catalogError;
    private AdditionInstallPlan? _currentPlan;

    public SettingsAdditionsPageViewModel(
        IAdditionsService additions,
        IDialogService dialogs,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(localizer);

        _additions = additions;
        _dialogs = dialogs;
        _localizer = localizer;
        _text = new AdditionPageText(localizer);

        _statusFilters =
        [
            new(AdditionStatusFilterValues.All, _text.Get("filter.all")),
            new(AdditionStatusFilterValues.Enabled, _text.Get("filter.enabled")),
            new(AdditionStatusFilterValues.Disabled, _text.Get("filter.disabled")),
            new(AdditionStatusFilterValues.MissingDependency, _text.Get("filter.missingDependency")),
            new(AdditionStatusFilterValues.Incompatible, _text.Get("filter.incompatible")),
            new(AdditionStatusFilterValues.UpdateAvailable, _text.Get("filter.updateAvailable")),
        ];
        _selectedStatusFilter = _statusFilters[0];

        Boundaries = new SettingsUnavailableSectionViewModel(
            _text.Get("boundaries.title"),
            [
                Boundary("permission"),
                Boundary("sandbox"),
                Boundary("registry"),
                Boundary("publisher"),
            ]);

        _catalogStatus = _text.Get("catalog.status.notRead");
    }

    public string Key => SettingsPageKeys.Additions;

    public string Title => _localizer.Get("settings.nav.additions");

    public string Subtitle => _text.Get("subtitle");

    /// <summary>本页没有元数据驱动的设置项；设置搜索不出现扩展列表项。</summary>
    public IReadOnlyList<SettingsSearchEntry> SearchEntries => [];

    /// <summary>扩展列表与安装记录不是元数据默认值，不提供“恢复本分类默认值”。</summary>
    public bool CanResetDefaults => false;

    /// <summary>当前筛选条件下的已安装扩展（视图直接绑定它）。</summary>
    public ObservableCollection<InstalledAdditionRowViewModel> Installed { get; } = [];

    /// <summary>当前筛选条件下的可安装扩展（来自通过签名验证的官方目录）。</summary>
    public ObservableCollection<AvailableAdditionRowViewModel> Available { get; } = [];

    public IReadOnlyList<AdditionStatusFilter> StatusFilters => _statusFilters;

    public SettingsUnavailableSectionViewModel Boundaries { get; }

    /// <summary>安装/更新的授权门槛说明：ADR 0053 的能力授权与沙箱能力尚未实现。</summary>
    public string AuthorizationNote => _text.Get("authorization.note");

    public string NameSourceNote => _text.Get("catalog.nameSource");

    [ObservableProperty]
    private AdditionStatusFilter _selectedStatusFilter;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _catalogStatus;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isStateCorrupt;

    [ObservableProperty]
    private string _stateError = string.Empty;

    [ObservableProperty]
    private string _installedCountText = string.Empty;

    [ObservableProperty]
    private string _availableCountText = string.Empty;

    [ObservableProperty]
    private AdditionPlanViewModel? _plan;

    /// <summary>已安装记录是否来自可读状态文件；损坏时页面保持只读并显示真实原因。</summary>
    public bool HasInstalled => _allInstalled.Count > 0;

    public bool HasAvailable => _allAvailable.Count > 0;

    public bool HasVisibleInstalled => Installed.Count > 0;

    public bool HasVisibleAvailable => Available.Count > 0;

    /// <summary>有安装记录但被当前搜索/筛选条件全部排除。</summary>
    public bool IsInstalledFilteredEmpty => HasInstalled && !HasVisibleInstalled;

    public bool HasPlan => Plan is not null;

    public string FilterLabel => _text.Get("filter.label");

    public string SearchPlaceholder => _text.Get("search.placeholder");

    public string ClearSearchText => _text.Get("search.clear");

    public string RefreshText => _text.Get("refresh");
    public string CheckCatalogText => _text.Get("catalog.check");

    public bool HasFilterText => FilterText.Length > 0;

    public string InstalledSectionTitle => _text.Get("installed.section");
    public string AvailableSectionTitle => _text.Get("catalog.section");
    public string InstalledEmptyMessage => _text.Get("installed.empty");
    public string AvailableEmptyMessage => _text.Get("catalog.empty");
    public string InstalledFilteredEmptyMessage => _text.Get("list.filteredEmpty");
    public string StateCorruptMessage => _text.Get("corrupt.reason");

    /// <summary>本页没有元数据设置项，设置搜索不会定位到页面内的行；返回 <c>false</c>。</summary>
    public bool Highlight(string optionKey) => false;

    /// <summary>
    /// 扩展列表与安装记录不是元数据默认值：这里不做任何事，也不提供会清空扩展登记的按钮
    /// （<see cref="CanResetDefaults"/> 为 false，设置中心不会显示“恢复本分类默认值”）。
    /// </summary>
    public void ResetToDefaults()
    {
    }

    /// <summary>进入页面只读本地安装记录；官方目录需要用户显式检查。</summary>
    public void Activate()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        ReloadInstalled();
    }

    /// <summary>只重新读取本地安装记录，不重新请求目录。</summary>
    [RelayCommand]
    private void Refresh()
    {
        ReloadInstalled();
        StatusMessage = _text.Get("status.refreshed");
    }

    /// <summary>读取官方扩展目录（显式操作）；失败时保留上一次成功读取的结果并显示真实原因。</summary>
    [RelayCommand]
    private async Task CheckCatalogAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var snapshot = await _additions.GetCatalogAsync(refresh: true).ConfigureAwait(true);
            if (!snapshot.IsSuccess)
            {
                _catalogError = snapshot.Error;
                CatalogStatus = Describe(snapshot.Error);
                return;
            }

            _catalog = snapshot.Value!;
            _catalogError = null;
            var warnings = string.Join(' ', _catalog.Warnings);
            CatalogStatus = _catalog.FromCache
                ? _text.Get("catalog.status.cached", warnings).TrimEnd()
                : _text.Get("catalog.status.verified", warnings).TrimEnd();
            ReloadInstalled();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>为单个扩展生成安装/更新计划；目标已安装且无需更新时给出准确提示。</summary>
    [RelayCommand]
    private async Task PlanAsync(string? id)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(id) || !AdditionId.TryParse(id, out var target))
        {
            return;
        }

        if (_catalog is null)
        {
            StatusMessage = _catalogError is null
                ? _text.Get("status.planNeedsCatalog")
                : Describe(_catalogError);
            return;
        }

        IsBusy = true;
        try
        {
            var plan = await _additions.CreatePlanAsync([target]).ConfigureAwait(true);
            if (!plan.IsSuccess)
            {
                Plan = null;
                _currentPlan = null;
                RaisePlanChanged();
                StatusMessage = Describe(plan.Error);
                return;
            }

            if (plan.Value!.IsEmpty)
            {
                Plan = null;
                _currentPlan = null;
                RaisePlanChanged();
                StatusMessage = _text.Get("status.alreadyCurrent", id);
                return;
            }

            _currentPlan = plan.Value;
            Plan = BuildPlan(plan.Value);
            RaisePlanChanged();
            StatusMessage = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelPlan()
    {
        _currentPlan = null;
        Plan = null;
        RaisePlanChanged();
        StatusMessage = string.Empty;
    }

    /// <summary>
    /// 确认并执行安装计划。确认弹窗与 <c>settings-additions.md</c> §4 的“查看计划 → 确认”两阶段一致；
    /// ADR 0053 的逐项能力授权弹窗尚未实现，因此这里不出现“允许并安装”措辞，也不宣称已获得授权。
    /// </summary>
    [RelayCommand]
    private async Task ConfirmPlanAsync()
    {
        if (IsBusy || _currentPlan is not { } plan)
        {
            return;
        }

        var names = string.Join(
            ", ",
            plan.All.Select(item => item.Entry.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
        var accepted = _dialogs.Confirm(
            _text.Get("plan.confirmTitle"),
            _text.Get("plan.confirmMessage", plan.Install.Count, plan.Update.Count, names),
            acknowledgementToken: null);
        if (!accepted)
        {
            StatusMessage = _text.Get("status.cancelled");
            return;
        }

        IsBusy = true;
        try
        {
            var applied = await _additions.ApplyPlanAsync(plan).ConfigureAwait(true);
            if (!applied.IsSuccess)
            {
                StatusMessage = Describe(applied.Error);
                return;
            }

            _currentPlan = null;
            Plan = null;
            RaisePlanChanged();
            ReloadInstalled();
            StatusMessage = _text.Get("status.installed", plan.All.Count);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(InstalledAdditionRowViewModel? row)
    {
        if (IsBusy || row is null)
        {
            return;
        }

        var enable = !row.IsEnabled;
        if (enable && !row.CanToggle)
        {
            // 启用前的依赖/Core API 检查不通过：不写入任何状态，只报告真实原因。
            StatusMessage = row.EnableBlockedReason;
            return;
        }

        if (row.IsReadOnly)
        {
            // 状态文件损坏时整页只读：不写入任何扩展状态。
            StatusMessage = row.EnableBlockedReason;
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _additions.SetEnabledAsync(row.Id, enable).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = Describe(result.Error);
                return;
            }

            ReloadInstalled();
            StatusMessage = enable
                ? _text.Get("status.enabled", row.Name)
                : _text.Get("status.disabled", row.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(InstalledAdditionRowViewModel? row)
    {
        if (IsBusy || row is null)
        {
            return;
        }

        if (!row.CanRemove)
        {
            StatusMessage = row.RemoveBlockedReason;
            return;
        }

        var accepted = _dialogs.Confirm(
            _text.Get("remove.title"),
            _text.Get("remove.message", row.Name, row.VersionText, row.IdText),
            acknowledgementToken: null);
        if (!accepted)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _additions.UninstallAsync(row.Id).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = Describe(result.Error);
                return;
            }

            ReloadInstalled();
            StatusMessage = _text.Get("status.removed", row.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RollbackAsync(InstalledAdditionRowViewModel? row)
    {
        if (IsBusy || row is null || row.Record.PreviousVersion is not { } previous)
        {
            return;
        }

        if (!row.CanRollback)
        {
            // 状态文件损坏或没有上一版本：不写入任何状态。
            return;
        }

        var accepted = _dialogs.Confirm(
            _text.Get("rollback.title"),
            _text.Get("rollback.message", row.Name, row.VersionText, previous.ToString()),
            acknowledgementToken: null);
        if (!accepted)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _additions.RollbackAsync(row.Id).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = Describe(result.Error);
                return;
            }

            ReloadInstalled();
            StatusMessage = _text.Get("status.rolledBack", row.Name, previous.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearSearch() => FilterText = string.Empty;

    partial void OnSelectedStatusFilterChanged(AdditionStatusFilter value) => ApplyFilters();

    partial void OnFilterTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasFilterText));
        ApplyFilters();
    }

    private void ReloadInstalled()
    {
        var load = _additions.LoadState();
        IsStateCorrupt = load.IsCorrupt;
        StateError = load.IsCorrupt ? load.Error ?? string.Empty : string.Empty;

        var records = load.Document.Additions
            .OrderBy(record => record.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var installedIds = records.Select(record => record.Id).ToHashSet();

        _allInstalled.Clear();
        foreach (var record in records)
        {
            _allInstalled.Add(BuildInstalledRow(record, records, installedIds));
        }

        InstalledCountText = _text.Get("installed.count", _allInstalled.Count);
        _allAvailable.Clear();
        if (_catalog is not null)
        {
            foreach (var entry in _catalog.Catalog.Additions
                         .Where(entry => !entry.Withdrawn && !installedIds.Contains(entry.Id))
                         .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(entry => entry.Id.Value, StringComparer.Ordinal))
            {
                _allAvailable.Add(BuildAvailableRow(entry));
            }
        }

        AvailableCountText = _text.Get("catalog.available.count", _allAvailable.Count);
        if (_catalog is not null && _allAvailable.Count == 0 && _catalogError is null)
        {
            CatalogStatus = _text.Get("catalog.status.none");
        }

        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(HasAvailable));
        ApplyFilters();
    }

    private InstalledAdditionRowViewModel BuildInstalledRow(
        InstalledAddition record,
        IReadOnlyList<InstalledAddition> all,
        HashSet<AdditionId> installedIds)
    {
        var missing = record.Dependencies
            .Where(dependency => !installedIds.Contains(dependency))
            .Select(dependency => dependency.Value)
            .ToArray();
        var dependents = all
            .Where(other => other.Id != record.Id && other.Dependencies.Contains(record.Id))
            .Select(other => other.Id.Value)
            .ToArray();

        var catalogEntry = _catalog?.Catalog.Additions.FirstOrDefault(
            entry => entry.Id == record.Id && !entry.Withdrawn);
        var availableVersion = catalogEntry?.Version;
        var hasUpdate = availableVersion is not null && availableVersion.CompareTo(record.Version) != 0;
        var incompatible = IsCoreApiIncompatible(record);

        var state = missing.Length > 0
            ? AdditionRowState.MissingDependency
            : incompatible
                ? AdditionRowState.Incompatible
                : record.Enabled
                    ? AdditionRowState.Enabled
                    : AdditionRowState.Disabled;

        var primaryStatus = record.Enabled
            ? _text.Get("state.enabled")
            : _text.Get("state.disabled");
        var secondaryStatus = missing.Length > 0
            ? _text.Get("state.missingDependency")
            : incompatible
                ? _text.Get("state.incompatible")
                : string.Empty;

        var enableBlocked = IsStateCorrupt
            ? _text.Get("blocked.corrupt")
            : missing.Length > 0
                ? _text.Get("blocked.missingDependency", string.Join(", ", missing))
                : incompatible
                    ? _text.Get("blocked.incompatible", record.CoreApiMin, record.CoreApiMax, AdditionCoreApi.Version)
                    : string.Empty;

        var removeBlocked = IsStateCorrupt
            ? _text.Get("blocked.corrupt")
            : dependents.Length > 0
                ? _text.Get("blocked.dependents", string.Join(", ", dependents))
                : string.Empty;

        return new InstalledAdditionRowViewModel(
            record,
            state,
            primaryStatus,
            secondaryStatus,
            catalogEntry is { Name.Length: > 0 } entry ? entry.Name : record.Id.Value,
            catalogEntry?.Description ?? string.Empty,
            record.Type == AdditionType.Feature
                ? _text.Get("row.type.feature")
                : _text.Get("row.type.data"),
            record.Version.ToString(),
            // 只显示安装日期：列宽有限，完整时间戳没有额外决策价值，也不放在 ToolTip 之外。
            _text.Get("row.installedAt", record.InstalledAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            missing.Length > 0 ? _text.Get("state.detail.missingDependency", string.Join(", ", missing)) : string.Empty,
            incompatible
                ? _text.Get("state.detail.incompatible", record.CoreApiMin, record.CoreApiMax, AdditionCoreApi.Version)
                : string.Empty,
            hasUpdate ? _text.Get("row.update", availableVersion) : string.Empty,
            record.PreviousVersion is { } previous ? _text.Get("row.previous", previous) : string.Empty,
            record.Enabled ? _text.Get("action.toggle.disable") : _text.Get("action.toggle.enable"),
            enableBlocked,
            removeBlocked,
            isReadOnly: IsStateCorrupt);
    }

    private AvailableAdditionRowViewModel BuildAvailableRow(AdditionEntry entry)
    {
        var required = entry.Dependencies.Where(dependency => !dependency.Optional).ToArray();
        return new AvailableAdditionRowViewModel(
            entry,
            entry.Version.ToString(),
            StorageSizeFormatter.FormatBytes(entry.Package.Size),
            required.Length == 0
                ? _text.Get("catalog.dependencies.none")
                : _text.Get(
                    "catalog.dependencies.list",
                    string.Join(
                        ", ",
                        required.Select(dependency =>
                            $"{dependency.Id.Value} {dependency.MinVersion}" +
                            (dependency.MaxVersion is null ? "+" : $"-{dependency.MaxVersion}")))),
            _text.Get("catalog.compatibility", entry.CoreApi.Min, entry.CoreApi.Max, AdditionCoreApi.Version),
            entry.Platforms.Count == 0
                ? _text.Get("catalog.platforms.all")
                : _text.Get("catalog.platforms.list", string.Join(", ", entry.Platforms)));
    }

    private AdditionPlanViewModel BuildPlan(AdditionInstallPlan plan)
    {
        var items = new List<AdditionPlanItemViewModel>();
        foreach (var item in plan.Install)
        {
            items.Add(PlanItem("plan.kind.install", item.Entry));
        }

        foreach (var item in plan.Update)
        {
            items.Add(PlanItem("plan.kind.update", item.Entry));
        }

        return new AdditionPlanViewModel(
            items,
            plan.Warnings,
            _text.Get("plan.summary", plan.Install.Count, plan.Update.Count),
            _text.Get("plan.restart"));

        AdditionPlanItemViewModel PlanItem(string kindKey, AdditionEntry entry) => new(
            _text.Get(kindKey),
            string.IsNullOrWhiteSpace(entry.Name) ? entry.Id.Value : entry.Name,
            entry.Version.ToString(),
            StorageSizeFormatter.FormatBytes(entry.Package.Size),
            entry.Dependencies.Count == 0
                ? _text.Get("catalog.dependencies.none")
                : string.Join(
                    ", ",
                    entry.Dependencies.Select(dependency =>
                        dependency.Id.Value + (dependency.Optional ? _text.Get("plan.optional") : string.Empty))),
            _text.Get("catalog.compatibility", entry.CoreApi.Min, entry.CoreApi.Max, AdditionCoreApi.Version));
    }

    private void ApplyFilters()
    {
        // 搜索与状态筛选只作用于“已安装”表格：控件位于该区块内，官方目录表格始终显示完整清单，
        // 避免出现筛选条件与可见列表不一致的错觉。
        var query = FilterText.Trim();
        Installed.Clear();
        foreach (var row in _allInstalled.Where(row => Matches(row, query, SelectedStatusFilter)))
        {
            Installed.Add(row);
        }

        Available.Clear();
        foreach (var row in _allAvailable)
        {
            Available.Add(row);
        }

        OnPropertyChanged(nameof(HasVisibleInstalled));
        OnPropertyChanged(nameof(HasVisibleAvailable));
        OnPropertyChanged(nameof(IsInstalledFilteredEmpty));

        // 有搜索或状态筛选时同时显示总数与当前可见数量，避免“4 个扩展”与实际只有 1 行矛盾。
        var hasActiveFilter =
            FilterText.Length > 0 ||
            !string.Equals(SelectedStatusFilter.Value, AdditionStatusFilterValues.All, StringComparison.Ordinal);
        InstalledCountText = hasActiveFilter
            ? _text.Get("installed.countFiltered", _allInstalled.Count, Installed.Count)
            : _text.Get("installed.count", _allInstalled.Count);
    }

    private static bool Matches(InstalledAdditionRowViewModel row, string query, AdditionStatusFilter filter)
    {
        if (query.Length > 0 &&
            !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !row.IdText.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !row.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return filter.Value switch
        {
            AdditionStatusFilterValues.Enabled => row.IsEnabled,
            AdditionStatusFilterValues.Disabled => !row.IsEnabled,
            AdditionStatusFilterValues.MissingDependency => row.State == AdditionRowState.MissingDependency,
            AdditionStatusFilterValues.Incompatible => row.State == AdditionRowState.Incompatible,
            // “可更新”只在目录给出不同版本时成立；没有目录时不会有命中。
            AdditionStatusFilterValues.UpdateAvailable => row.CanUpdate,
            _ => true,
        };
    }

    private static bool IsCoreApiIncompatible(InstalledAddition record)
    {
        if (record.CoreApiMin is not { } min || record.CoreApiMax is not { } max)
        {
            // 安装记录没有声明范围时不猜测；加载时会由运行时再次复核。
            return false;
        }

        var current = AdditionCoreApi.Version;
        return current.CompareTo(min) < 0 || current.CompareTo(max) > 0;
    }

    private void RaisePlanChanged()
    {
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(HasPlan));
    }

    private SettingsUnavailableItemViewModel Boundary(string name) => new(
        _text.Get($"boundary.{name}.title"),
        _text.Get($"boundary.{name}.reason"));

    private string Describe(EclError? error)
    {
        if (error is null)
        {
            return _text.Get("error.unknown");
        }

        var key = $"settings.page.additions.error.{error.Code}";
        var localized = _localizer.Get(key);
        return string.Equals(localized, key, StringComparison.Ordinal) ? error.Message : localized;
    }
}
