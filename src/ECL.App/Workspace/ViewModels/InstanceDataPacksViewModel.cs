// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.Core.Localization;
using Ecl.Minecraft.World;
using Ecl.World.Worlds;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 一个数据包条目的只读投影。数据只来自 <see cref="DatapackEntry"/>：
/// 启用状态来自世界 <c>level.dat</c> 的 <c>DataPacks</c>，名称取文件名，简介与格式来自
/// 包内 <c>pack.mcmeta</c>；读取失败只标记为损坏，不猜测内容。
/// </summary>
public sealed class DatapackRowViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private bool _isEnabled;

    public DatapackRowViewModel(DatapackEntry entry, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localizer);

        _localizer = localizer;
        Entry = entry;
        FileName = entry.FileName;
        Title = Path.GetFileNameWithoutExtension(entry.FileName);
        Description = entry.Description ?? string.Empty;
        VersionRangeText = BuildVersionRangeText(entry, localizer);
        _isEnabled = entry.Enabled;

        StatusKind = entry.ReadState switch
        {
            DatapackReadState.Corrupt => ContentPackStatusKind.Corrupt,
            DatapackReadState.Unrecognized => ContentPackStatusKind.Unrecognized,
            _ => ContentPackStatusKind.Active,
        };

        // 数据包没有“游戏记录为不兼容”的世界级数据，因此不产生黄色异常态；
        // 颜色只表达文件健康度与是否已启用（与资源库模板一致）。
        StatusText = localizer.Get(StatusKind switch
        {
            ContentPackStatusKind.Corrupt => "packs.status.corrupt",
            ContentPackStatusKind.Unrecognized => "packs.status.unrecognized",
            _ => entry.Enabled ? "packs.status.active" : "packs.status.normal",
        });

        StatusHint = localizer.Get(StatusKind switch
        {
            ContentPackStatusKind.Corrupt => "packs.status.corruptHint",
            ContentPackStatusKind.Unrecognized => "packs.status.unrecognizedHint",
            _ => entry.Enabled ? "packs.status.activeHint" : "packs.status.normalHint",
        });
    }

    public DatapackEntry Entry { get; }

    public string FileName { get; }

    /// <summary>名称：文件名去掉扩展名；数据包没有独立的显示名称字段。</summary>
    public string Title { get; }

    /// <summary>从 <c>pack.mcmeta</c> 提取的简介；没有简介时为空串，页面不显示占位。</summary>
    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    /// <summary>是否已启用；由页面在拖动/启用后同步，写盘只在「应用」时发生。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        internal set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(ToggleLabel));
            }
        }
    }

    /// <summary>启用/禁用按钮的文案；与跨表拖动等价，供键盘与辅助技术使用。</summary>
    public string ToggleLabel => _localizer.Get(IsEnabled ? "packs.toggle.disable" : "packs.toggle.enable");

    /// <summary>状态图标的视觉分类（与资源包页共用同一套矢量图标语义）。</summary>
    public ContentPackStatusKind StatusKind { get; }

    public string StatusText { get; }

    public string StatusHint { get; }

    /// <summary>
    /// 版本范围列：数据包的 <c>pack_format</c> 与资源包格式编号不是同一套对照，
    /// 因此只显示文件声明的格式范围，绝不套用资源包格式表；缺失时显示未知。
    /// </summary>
    public string VersionRangeText { get; }

    private static string BuildVersionRangeText(DatapackEntry entry, ILocalizer localizer)
    {
        if (entry.SupportedFormats is { } declared)
        {
            return localizer.Get(
                "packs.versionRange.formatRange",
                declared.MinInclusive,
                declared.MaxInclusive);
        }

        if (entry.PackFormat is { } format)
        {
            return localizer.Get("packs.versionRange.format", format);
        }

        return localizer.Get("packs.versionRange.unknown");
    }
}

/// <summary>
/// 实例工作区「内容 · 数据包」页面（B2 收尾，`resource-library-template.md` §5/§5.1）。
/// <para>
/// 数据包属于具体世界：进入页面先从 <see cref="IDatapackService.ListWorldsAsync"/> 的真实世界清单
/// 选择 World Target，再读取该世界 <c>datapacks</c> 与 <c>level.dat</c> 的启用状态。
/// 页面只持有待应用的工作副本；拖动、跨表移动与启用/禁用都不写盘，只有「应用」调用
/// <see cref="IDatapackService.ApplyAsync"/> 一次提交顺序与启用状态。删除按自身流程执行，
/// 文件与 level.dat 引用由领域在同一次事务中处理。
/// </para>
/// <para>
/// 页面不拼接世界路径或目标身份：所有读写都使用世界发现结果构造的
/// <see cref="DatapackTarget"/>，领域在每次写入前重新验证目标仍属于当前实例。
/// </para>
/// </summary>
public sealed partial class InstanceDataPacksViewModel : ObservableObject, IDisposable
{
    private readonly string _instanceId;
    private readonly IDatapackService _datapacks;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private List<string> _persistedEnabled = [];
    private DatapackWorldChoice? _loadedWorld;
    private bool _hasLoadedWorld;
    private bool _worldsLoaded;
    private int _worldGeneration;
    private int _listGeneration;

    public InstanceDataPacksViewModel(
        string instanceId,
        IDatapackService datapacks,
        IDialogService dialogs,
        ILocalizer localizer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(datapacks);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(localizer);

        _instanceId = instanceId;
        _datapacks = datapacks;
        _dialogs = dialogs;
        _localizer = localizer;

        EnabledPacks.CollectionChanged += (_, _) => OnCollectionsChanged();
        DisabledPacks.CollectionChanged += (_, _) => OnCollectionsChanged();
    }

    /// <summary>
    /// 由 Shell 注入的下载入口：带入实例 ID，跳到下载中心「数据包」页签。
    /// 为 <c>null</c> 时下载按钮不可用（避免伪造入口）。
    /// </summary>
    public Action<string>? OpenDatapackBrowser { get; set; }

    public string InstanceId => _instanceId;

    /// <summary>可供选择的世界；条目只来自 <see cref="IDatapackService.ListWorldsAsync"/>。</summary>
    public ObservableCollection<DatapackWorldChoice> Worlds { get; } = [];

    /// <summary>已启用表格（顺序即 level.dat 中的加载顺序）。</summary>
    public ObservableCollection<DatapackRowViewModel> EnabledPacks { get; } = [];

    /// <summary>未启用表格；顺序只影响本页面显示。</summary>
    public ObservableCollection<DatapackRowViewModel> DisabledPacks { get; } = [];

    public bool IsDisposed { get; private set; }

    [ObservableProperty]
    private DatapackWorldChoice? _selectedWorld;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoadingWorlds;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>世界或数据包读取的警告；非空时页面以弱提示展示。</summary>
    [ObservableProperty]
    private string _warningText = string.Empty;

    /// <summary>世界发现本身的警告（例如某个世界不可读被排除）。</summary>
    [ObservableProperty]
    private string _worldScanWarning = string.Empty;

    [ObservableProperty]
    private bool _hasPendingChanges;

    public bool CanApply => SelectedWorld is not null && HasPendingChanges && !IsBusy;

    public bool CanEdit => SelectedWorld is not null && !IsBusy;

    public bool CanOpenBrowser => OpenDatapackBrowser is not null;

    public bool IsWorldSelected => SelectedWorld is not null;

    public bool HasWorlds => Worlds.Count > 0;

    /// <summary>世界清单已读取、当前实例确实没有可用世界。</summary>
    public bool ShowNoWorlds => _worldsLoaded && !IsLoadingWorlds && Worlds.Count == 0;

    public string EnabledCountText => _localizer.Get("packs.count", EnabledPacks.Count);

    public string DisabledCountText => _localizer.Get("packs.count", DisabledPacks.Count);

    public bool HasEnabledPacks => EnabledPacks.Count > 0;

    public bool HasDisabledPacks => DisabledPacks.Count > 0;

    public bool HasAnyPacks => EnabledPacks.Count + DisabledPacks.Count > 0;

    public string KindTitle => _localizer.Get("instance.nav.dataPacks");

    public string EnabledSectionTitle => _localizer.Get("packs.section.enabled", KindTitle);

    public string DisabledSectionTitle => _localizer.Get("packs.section.disabled", KindTitle);

    public string AddButtonText => _localizer.Get("packs.add", KindTitle);

    public string DownloadButtonText => _localizer.Get("packs.download", KindTitle);

    public string EmptyEnabledMessage => SelectedWorld is null
        ? _localizer.Get("datapacks.empty.noWorld")
        : _localizer.Get("packs.empty.enabled", KindTitle);

    public string EmptyDisabledMessage => SelectedWorld is null
        ? _localizer.Get("datapacks.empty.noWorld")
        : _localizer.Get("packs.empty.disabled", KindTitle);

    public string WorldLabel => _localizer.Get("downloads.discovery.target.world");

    public string WorldHint => SelectedWorld is null
        ? _localizer.Get("datapacks.world.hint")
        : _localizer.Get(
            "datapacks.world.selected",
            SelectedWorld.Name,
            SelectedWorld.FolderName);

    public string NoWorldsText => _localizer.Get("datapacks.world.noWorlds");

    public string WorldLoadingText => _localizer.Get("datapacks.world.loading");

    public string DragHint => _localizer.Get("datapacks.dragHint");

    public string DisabledOrderHint => _localizer.Get("datapacks.disabledOrderHint");

    public string ApplyHint => _localizer.Get("datapacks.applyHint");

    public string DownloadHint => _localizer.Get("datapacks.downloadHint");

    /// <summary>首次进入页面时读取世界清单；不自动选择世界，必须先显式选择。</summary>
    public async Task InitializeAsync()
    {
        if (IsDisposed || _worldsLoaded)
        {
            return;
        }

        await LoadWorldsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsDisposed || IsBusy)
        {
            return;
        }

        if (HasPendingChanges && !_dialogs.Confirm(
                _localizer.Get("datapacks.refreshTitle"),
                _localizer.Get("datapacks.refreshDiscardConfirm")))
        {
            return;
        }

        StatusMessage = string.Empty;
        if (SelectedWorld is null)
        {
            await LoadWorldsAsync().ConfigureAwait(true);
            return;
        }

        await LoadDatapacksAsync(SelectedWorld).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddDatapackAsync()
    {
        if (IsDisposed || IsBusy || SelectedWorld is null)
        {
            return;
        }

        var file = _dialogs.PickOpenFile(
            _localizer.Get("datapacks.importTitle"),
            _localizer.Get("datapacks.importFilter"));
        if (file is null)
        {
            return;
        }

        var world = SelectedWorld;
        await RunAsync(async () =>
        {
            var result = await _datapacks.ImportAsync(_instanceId, world.Target, file).ConfigureAwait(true);
            StatusMessage = result.IsSuccess
                ? _localizer.Get("packs.imported", result.Value!.FileName)
                : result.Error?.ToString() ?? _localizer.Get("packs.importFailed");
            if (result.IsSuccess)
            {
                await LoadDatapacksAsync(world).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void DownloadDatapacks()
    {
        if (IsDisposed || OpenDatapackBrowser is null)
        {
            return;
        }

        OpenDatapackBrowser(_instanceId);
    }

    /// <summary>提交待应用的启用顺序；只有成功后才刷新为新的已保存状态。</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (IsDisposed || IsBusy || SelectedWorld is not { } world || !HasPendingChanges)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var ordered = EnabledPacks.Select(row => row.FileName).ToArray();
            var result = await _datapacks.ApplyAsync(_instanceId, world.Target, ordered).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                StatusMessage = _localizer.Get("datapacks.applied", ordered.Length, world.Name);
                await LoadDatapacksAsync(world).ConfigureAwait(true);
                return;
            }

            // 应用失败时保留用户的待应用更改，不静默丢弃选择。
            StatusMessage = result.Error?.ToString() ?? _localizer.Get("packs.applyFailed");
        }).ConfigureAwait(true);
    }

    /// <summary>删除按自身流程执行：确认后立即删除文件并清理 level.dat 引用，不做成待应用更改。</summary>
    [RelayCommand]
    private async Task DeleteDatapackAsync(DatapackRowViewModel? row)
    {
        if (IsDisposed || IsBusy || row is null || SelectedWorld is not { } world)
        {
            return;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("datapacks.deleteTitle"),
                _localizer.Get("datapacks.deleteConfirm", world.Name, row.FileName)))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _datapacks.DeleteAsync(_instanceId, world.Target, row.FileName)
                .ConfigureAwait(true);
            if (result.IsSuccess)
            {
                StatusMessage = _localizer.Get("packs.deleted", row.FileName);
                await LoadDatapacksAsync(world).ConfigureAwait(true);
                return;
            }

            // 删除失败时保留当前工作副本，不把未删除的文件从页面抹掉。
            StatusMessage = result.Error?.ToString() ?? _localizer.Get("packs.deleteFailed");
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 拖动/跨表移动的唯一条目：把 <paramref name="row"/> 放入目标表格的
    /// <paramref name="targetIndex"/>（超出范围时追加到末尾）。只改页面工作副本，不写盘。
    /// </summary>
    public void MovePack(DatapackRowViewModel row, bool toEnabled, int targetIndex)
    {
        if (IsDisposed || IsBusy || SelectedWorld is null || row is null)
        {
            return;
        }

        if (toEnabled)
        {
            DisabledPacks.Remove(row);
            if (EnabledPacks.Contains(row))
            {
                EnabledPacks.Remove(row);
            }

            EnabledPacks.Insert(Math.Clamp(targetIndex, 0, EnabledPacks.Count), row);
            row.IsEnabled = true;
        }
        else
        {
            EnabledPacks.Remove(row);
            if (DisabledPacks.Contains(row))
            {
                DisabledPacks.Remove(row);
            }

            DisabledPacks.Insert(Math.Clamp(targetIndex, 0, DisabledPacks.Count), row);
            row.IsEnabled = false;
        }

        RecomputePending();
    }

    /// <summary>
    /// 启用/禁用单个条目：与跨表拖动等价，只改页面工作副本，写盘仍由「应用」提交。
    /// 供键盘与辅助技术使用，避免只依赖鼠标拖动。
    /// </summary>
    [RelayCommand]
    private void TogglePack(DatapackRowViewModel? row)
    {
        if (IsDisposed || IsBusy || row is null)
        {
            return;
        }

        if (row.IsEnabled)
        {
            MovePack(row, toEnabled: false, 0);
        }
        else
        {
            MovePack(row, toEnabled: true, EnabledPacks.Count);
        }
    }

    /// <summary>同表内拖动排序；目标索引按移除后的列表计算。</summary>
    public void MoveWithin(DatapackRowViewModel row, bool enabledTable, int targetIndex)
    {
        var table = enabledTable ? EnabledPacks : DisabledPacks;
        if (IsDisposed || IsBusy || SelectedWorld is null || !table.Contains(row))
        {
            return;
        }

        var current = table.IndexOf(row);
        var target = Math.Clamp(targetIndex, 0, table.Count - 1);
        if (current == target)
        {
            return;
        }

        table.Move(current, target);
        RecomputePending();
    }

    /// <summary>
    /// 由视图在世界下拉框的 SelectionChanged 中调用（下拉框为单向绑定，避免
    /// 双向绑定在确认取消时与控件选择状态互相覆盖）：
    /// 切换世界会丢弃上一个世界的待应用更改，必须经用户确认；
    /// 拒绝时返回 <c>false</c>，视图把下拉框恢复到当前世界。
    /// </summary>
    public bool TrySelectWorld(DatapackWorldChoice world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (IsDisposed || ReferenceEquals(SelectedWorld, world))
        {
            return true;
        }

        if (HasPendingChanges && _loadedWorld is not null &&
            !_dialogs.Confirm(
                _localizer.Get("datapacks.switchDiscardTitle"),
                _localizer.Get("datapacks.refreshDiscardConfirm")))
        {
            return false;
        }

        SelectedWorld = world;
        return true;
    }

    partial void OnSelectedWorldChanged(DatapackWorldChoice? value)
    {
        if (IsDisposed)
        {
            return;
        }

        StatusMessage = string.Empty;
        NotifyWorldPresentation();

        if (value is null)
        {
            _loadedWorld = null;
            _hasLoadedWorld = false;
            ClearRows();
            WarningText = string.Empty;
            RecomputePending();
            return;
        }

        _ = LoadDatapacksAsync(value);
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanEdit));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingWorldsChanged(bool value) => NotifyWorldPresentation();

    private async Task LoadWorldsAsync()
    {
        var generation = ++_worldGeneration;
        IsLoadingWorlds = true;
        WorldScanWarning = string.Empty;
        try
        {
            var result = await _datapacks.ListWorldsAsync(_instanceId).ConfigureAwait(true);
            if (IsDisposed || generation != _worldGeneration)
            {
                return;
            }

            Worlds.Clear();
            SelectedWorld = null;
            ClearRows();
            _loadedWorld = null;
            _hasLoadedWorld = false;
            RecomputePending();

            if (!result.IsSuccess)
            {
                StatusMessage = _localizer.Get(
                    "downloads.discovery.target.worldsFailed",
                    result.Error?.ToString() ?? string.Empty);
                _worldsLoaded = false;
                return;
            }

            var scan = result.Value!;
            _worldsLoaded = true;
            WorldScanWarning = string.Join(Environment.NewLine, scan.Warnings);
            if (!scan.IsAccessible && !string.IsNullOrWhiteSpace(WorldScanWarning))
            {
                StatusMessage = _localizer.Get(
                    "downloads.discovery.target.worldsFailed",
                    WorldScanWarning);
            }

            foreach (var world in scan.Worlds)
            {
                Worlds.Add(new DatapackWorldChoice(
                    world.Name,
                    world.FolderName,
                    _localizer.Get("downloads.discovery.target.worldDetail", world.FolderName),
                    new DatapackTarget(_instanceId, world.FolderName)));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed && generation == _worldGeneration)
            {
                StatusMessage = _localizer.Get("downloads.discovery.target.worldsFailed", exception.Message);
                _worldsLoaded = false;
            }
        }
        finally
        {
            if (!IsDisposed && generation == _worldGeneration)
            {
                IsLoadingWorlds = false;
                NotifyWorldPresentation();
            }
        }
    }

    private async Task LoadDatapacksAsync(DatapackWorldChoice world)
    {
        if (IsDisposed)
        {
            return;
        }

        var generation = ++_listGeneration;
        _loadedWorld = world;
        _hasLoadedWorld = false;

        // 先丢弃上一个世界的工作副本：读取完成前「应用」不得用旧顺序写新世界。
        ClearRows();
        RecomputePending();
        IsBusy = true;
        try
        {
            var result = await _datapacks.ListAsync(_instanceId, world.Target).ConfigureAwait(true);
            if (IsDisposed || generation != _listGeneration || !ReferenceEquals(SelectedWorld, world))
            {
                return;
            }

            ClearRows();
            if (!result.IsSuccess)
            {
                _persistedEnabled = [];
                WarningText = result.Error?.ToString() ?? string.Empty;
                StatusMessage = _localizer.Get("datapacks.scanFailed");
                return;
            }

            var list = result.Value!;
            var rows = list.Packs
                .Select(entry => new DatapackRowViewModel(entry, _localizer))
                .ToArray();
            foreach (var row in rows.Where(row => row.IsEnabled))
            {
                EnabledPacks.Add(row);
            }

            foreach (var row in rows.Where(row => !row.IsEnabled))
            {
                DisabledPacks.Add(row);
            }

            _persistedEnabled = rows.Where(row => row.IsEnabled).Select(row => row.FileName).ToList();
            _hasLoadedWorld = true;
            WarningText = BuildWarning(list);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed && generation == _listGeneration)
            {
                ClearRows();
                _persistedEnabled = [];
                WarningText = exception.Message;
                StatusMessage = _localizer.Get("datapacks.scanFailed");
            }
        }
        finally
        {
            if (!IsDisposed && generation == _listGeneration)
            {
                IsBusy = false;
                RecomputePending();
            }
        }
    }

    private string BuildWarning(DatapackList list)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(WorldScanWarning))
        {
            warnings.Add(WorldScanWarning);
        }

        if (list.Status == DatapackSelectionStatus.NotInitialized)
        {
            warnings.Add(_localizer.Get("datapacks.warning.notInitialized"));
        }

        if (list.MissingEntries.Count > 0)
        {
            warnings.Add(_localizer.Get("packs.warning.missing", list.MissingEntries.Count, string.Join(", ", list.MissingEntries)));
        }

        if (list.UnmanagedEntries.Count > 0)
        {
            warnings.Add(_localizer.Get("packs.warning.unmanaged", list.UnmanagedEntries.Count, string.Join(", ", list.UnmanagedEntries)));
        }

        if (list.DuplicateEntries.Count > 0)
        {
            warnings.Add(_localizer.Get("packs.warning.duplicates", list.DuplicateEntries.Count, string.Join(", ", list.DuplicateEntries)));
        }

        return string.Join(Environment.NewLine, warnings);
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            if (!IsDisposed)
            {
                IsBusy = false;
                RecomputePending();
            }
        }
    }

    private void ClearRows()
    {
        EnabledPacks.Clear();
        DisabledPacks.Clear();
        _persistedEnabled = [];
    }

    private void RecomputePending()
    {
        var current = EnabledPacks.Select(row => row.FileName).ToArray();
        HasPendingChanges = SelectedWorld is not null &&
                            _hasLoadedWorld &&
                            !current.SequenceEqual(_persistedEnabled, StringComparer.OrdinalIgnoreCase);
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void NotifyWorldPresentation()
    {
        OnPropertyChanged(nameof(WorldHint));
        OnPropertyChanged(nameof(EmptyEnabledMessage));
        OnPropertyChanged(nameof(EmptyDisabledMessage));
        OnPropertyChanged(nameof(HasWorlds));
        OnPropertyChanged(nameof(ShowNoWorlds));
        OnPropertyChanged(nameof(IsWorldSelected));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanEdit));
    }

    private void OnCollectionsChanged()
    {
        OnPropertyChanged(nameof(EnabledCountText));
        OnPropertyChanged(nameof(DisabledCountText));
        OnPropertyChanged(nameof(HasEnabledPacks));
        OnPropertyChanged(nameof(HasDisabledPacks));
        OnPropertyChanged(nameof(HasAnyPacks));
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        OpenDatapackBrowser = null;
        Worlds.Clear();
        EnabledPacks.Clear();
        DisabledPacks.Clear();
    }
}
