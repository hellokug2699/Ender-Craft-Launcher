// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Mods;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 资源包/光影包行的状态分类（resource-library-template.md §3.1「状态」）。
/// 颜色语义固定：绿色=正常已启用、黄色=异常、红色=文件损坏、灰色=未识别。
/// </summary>
public enum ContentPackStatusKind
{
    Active,
    Warning,
    Corrupt,
    Unrecognized,

    /// <summary>页面级提示用的空心感叹号（不是条目状态）。</summary>
    Note,
}

/// <summary>
/// 一个资源包/光影包条目的只读投影。数据只来自 <see cref="ContentPackEntry"/>：
/// 启用状态与“已确认不兼容”来自 options.txt，名称/简介/格式来自包内 <c>pack.mcmeta</c>，
/// 读取失败只标记为损坏，不猜测内容。
/// </summary>
public sealed class ContentPackRowViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private bool _isEnabled;

    public ContentPackRowViewModel(ContentPackEntry entry, ILocalizer localizer)
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
            ContentPackReadState.Corrupt => ContentPackStatusKind.Corrupt,
            ContentPackReadState.Unrecognized => ContentPackStatusKind.Unrecognized,
            _ when entry.Incompatible => ContentPackStatusKind.Warning,
            _ => ContentPackStatusKind.Active,
        };

        // 状态列表达“文件健康度”：绿色=正常（已启用时标注“已启用”），
        // 黄色=游戏记录为不兼容，红色=损坏，灰色=未识别。
        StatusText = localizer.Get(StatusKind switch
        {
            ContentPackStatusKind.Corrupt => "packs.status.corrupt",
            ContentPackStatusKind.Unrecognized => "packs.status.unrecognized",
            ContentPackStatusKind.Warning => "packs.status.warning",
            _ => entry.Enabled ? "packs.status.active" : "packs.status.normal",
        });

        StatusHint = StatusKind switch
        {
            ContentPackStatusKind.Corrupt => localizer.Get("packs.status.corruptHint"),
            ContentPackStatusKind.Unrecognized => localizer.Get(
                entry.Kind == ContentPackKind.ShaderPack
                    ? "packs.status.unrecognizedShaderHint"
                    : "packs.status.unrecognizedHint"),
            ContentPackStatusKind.Warning => localizer.Get("packs.status.warningHint"),
            _ => localizer.Get(entry.Enabled ? "packs.status.activeHint" : "packs.status.normalHint"),
        };
    }

    public ContentPackEntry Entry { get; }

    public string FileName { get; }

    /// <summary>名称：文件名去掉扩展名；包内没有独立名称字段，简介单独显示。</summary>
    public string Title { get; }

    /// <summary>从 ZIP 提取的简介；没有简介时为空串，页面不显示占位。</summary>
    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    /// <summary>是否已启用；由页面在拖动/应用后同步，写盘只在「应用」时发生。</summary>
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

    /// <summary>
    /// 启用/禁用按钮的文案：与跨表拖动等价，供键盘与辅助技术使用；
    /// 只改页面工作副本，写盘仍由「应用」提交。
    /// </summary>
    public string ToggleLabel => _localizer.Get(IsEnabled ? "packs.toggle.disable" : "packs.toggle.enable");

    public ContentPackStatusKind StatusKind { get; }

    public string StatusText { get; }

    public string StatusHint { get; }

    /// <summary>版本范围列：只显示文件声明或官方对照表中的事实，未知时显示未知。</summary>
    public string VersionRangeText { get; }

    private static string BuildVersionRangeText(ContentPackEntry entry, ILocalizer localizer)
    {
        if (entry.Kind == ContentPackKind.ShaderPack)
        {
            // 光影包没有 pack.mcmeta，不声明 Minecraft 版本范围。
            return localizer.Get("packs.versionRange.unknown");
        }

        if (entry.SupportedFormats is { } declared)
        {
            if (ResourcePackFormats.Resolve(declared) is { } declaredRange)
            {
                return localizer.Get("packs.versionRange.range", declaredRange.Minimum, declaredRange.Maximum);
            }

            return localizer.Get(
                "packs.versionRange.formatRange",
                declared.MinInclusive,
                declared.MaxInclusive);
        }

        if (entry.PackFormat is { } format)
        {
            if (ResourcePackFormats.Resolve(format) is { } range)
            {
                return localizer.Get("packs.versionRange.range", range.Minimum, range.Maximum);
            }

            return localizer.Get("packs.versionRange.format", format);
        }

        return localizer.Get("packs.versionRange.unknown");
    }
}

/// <summary>
/// 实例工作区「内容 · 资源包 / 光影包」页面（B2b）。
/// <para>
/// 布局与交互按 <c>hints/ui/pages/resource-library-template.md</c>：顶部刷新/添加/下载，
/// 「已启用 / 未启用」两张表格各自计数，行内删除，拖动调整顺序或跨表启用/禁用，
/// 「应用」把启用状态与顺序一次写入对应资源类型的配置。
/// </para>
/// <para>
/// 页面只持有“待应用”的工作副本：拖动与跨表移动不写盘，只有「应用」调用
/// <see cref="IContentPackService.ApplyAsync"/>；删除按自身流程立即执行并确认。
/// 未启用条目的顺序在 Minecraft 配置中没有表示，因此只作为本页面的整理顺序，
/// 不进入待应用更改（页面在表格下方说明这一点）。
/// </para>
/// </summary>
public sealed partial class ContentPackPageViewModel : ObservableObject, IDisposable
{
    private readonly string _instanceId;
    private readonly ContentPackKind _kind;
    private readonly IContentPackService _packs;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly IShaderSupportService? _shaderSupport;
    private readonly IShaderHintStore? _shaderHints;
    private List<string> _persistedEnabled = [];
    private bool _isLoading;
    private int _lastPackCount = -1;

    public ContentPackPageViewModel(
        string instanceId,
        ContentPackKind kind,
        IContentPackService packs,
        IDialogService dialogs,
        ILocalizer localizer,
        IShaderSupportService? shaderSupport = null,
        IShaderHintStore? shaderHints = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(localizer);

        _instanceId = instanceId;
        _kind = kind;
        _packs = packs;
        _dialogs = dialogs;
        _localizer = localizer;
        _shaderSupport = shaderSupport;
        _shaderHints = shaderHints;

        EnabledPacks.CollectionChanged += (_, _) => OnCollectionsChanged();
        DisabledPacks.CollectionChanged += (_, _) => OnCollectionsChanged();
    }

    /// <summary>
    /// 由 Shell 注入的下载入口：带入实例 ID 与资源类型，跳到下载中心的对应页签。
    /// 为 <c>null</c> 时下载按钮不可用（避免伪造入口）。
    /// </summary>
    public Action<string, ContentPackKind>? OpenPackBrowser { get; set; }

    public string InstanceId => _instanceId;

    public ContentPackKind Kind => _kind;

    public bool IsShaderPage => _kind == ContentPackKind.ShaderPack;

    /// <summary>已启用表格（顺序即 options.txt 中的顺序 / 优先级）。</summary>
    public ObservableCollection<ContentPackRowViewModel> EnabledPacks { get; } = [];

    /// <summary>未启用表格；顺序只影响本页面显示。</summary>
    public ObservableCollection<ContentPackRowViewModel> DisabledPacks { get; } = [];

    public bool IsDisposed { get; private set; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>options.txt 的读取警告（无法解析、缺失条目等）；非空时页面以弱提示展示。</summary>
    [ObservableProperty]
    private string _warningText = string.Empty;

    [ObservableProperty]
    private bool _hasPendingChanges;

    public bool CanApply => HasPendingChanges && !IsBusy;

    public bool CanEdit => !IsBusy;

    public bool CanOpenBrowser => OpenPackBrowser is not null;

    public string EnabledCountText => _localizer.Get("packs.count", EnabledPacks.Count);

    public string DisabledCountText => _localizer.Get("packs.count", DisabledPacks.Count);

    public bool HasEnabledPacks => EnabledPacks.Count > 0;

    public bool HasDisabledPacks => DisabledPacks.Count > 0;

    public bool HasAnyPacks => EnabledPacks.Count + DisabledPacks.Count > 0;

    /// <summary>“未启用条目的顺序不写入游戏配置”的说明（模板 §6 的准确边界）。</summary>
    public string DisabledOrderHint => _localizer.Get("packs.disabledOrderHint");

    public string ApplyHint => _localizer.Get("packs.applyHint");

    /// <summary>页面标题，用于表格上方的资源类型说明。</summary>
    public string KindTitle => _localizer.Get(
        _kind == ContentPackKind.ShaderPack ? "instance.nav.shaders" : "instance.nav.resourcePacks");

    public string EnabledSectionTitle => _localizer.Get("packs.section.enabled", KindTitle);

    public string DisabledSectionTitle => _localizer.Get("packs.section.disabled", KindTitle);

    public string AddButtonText => _localizer.Get("packs.add", KindTitle);

    public string DownloadButtonText => _localizer.Get("packs.download", KindTitle);

    public string EmptyEnabledMessage => _localizer.Get("packs.empty.enabled", KindTitle);

    public string EmptyDisabledMessage => _localizer.Get("packs.empty.disabled", KindTitle);

    public string DragHint => _localizer.Get("packs.dragHint");

    /// <summary>
    /// 光影支持检测结果（shaders.md §2）；<c>null</c> 表示尚未检测或检测失败。
    /// 资源包页不使用。
    /// </summary>
    public ShaderSupportState? ShaderSupport { get; private set; }

    /// <summary>首次提示横幅是否显示：不支持且用户尚未收起（shaders.md §3）。</summary>
    public bool IsShaderBannerVisible =>
        IsShaderPage && ShaderSupport is { } state && state != ShaderSupportState.Supported && !IsShaderHintDismissed;

    /// <summary>收起首次提示后的一行灰色弱提示（shaders.md §4）。</summary>
    public bool IsShaderHintVisible =>
        IsShaderPage && ShaderSupport is { } state && state != ShaderSupportState.Supported && IsShaderHintDismissed;

    public bool IsShaderHintDismissed { get; private set; }

    /// <summary>按真实检测结果生成的原因文案，不使用固定的“没有安装 OptiFine”。</summary>
    public string ShaderSupportReason { get; private set; } = string.Empty;

    /// <summary>检测本身失败（读取实例或扫描模组失败）时的说明；非空时不冒充任何支持状态。</summary>
    public string ShaderSupportError { get; private set; } = string.Empty;

    public string ShaderBannerTitle => _localizer.Get("shaders.banner.title");

    public string ShaderBannerBody => _localizer.Get("shaders.banner.body");

    public string ShaderBannerNote => _localizer.Get("shaders.banner.note");

    public string ShaderBannerDismissText => _localizer.Get("shaders.banner.dismiss");


    /// <summary>首次进入页面时加载；失败只改变本页文案，不抛给 Shell。</summary>
    public async Task InitializeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsDisposed || IsBusy)
        {
            return;
        }

        if (HasPendingChanges && !_dialogs.Confirm(
                _localizer.Get("packs.refreshTitle"),
                _localizer.Get("packs.refreshDiscardConfirm")))
        {
            return;
        }

        StatusMessage = string.Empty;
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddPackAsync()
    {
        if (IsDisposed || IsBusy)
        {
            return;
        }

        var file = _dialogs.PickOpenFile(
            _localizer.Get("packs.importTitle", KindTitle),
            _localizer.Get("packs.importFilter"));
        if (file is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _packs.ImportAsync(_instanceId, _kind, file).ConfigureAwait(true);
            StatusMessage = result.IsSuccess
                ? _localizer.Get("packs.imported", result.Value!.FileName)
                : result.Error?.ToString() ?? _localizer.Get("packs.importFailed");
            if (result.IsSuccess)
            {
                await ReloadAsync().ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void DownloadPacks()
    {
        if (IsDisposed || OpenPackBrowser is null)
        {
            return;
        }

        OpenPackBrowser(_instanceId, _kind);
    }

    /// <summary>收起首次提示：按实例保存，之后只显示灰色弱提示（shaders.md §4/§5）。</summary>
    [RelayCommand]
    private void DismissShaderBanner()
    {
        if (IsDisposed)
        {
            return;
        }

        _shaderHints?.Dismiss(_instanceId);
        IsShaderHintDismissed = true;
        NotifyShaderPresentation();
    }

    /// <summary>提交待应用的启用状态与顺序；只有成功后才刷新为新的已保存状态。</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (IsDisposed || IsBusy || !HasPendingChanges)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var ordered = EnabledPacks.Select(row => row.FileName).ToArray();
            var result = await _packs.ApplyAsync(_instanceId, _kind, ordered).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                StatusMessage = _localizer.Get("packs.applied", ordered.Length);
                await ReloadAsync().ConfigureAwait(true);
                return;
            }

            // 应用失败时保留用户的待应用更改，不静默丢弃选择。
            StatusMessage = result.Error?.ToString() ?? _localizer.Get("packs.applyFailed");
        }).ConfigureAwait(true);
    }

    /// <summary>删除单个文件：按页面规格属于自身操作流程，确认后立即执行，不做成待应用更改。</summary>
    [RelayCommand]
    private async Task DeletePackAsync(ContentPackRowViewModel? row)
    {
        if (IsDisposed || IsBusy || row is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("packs.deleteTitle"),
                _localizer.Get("packs.deleteConfirm", row.FileName)))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _packs.DeleteAsync(_instanceId, _kind, row.FileName).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                StatusMessage = _localizer.Get("packs.deleted", row.FileName);
                await ReloadAsync().ConfigureAwait(true);
                return;
            }

            // 删除失败时同样保留当前工作副本，不把未删除的文件从页面抹掉。
            StatusMessage = result.Error?.ToString() ?? _localizer.Get("packs.deleteFailed");
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 拖动/跨表移动的唯一条目：把 <paramref name="row"/> 放入目标表格的
    /// <paramref name="targetIndex"/>（超出范围时追加到末尾）。只改页面工作副本，不写盘。
    /// </summary>
    public void MovePack(ContentPackRowViewModel row, bool toEnabled, int targetIndex)
    {
        if (IsDisposed || IsBusy || row is null)
        {
            return;
        }

        if (toEnabled)
        {
            // 光影包同时只能启用一个：启用新的条目时把原有条目退回未启用表。
            if (IsShaderPage)
            {
                foreach (var other in EnabledPacks.Where(item => !ReferenceEquals(item, row)).ToArray())
                {
                    EnabledPacks.Remove(other);
                    other.IsEnabled = false;
                    DisabledPacks.Insert(0, other);
                }
            }

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
    private void TogglePack(ContentPackRowViewModel? row)
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
    public void MoveWithin(ContentPackRowViewModel row, bool enabledTable, int targetIndex)
    {
        var table = enabledTable ? EnabledPacks : DisabledPacks;
        if (IsDisposed || IsBusy || !table.Contains(row))
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

    private async Task ReloadAsync()
    {
        if (IsDisposed || _isLoading)
        {
            return;
        }

        _isLoading = true;
        try
        {
            var result = await _packs.ListAsync(_instanceId, _kind).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            EnabledPacks.Clear();
            DisabledPacks.Clear();

            if (!result.IsSuccess)
            {
                _persistedEnabled = [];
                WarningText = result.Error?.ToString() ?? string.Empty;
                StatusMessage = _localizer.Get("packs.scanFailed");
                RecomputePending();
                return;
            }

            var list = result.Value!;
            var rows = list.Packs
                .Select(entry => new ContentPackRowViewModel(entry, _localizer))
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
            WarningText = BuildWarning(list);
            RecomputePending();

            if (IsShaderPage)
            {
                var count = rows.Length;
                // 全部光影包被清空时重置该实例的首次提示状态（shaders.md §5）。
                if (_lastPackCount > 0 && count == 0)
                {
                    _shaderHints?.Reset(_instanceId);
                    IsShaderHintDismissed = false;
                }

                _lastPackCount = count;
                await RefreshShaderSupportAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed)
            {
                WarningText = exception.Message;
                StatusMessage = _localizer.Get("packs.scanFailed");
                RecomputePending();
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private string BuildWarning(ContentPackList list)
    {
        var warnings = new List<string>();
        if (list.Status == PackOptionsStatus.Unreadable && !string.IsNullOrWhiteSpace(list.Warning))
        {
            warnings.Add(list.Warning);
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

    /// <summary>
    /// 光影支持检测（shaders.md §6）：每次刷新光影包列表后重新检测组件，
    /// 安装/卸载模组后的下一次进入也会反映最新结果。
    /// 检测失败只显示失败说明，不降级为“支持”。
    /// </summary>
    private async Task RefreshShaderSupportAsync()
    {
        IsShaderHintDismissed = _shaderHints?.IsDismissed(_instanceId) ?? false;

        if (_shaderSupport is null)
        {
            ShaderSupport = null;
            ShaderSupportError = _localizer.Get("shaders.detect.unavailable");
            ShaderSupportReason = string.Empty;
            NotifyShaderPresentation();
            return;
        }

        var result = await _shaderSupport.DetectAsync(_instanceId).ConfigureAwait(true);
        if (IsDisposed)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            ShaderSupport = null;
            ShaderSupportError = result.Error?.ToString() ?? _localizer.Get("shaders.detect.failed");
            ShaderSupportReason = string.Empty;
            NotifyShaderPresentation();
            return;
        }

        var detection = result.Value!;
        ShaderSupport = detection.State;
        ShaderSupportError = string.Empty;
        ShaderSupportReason = DescribeShaderSupport(detection);
        NotifyShaderPresentation();
    }

    /// <summary>按真实检测结果生成原因，不固定成“没有安装 OptiFine”。</summary>
    private string DescribeShaderSupport(ShaderSupportResult result) => result.State switch
    {
        ShaderSupportState.Supported => string.Empty,
        ShaderSupportState.Missing => _localizer.Get("shaders.reason.missing"),
        ShaderSupportState.Incomplete => _localizer.Get(
            "shaders.reason.incomplete",
            ProviderLabel(result.Providers),
            result.Detail ?? _localizer.Get("shaders.reason.dependencyUnknown")),
        ShaderSupportState.Conflict => _localizer.Get(
            "shaders.reason.conflict",
            ProviderLabel(result.Providers)),
        ShaderSupportState.Incompatible => _localizer.Get(
            result.Detail switch
            {
                "iris-forge-unsupported" => "shaders.reason.irisForge",
                "iris-version-unsupported" or "oculus-version-unsupported" => "shaders.reason.versionMismatch",
                "oculus-loader-unsupported" => "shaders.reason.oculusFabric",
                "iris-artifact-mismatch" or "oculus-artifact-mismatch" => "shaders.reason.artifactMismatch",
                _ => "shaders.reason.incompatible",
            },
            ProviderLabel(result.Providers)),
        _ => _localizer.Get(
            "shaders.reason.unknown",
            ProviderLabel(result.Providers)),
    };

    private string ProviderLabel(IReadOnlyList<string> providers)
    {
        var labels = providers.Select(provider => provider switch
        {
            ShaderSupportDetector.IrisModId => _localizer.Get("shaders.provider.iris"),
            ShaderSupportDetector.OculusModId => _localizer.Get("shaders.provider.oculus"),
            ShaderSupportDetector.OptiFineProvider => _localizer.Get("shaders.provider.optifine"),
            _ => provider,
        }).ToArray();

        return labels.Length == 0 ? string.Empty : string.Join(" / ", labels);
    }

    private void NotifyShaderPresentation()
    {
        OnPropertyChanged(nameof(ShaderSupport));
        OnPropertyChanged(nameof(IsShaderBannerVisible));
        OnPropertyChanged(nameof(IsShaderHintVisible));
        OnPropertyChanged(nameof(IsShaderHintDismissed));
        OnPropertyChanged(nameof(ShaderSupportReason));
        OnPropertyChanged(nameof(ShaderSupportError));
    }

    private void RecomputePending()
    {
        var current = EnabledPacks.Select(row => row.FileName).ToArray();
        HasPendingChanges = !current.SequenceEqual(_persistedEnabled, StringComparer.OrdinalIgnoreCase);
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void OnCollectionsChanged()
    {
        OnPropertyChanged(nameof(EnabledCountText));
        OnPropertyChanged(nameof(DisabledCountText));
        OnPropertyChanged(nameof(HasEnabledPacks));
        OnPropertyChanged(nameof(HasDisabledPacks));
        OnPropertyChanged(nameof(HasAnyPacks));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanEdit));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        OpenPackBrowser = null;
        EnabledPacks.Clear();
        DisabledPacks.Clear();
    }
}
