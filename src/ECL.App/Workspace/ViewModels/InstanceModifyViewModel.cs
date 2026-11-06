// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Engine.Inventory;
using Ecl.Minecraft.Engine.Snapshots;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Versions;
using Ecl.Operations;
using Ecl.Sessions;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 实例工作区「配置 · 修改」二级页。
///
/// B2 步骤 3b-4a 建立了只读变更计划与影响预览（`instance-modify.md` §5–§7）；
/// B2 步骤 3b-4b 在这套目标值与五态计划状态机上追加<strong>受控执行</strong>：
/// 确认 → 创建并校验完整实例快照 → 通过 <see cref="IVersionInstallService.ReconfigureAsync"/>
/// 执行变更 → 按领域返回的回滚信号区分「失败且已回滚」「失败且需要恢复」。
///
/// 计划内容只来自真实领域结论：
/// - 目标版本目录来自 <see cref="IVersionCatalog"/>，加载器与组件按目标版本过滤后由
///   <see cref="ILoaderCatalog"/> 提供，不允许手输版本字符串；
/// - Java 结论来自 <see cref="IJavaSelectionService"/>；目标版本文件尚未安装时，
///   只使用 <see cref="JavaRequirements.TryInferMinimumMajor"/> 的推断值，并明确标注为推断；
/// - 内容影响来自 <see cref="IModService.ScanAsync"/> 的模组元数据与
///   <see cref="IContentPackService.ListAsync"/>；无法可靠判断时显示「未知」，不冒充兼容；
/// - 活动进程来自 <see cref="IGameSessionService"/>，写入任务来自 <see cref="IOperationService"/>；
///   两者都只能证明“存在”，不能证明“属于本实例”，因此按需处理项呈现。
///
/// 目标实例由工作区打开时的稳定 InstanceId 固定绑定，页面不提供目录或实例切换入口。
/// 生命周期由 <see cref="InstanceDetailPageViewModel"/> 持有；关闭页面不会取消已提交的执行任务
/// （任务归属统一 Operation，页面只停止回写自己的状态）。
/// </summary>
public sealed partial class InstanceModifyViewModel : ObservableObject, IDisposable
{
    private const int MaxModNamesInPlan = 6;

    /// <summary>
    /// 可以直接展示给用户的安装错误代码：这些文案只描述失败类别与文件名，不含本地路径。
    /// 其余错误一律显示通用说明，细节只写诊断文件。
    /// </summary>
    private static readonly HashSet<string> SafeInstallErrorCodes = new(StringComparer.Ordinal)
    {
        "install.cancelled",
        "install.download_failed",
        "install.instance_missing",
        "install.unsupported_loader",
        "install.loader_version_missing",
        "install.client_missing",
        "install.assets_missing",
        "install.version_not_found",
        "install.version_invalid",
        "install.loader_failed",
        "install.fabric_api_missing",
        "install.optifine_missing",
    };

    private static readonly IReadOnlyList<string> LoaderOrder =
    [
        LoaderKinds.Vanilla,
        LoaderKinds.Fabric,
        LoaderKinds.Forge,
        LoaderKinds.NeoForge,
        LoaderKinds.Cleanroom,
    ];

    private static readonly IReadOnlyList<OperationKind> WriteOperationKinds =
    [
        OperationKind.Install,
        OperationKind.Download,
        OperationKind.Import,
        OperationKind.Backup,
        OperationKind.Restore,
        OperationKind.Verification,
    ];

    /// <summary>处于活动状态的游戏会话状态；进程退出后进入采集收尾，不再视为游戏在运行。</summary>
    private static readonly IReadOnlyList<GameSessionState> ActiveSessionStates =
    [
        GameSessionState.Preparing,
        GameSessionState.Starting,
        GameSessionState.Running,
    ];

    private readonly InstanceWorkspaceState _state;
    private readonly IVersionCatalog _versionCatalog;
    private readonly ILoaderCatalog _loaderCatalog;
    private readonly IVersionResolver _versionResolver;
    private readonly IJavaSelectionService _javaSelection;
    private readonly IModService _mods;
    private readonly IContentPackService _contentPacks;
    private readonly IGameSessionService _sessions;
    private readonly IOperationService _operations;
    private readonly IVersionInstallService _install;
    private readonly IInstanceSnapshotService _snapshots;
    private readonly IDialogEngine _dialogs;
    private readonly ILocalizer _localizer;

    private CancellationTokenSource? _targetCancellation;
    private CancellationTokenSource? _planCancellation;
    private string _targetFabricApiVersion = string.Empty;
    private string? _diagnosticPath;
    private ModifyExecutionTarget? _planTarget;
    private int _generation;
    private bool _suppressTargetReload;
    private bool _initialized;

    public InstanceModifyViewModel(
        InstanceWorkspaceState state,
        IVersionCatalog versionCatalog,
        ILoaderCatalog loaderCatalog,
        IVersionResolver versionResolver,
        IJavaSelectionService javaSelection,
        IModService mods,
        IContentPackService contentPacks,
        IGameSessionService sessions,
        IOperationService operations,
        IVersionInstallService install,
        IInstanceSnapshotService snapshots,
        IDialogEngine dialogs,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(versionCatalog);
        ArgumentNullException.ThrowIfNull(loaderCatalog);
        ArgumentNullException.ThrowIfNull(versionResolver);
        ArgumentNullException.ThrowIfNull(javaSelection);
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(contentPacks);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(localizer);

        _state = state;
        _versionCatalog = versionCatalog;
        _loaderCatalog = loaderCatalog;
        _versionResolver = versionResolver;
        _javaSelection = javaSelection;
        _mods = mods;
        _contentPacks = contentPacks;
        _sessions = sessions;
        _operations = operations;
        _install = install;
        _snapshots = snapshots;
        _dialogs = dialogs;
        _localizer = localizer;

        _planStateDetail = ModifyPlanPresentation.StateDetail(InstanceModifyPlanState.NotChecked, _localizer);

        _suppressTargetReload = true;
        try
        {
            RebuildLoaderChoices();
            SelectedTargetLoader = LoaderChoices.FirstOrDefault(choice =>
                                     string.Equals(choice.Id, CurrentLoaderId, StringComparison.Ordinal))
                                 ?? LoaderChoices[0];
        }
        finally
        {
            _suppressTargetReload = false;
        }
    }

    /// <summary>可变更的目标 Minecraft 版本目录；没有可用目录时为空。</summary>
    public ObservableCollection<VersionChoiceViewModel> TargetVersions { get; } = [];

    /// <summary>按目标版本过滤后的加载器选项（Cleanroom 仅 1.12.2）。</summary>
    public ObservableCollection<LoaderChoiceViewModel> LoaderChoices { get; } = [];

    /// <summary>目标加载器在当前目标版本下可验证的版本。</summary>
    public ObservableCollection<VersionChoiceViewModel> LoaderVersions { get; } = [];

    /// <summary>目标加载器支持的 OptiFine 版本；第一项为“不安装”，永不自作主张安装。</summary>
    public ObservableCollection<VersionChoiceViewModel> OptiFineVersions { get; } = [];

    /// <summary>只读计划条目；生成计划前为空。</summary>
    public ObservableCollection<ModifyPlanItemViewModel> PlanItems { get; } = [];

    /// <summary>可由用户解决、必须处理完才能继续的项。</summary>
    public ObservableCollection<string> ActionItems { get; } = [];

    /// <summary>无法安全继续的原因；非空时状态为“不可执行”。</summary>
    public ObservableCollection<string> Blockers { get; } = [];

    public string InstanceId => _state.InstanceId;

    public string InstanceName => _state.Item.Name;

    public string InstanceMeta => _state.Item.VersionText;

    public string CurrentVersionText => _state.Item.MinecraftVersionText;

    public string CurrentLoaderText => _state.Item.LoaderText;

    public string CurrentLoaderVersionText => string.IsNullOrWhiteSpace(_state.Model.LoaderVersion)
        ? _localizer.Get(string.Equals(CurrentLoaderId, LoaderKinds.Vanilla, StringComparison.Ordinal)
            ? "instance.modify.valueNone"
            : "instance.modify.valueUnknown")
        : _state.Model.LoaderVersion;

    public string CurrentOptiFineText => string.IsNullOrWhiteSpace(_state.Model.OptiFineVersion)
        ? _localizer.Get("instance.modify.valueNone")
        : _state.Model.OptiFineVersion;

    public string CurrentPath => _state.Item.PathText;

    /// <summary>拟变更的组件组合（Fabric API / OptiFine）；未选择时显示“无”。</summary>
    public string TargetComponentsText =>
        DescribeTargetComponents(SelectedTargetLoader?.Id ?? LoaderKinds.Vanilla, SelectedTargetOptiFine?.Value);

    /// <summary>当前实例安装的 ECL 可识别组件；没有时显示“无”。</summary>
    public string CurrentComponentsText =>
        InstanceComponentsPresentation.Describe(_state.Model, _localizer) is { Length: > 0 } components
            ? components
            : _localizer.Get("instance.modify.componentsNone");

    /// <summary>
    /// 目标版本可变更的加载器说明：非 1.12.2 不含 Cleanroom；当前实例加载器无法识别时
    /// 也会在这里说明，避免把“未知加载器”静默当成原版。
    /// </summary>
    [ObservableProperty]
    private string _loaderFilterNote = string.Empty;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedTargetVersion;

    [ObservableProperty]
    private LoaderChoiceViewModel? _selectedTargetLoader;

    /// <summary>目标加载器版本；原版不需要。</summary>
    [ObservableProperty]
    private VersionChoiceViewModel? _selectedTargetLoaderVersion;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedTargetOptiFine;

    /// <summary>目标组合的 Fabric API 结论（保留当前 / 默认最新兼容）；非 Fabric 为空。</summary>
    [ObservableProperty]
    private string _fabricApiText = string.Empty;

    /// <summary>OptiFine 的真实状态说明（例如当前版本在目标版本下没有可用文件）。</summary>
    [ObservableProperty]
    private string _optiFineNote = string.Empty;

    /// <summary>目标版本 / 加载器 / 组件读取中的状态或错误文案；成功时为空。</summary>
    [ObservableProperty]
    private string _targetStatus = string.Empty;

    /// <summary>最近一次目标组合读取失败的原因；生成计划时据此显示不可执行。</summary>
    [ObservableProperty]
    private string _targetLoadError = string.Empty;

    [ObservableProperty]
    private bool _isLoadingTarget;

    /// <summary>目标加载器需要版本号（原版不需要）。</summary>
    [ObservableProperty]
    private bool _isLoaderVersionRequired;

    /// <summary>目标加载器使用安装器（Forge / NeoForge），其 processors 需要可用的 Java。</summary>
    [ObservableProperty]
    private bool _requiresProcessorJava;

    public bool IsFabricTarget => string.Equals(SelectedTargetLoader?.Id, LoaderKinds.Fabric, StringComparison.Ordinal);

    public bool IsOptiFineTarget =>
        SelectedTargetLoader?.Id is LoaderKinds.Forge or LoaderKinds.Cleanroom;

    /// <summary>
    /// 目标组合实际支持且 ECL 能识别的组件区域。原版、NeoForge 等没有可编辑附加组件的组合
    /// 不显示该区域；已有组件的影响仍由变更计划的「需要处理」项说明。
    /// </summary>
    public bool ShowComponentsSection =>
        SelectedTargetVersion is not null && (IsFabricTarget || IsOptiFineTarget);

    /// <summary>Fabric 目标的 Fabric API 行（只读，默认最新兼容版本）。</summary>
    public bool ShowFabricApiRow => SelectedTargetVersion is not null && IsFabricTarget;

    /// <summary>Forge/Cleanroom 目标的 OptiFine 行（只有该加载器支持 OptiFine）。</summary>
    public bool ShowOptiFineRow => SelectedTargetVersion is not null && IsOptiFineTarget;

    /// <summary>目标版本确实有可选的 OptiFine 文件时才显示可编辑选择器；否则只显示只读结论与原因。</summary>
    public bool ShowOptiFineControl =>
        SelectedTargetVersion is not null && IsOptiFineTarget && OptiFineVersions.Count > 1;

    /// <summary>目标组合的 Fabric API 版本；读取失败或无可用版本时显示只读结论，不伪造版本号。</summary>
    public string TargetFabricApiText => !IsFabricTarget
        ? _localizer.Get("instance.modify.componentsNone")
        : !string.IsNullOrWhiteSpace(_targetFabricApiVersion)
            ? _targetFabricApiVersion
            : string.IsNullOrWhiteSpace(FabricApiText)
                ? _localizer.Get("instance.modify.componentsNone")
                : _localizer.Get("instance.modify.components.readFailed");

    /// <summary>目标 OptiFine 选择的可读文本；没有可编辑控件时也用它说明目标状态。</summary>
    public string TargetOptiFineText =>
        SelectedTargetOptiFine?.DisplayName ?? _localizer.Get("instance.modify.target.optiFineNone");

    public bool HasLoaderFilterNote => LoaderFilterNote.Length > 0;

    public bool HasFabricApiNote => FabricApiText.Length > 0;

    public bool HasOptiFineNote => OptiFineNote.Length > 0;

    public bool HasTargetStatus => TargetStatus.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GeneratePlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreSnapshotCommand))]
    private InstanceModifyPlanState _planState = InstanceModifyPlanState.NotChecked;

    public string PlanStateText => ModifyPlanPresentation.StateText(PlanState, _localizer);

    [ObservableProperty]
    private string _planStateDetail;

    public bool IsPlanExecutable => PlanState == InstanceModifyPlanState.Executable;

    public bool IsPlanBlocked => PlanState == InstanceModifyPlanState.NotExecutable;

    public bool IsPlanNeedsAction => PlanState == InstanceModifyPlanState.NeedsAction;

    public bool IsChecking => PlanState == InstanceModifyPlanState.Checking;

    /// <summary>执行中：目标选择与提交均锁定；页面只转发真实阶段，不提供强制取消。</summary>
    public bool IsExecuting => PlanState == InstanceModifyPlanState.Executing;

    public bool IsSucceeded => PlanState == InstanceModifyPlanState.Succeeded;

    /// <summary>失败且已回滚：领域确认文件级回滚完整、实例元数据已还原。</summary>
    public bool IsFailedRolledBack => PlanState == InstanceModifyPlanState.FailedRolledBack;

    /// <summary>失败且需要恢复：回滚不完整或元数据未还原，必须提供受控恢复入口。</summary>
    public bool IsFailedNeedsRecovery => PlanState == InstanceModifyPlanState.FailedNeedsRecovery;

    /// <summary>执行前中止：快照/校验失败或运行状态变化，实例未写入本次变更。</summary>
    public bool IsFailedBeforeChange => PlanState == InstanceModifyPlanState.FailedBeforeChange;

    /// <summary>目标选择是否可编辑：执行中与恢复中锁定，避免与正在运行的写入竞争。</summary>
    public bool IsTargetEditable => !IsExecuting && !IsRestoring;

    public bool HasPlan => PlanItems.Count > 0;

    public bool HasBlockers => Blockers.Count > 0;

    public bool HasActionItems => ActionItems.Count > 0;

    /// <summary>执行/恢复的真实阶段文本（来自领域进度，不做百分比猜测）。</summary>
    [ObservableProperty]
    private string _executionStage = string.Empty;

    /// <summary>执行结果摘要：成功时是实际提交的目标，失败时是领域错误与回滚结论。</summary>
    [ObservableProperty]
    private string _executionResult = string.Empty;

    /// <summary>变更前快照 ID；创建成功后保留，恢复入口只使用这个已校验的快照。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSnapshotCommand))]
    private string _snapshotId = string.Empty;

    /// <summary>快照范围的说明；未创建快照时为空。</summary>
    [ObservableProperty]
    private string _snapshotSummary = string.Empty;

    /// <summary>诊断文件的说明（是否写入、可打开日志目录）；不包含本地路径。</summary>
    [ObservableProperty]
    private string _diagnosticNote = string.Empty;

    public bool HasExecutionStage => ExecutionStage.Length > 0;

    public bool HasExecutionResult => ExecutionResult.Length > 0;

    public bool HasSnapshotSummary => SnapshotSummary.Length > 0;

    public bool HasDiagnosticNote => DiagnosticNote.Length > 0;

    /// <summary>诊断文件是否真实存在；只有它存在时才提供打开入口。</summary>
    public bool HasDiagnosticFile => _diagnosticPath is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSnapshotCommand))]
    [NotifyPropertyChangedFor(nameof(IsTargetEditable))]
    private bool _isRestoring;

    [ObservableProperty]
    private string _restoreStatus = string.Empty;

    public bool HasRestoreStatus => RestoreStatus.Length > 0;

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// 受控变更成功后刷新工作区身份（由 <see cref="InstanceDetailPageViewModel"/> 注入）。
    /// 页面不直接读写实例列表，也不自行重新构造工作区。
    /// </summary>
    internal Func<Task>? WorkspaceRefresh { get; set; }

    /// <summary>
    /// 最近一次由选择变化触发的目标组合读取。绑定层无法等待异步命令，因此把任务暴露给
    /// 自动化测试与内部调用方，避免用固定延迟猜测后台读取是否完成。
    /// </summary>
    internal Task PendingTargetRefresh { get; private set; } = Task.CompletedTask;

    private string CurrentLoaderId => NormalizeLoader(_state.Model.Loader);

    /// <summary>
    /// 首次显示页面时读取目标版本目录。失败只影响本页文案（用户仍可看到当前配置），
    /// 不构造空白页面；重复调用幂等。
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (_initialized || IsDisposed)
        {
            return;
        }

        _initialized = true;
        await LoadTargetVersionsAsync().ConfigureAwait(true);
    }

    private async Task LoadTargetVersionsAsync()
    {
        var generation = ++_generation;
        TargetStatus = _localizer.Get("instance.modify.target.loading");
        OnPropertyChanged(nameof(HasTargetStatus));

        try
        {
            var result = await _versionCatalog.GetVersionsAsync().ConfigureAwait(true);
            if (IsDisposed || generation != _generation)
            {
                return;
            }

            TargetVersions.Clear();
            if (!result.IsSuccess)
            {
                TargetStatus = _localizer.Get(
                    "instance.modify.target.catalogFailed",
                    result.Error?.Message ?? string.Empty);
                return;
            }

            foreach (var version in result.Value!)
            {
                TargetVersions.Add(new VersionChoiceViewModel(
                    version.Id,
                    FormatVersionWithDate(version.Id, version.ReleasedAt)));
            }

            var current = _state.Model.GameVersion;
            if (!TargetVersions.Any(choice => string.Equals(choice.Value, current, StringComparison.Ordinal)))
            {
                // 实例来自外部安装时，其游戏版本可能不在 Mojang 目录中；仍然允许以它作为
                // 当前目标显示，但不伪造日期或可用性。
                TargetVersions.Insert(0, new VersionChoiceViewModel(
                    current,
                    _localizer.Get("instance.modify.target.versionUnlisted", current)));
            }

            _suppressTargetReload = true;
            try
            {
                SelectedTargetVersion = TargetVersions.FirstOrDefault(choice =>
                                           string.Equals(choice.Value, current, StringComparison.Ordinal))
                                       ?? TargetVersions.FirstOrDefault();

                // 加载器选项按目标版本过滤（Cleanroom 仅 1.12.2），版本确定后必须重建一次。
                RebuildLoaderChoices();

                // 首次进入页面时默认以实例当前的加载器作为目标，而不是目录中的第一个选项。
                SelectedTargetLoader = LoaderChoices.FirstOrDefault(choice =>
                                           string.Equals(choice.Id, CurrentLoaderId, StringComparison.Ordinal))
                                       ?? SelectedTargetLoader;
            }
            finally
            {
                _suppressTargetReload = false;
            }

            if (SelectedTargetVersion is null)
            {
                TargetStatus = _localizer.Get("instance.modify.target.catalogEmpty");
                return;
            }

            TargetStatus = string.Empty;
            await RefreshTargetAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 页面关闭或重新选择目标时取消；没有需要回写的状态。
        }
        catch (Exception exception)
        {
            if (IsDisposed || generation != _generation)
            {
                return;
            }

            TargetVersions.Clear();
            TargetStatus = _localizer.Get("instance.modify.target.catalogFailed", exception.Message);
        }
        finally
        {
            OnPropertyChanged(nameof(HasTargetStatus));
        }
    }

    /// <summary>
    /// 按目标版本与加载器读取可验证的加载器版本、Fabric API 与 OptiFine 选项。
    /// 每次选择变化都会取消上一次读取，旧结果不会覆盖新选择。
    /// </summary>
    private async Task RefreshTargetAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        _targetCancellation?.Cancel();
        _targetCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _targetCancellation = cancellation;
        var token = cancellation.Token;
        var generation = ++_generation;

        ResetPlan();

        var version = SelectedTargetVersion?.Value ?? string.Empty;
        var loaderId = SelectedTargetLoader?.Id ?? LoaderKinds.Vanilla;

        LoaderVersions.Clear();
        OptiFineVersions.Clear();
        SetSelectedLoaderVersion(null);
        _targetFabricApiVersion = string.Empty;
        FabricApiText = string.Empty;
        OptiFineNote = string.Empty;
        TargetLoadError = string.Empty;
        TargetStatus = string.Empty;
        IsLoaderVersionRequired = !string.Equals(loaderId, LoaderKinds.Vanilla, StringComparison.Ordinal);
        RequiresProcessorJava = LoaderKinds.TryParse(loaderId, out var kind) && LoaderKinds.UsesInstaller(kind);
        SetSelectedOptiFine(null);
        NotifyComponentPresentation();
        OnPropertyChanged(nameof(HasFabricApiNote));
        OnPropertyChanged(nameof(HasOptiFineNote));

        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        IsLoadingTarget = true;
        GeneratePlanCommand.NotifyCanExecuteChanged();
        TargetStatus = _localizer.Get("instance.modify.target.loading");
        OnPropertyChanged(nameof(HasTargetStatus));

        try
        {
            if (string.Equals(loaderId, LoaderKinds.Vanilla, StringComparison.Ordinal))
            {
                return;
            }

            var loaderVersions = await LoadLoaderVersionsAsync(loaderId, version, token).ConfigureAwait(true);
            if (!IsCurrent(generation))
            {
                return;
            }

            if (!loaderVersions.IsSuccess)
            {
                TargetLoadError = loaderVersions.Error?.Message ?? string.Empty;
                TargetStatus = _localizer.Get("instance.modify.target.loadFailed", TargetLoadError);
                return;
            }

            foreach (var info in loaderVersions.Value!)
            {
                LoaderVersions.Add(new VersionChoiceViewModel(
                    info.Version,
                    FormatVersionWithDate(info.Version, info.ReleasedAt)));
            }

            if (LoaderVersions.Count == 0)
            {
                TargetLoadError = _localizer.Get("instance.modify.target.loaderVersionEmpty");
                TargetStatus = TargetLoadError;
                return;
            }

            // 保持当前版本时选中当前加载器版本；否则使用目录中的第一个（最新）版本。
            var sameTarget = string.Equals(version, _state.Model.GameVersion, StringComparison.Ordinal) &&
                             string.Equals(loaderId, CurrentLoaderId, StringComparison.Ordinal);
            SetSelectedLoaderVersion((sameTarget
                                         ? LoaderVersions.FirstOrDefault(choice =>
                                             string.Equals(choice.Value, _state.Model.LoaderVersion, StringComparison.Ordinal))
                                         : null)
                                     ?? LoaderVersions[0]);

            if (string.Equals(loaderId, LoaderKinds.Fabric, StringComparison.Ordinal))
            {
                await LoadFabricApiAsync(version, token).ConfigureAwait(true);
            }

            if (string.Equals(loaderId, LoaderKinds.Forge, StringComparison.Ordinal) ||
                string.Equals(loaderId, LoaderKinds.Cleanroom, StringComparison.Ordinal))
            {
                await LoadOptiFineAsync(version, token).ConfigureAwait(true);
            }

            if (!IsCurrent(generation))
            {
                return;
            }

            TargetStatus = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 选择已变化：丢弃结果即可。
        }
        catch (Exception exception)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            TargetLoadError = exception.Message;
            TargetStatus = _localizer.Get("instance.modify.target.loadFailed", exception.Message);
        }
        finally
        {
            if (IsCurrent(generation))
            {
                IsLoadingTarget = false;
                OnPropertyChanged(nameof(HasTargetStatus));
                OnPropertyChanged(nameof(TargetComponentsText));
                GeneratePlanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private async Task<Result<IReadOnlyList<LoaderVersionInfo>>> LoadLoaderVersionsAsync(
        string loaderId,
        string version,
        CancellationToken token)
    {
        if (!LoaderKinds.TryParse(loaderId, out var kind))
        {
            return Result<IReadOnlyList<LoaderVersionInfo>>.Fail(
                "modify.unsupported_loader",
                _localizer.Get("instance.modify.block.unsupportedLoader"));
        }

        return kind switch
        {
            LoaderKind.Fabric => await _loaderCatalog.GetFabricLoadersAsync(version, token).ConfigureAwait(true),
            LoaderKind.Forge => await _loaderCatalog.GetForgeVersionsAsync(version, token).ConfigureAwait(true),
            LoaderKind.NeoForge => await _loaderCatalog.GetNeoForgeVersionsAsync(version, token).ConfigureAwait(true),
            _ => await _loaderCatalog.GetCleanroomVersionsAsync(version, token).ConfigureAwait(true),
        };
    }

    private async Task LoadFabricApiAsync(string version, CancellationToken token)
    {
        var apiVersions = await _loaderCatalog.GetFabricApiVersionsAsync(version, token).ConfigureAwait(true);
        if (token.IsCancellationRequested || IsDisposed)
        {
            return;
        }

        if (!apiVersions.IsSuccess)
        {
            FabricApiText = _localizer.Get("instance.modify.target.fabricApiFailed", apiVersions.Error?.Message ?? string.Empty);
            OnPropertyChanged(nameof(HasFabricApiNote));
            NotifyComponentPresentation();
            return;
        }

        var latest = apiVersions.Value!.FirstOrDefault();
        if (latest is null)
        {
            FabricApiText = _localizer.Get("instance.modify.target.fabricApiNone");
            OnPropertyChanged(nameof(HasFabricApiNote));
            NotifyComponentPresentation();
            return;
        }

        // ADR 0007：Fabric API 默认使用目标组合下最新的兼容版本，并明确展示；
        // 已记录的当前版本若仍在目录中则保持不变，避免无关的组件升级。
        var keepCurrent = apiVersions.Value!.FirstOrDefault(item =>
            string.Equals(item.Version, _state.Model.FabricApiVersion, StringComparison.OrdinalIgnoreCase));
        var selected = keepCurrent ?? latest;
        _targetFabricApiVersion = selected.Version;
        FabricApiText = keepCurrent is not null
            ? _localizer.Get("instance.modify.target.fabricApiCurrent", selected.Version)
            : _localizer.Get("instance.modify.target.fabricApiLatest", selected.Version);
        OnPropertyChanged(nameof(HasFabricApiNote));
        NotifyComponentPresentation();
    }

    private async Task LoadOptiFineAsync(string version, CancellationToken token)
    {
        var optiFine = await _loaderCatalog.GetOptiFineVersionsAsync(version, token).ConfigureAwait(true);
        if (token.IsCancellationRequested || IsDisposed)
        {
            return;
        }

        var none = new VersionChoiceViewModel(null, _localizer.Get("instance.modify.target.optiFineNone"));
        OptiFineVersions.Add(none);

        if (!optiFine.IsSuccess)
        {
            OptiFineNote = _localizer.Get("instance.modify.target.optiFineFailed", optiFine.Error?.Message ?? string.Empty);
            SetSelectedOptiFine(none);
            OnPropertyChanged(nameof(HasOptiFineNote));
            NotifyComponentPresentation();
            return;
        }

        foreach (var item in optiFine.Value!)
        {
            OptiFineVersions.Add(new VersionChoiceViewModel(
                item.Key,
                FormatVersionWithDate(item.Key, item.ReleasedAt)));
        }

        var hasOptiFineFiles = optiFine.Value!.Count > 0;
        var current = _state.Model.OptiFineVersion;
        if (string.IsNullOrWhiteSpace(current))
        {
            SetSelectedOptiFine(none);
            if (!hasOptiFineFiles)
            {
                OptiFineNote = _localizer.Get("instance.modify.target.optiFineNoFiles");
            }

            OnPropertyChanged(nameof(HasOptiFineNote));
            NotifyComponentPresentation();
            return;
        }

        var preserved = OptiFineVersions.FirstOrDefault(choice =>
            string.Equals(choice.Value, current, StringComparison.OrdinalIgnoreCase));
        if (preserved is not null)
        {
            SetSelectedOptiFine(preserved);
        }
        else
        {
            // OptiFine 只能由用户显式选择（ADR 0007）；目标版本下没有当前版本的文件时
            // 不自动替换为其他版本，而是如实说明需要手动选择。
            SetSelectedOptiFine(none);
            OptiFineNote = _localizer.Get("instance.modify.target.optiFineMissingForTarget", current);
        }

        OnPropertyChanged(nameof(HasOptiFineNote));
        NotifyComponentPresentation();
    }

    private void SetSelectedOptiFine(VersionChoiceViewModel? choice)
    {
        _suppressTargetReload = true;
        try
        {
            SelectedTargetOptiFine = choice;
        }
        finally
        {
            _suppressTargetReload = false;
        }
    }

    /// <summary>切换目标加载器时清空旧版本的加载器版本，避免把上一个加载器的版本带到新组合。</summary>
    private void SetSelectedLoaderVersion(VersionChoiceViewModel? choice)
    {
        _suppressTargetReload = true;
        try
        {
            SelectedTargetLoaderVersion = choice;
        }
        finally
        {
            _suppressTargetReload = false;
        }
    }

    private bool IsCurrent(int generation) => !IsDisposed && generation == _generation;

    private void RebuildLoaderChoices()
    {
        var version = SelectedTargetVersion?.Value;
        var keep = SelectedTargetLoader?.Id;

        LoaderChoices.Clear();
        foreach (var id in LoaderOrder)
        {
            if (string.Equals(id, LoaderKinds.Cleanroom, StringComparison.Ordinal) &&
                !string.Equals(version, "1.12.2", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            LoaderChoices.Add(new LoaderChoiceViewModel(id, LoaderTitle(id)));
        }

        var current = CurrentLoaderId;
        SelectedTargetLoader = LoaderChoices.FirstOrDefault(choice => string.Equals(choice.Id, keep, StringComparison.Ordinal))
                               ?? LoaderChoices.FirstOrDefault(choice => string.Equals(choice.Id, current, StringComparison.Ordinal))
                               ?? LoaderChoices[0];

        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(version) &&
            !string.Equals(version, "1.12.2", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(_localizer.Get("instance.modify.target.cleanroomExcluded"));
        }

        if (LoaderKinds.TryParse(_state.Model.Loader, out _) is false)
        {
            notes.Add(_localizer.Get("instance.modify.target.currentLoaderUnsupported", _state.Model.Loader));
        }

        LoaderFilterNote = string.Join(" ", notes);
        OnPropertyChanged(nameof(IsFabricTarget));
        OnPropertyChanged(nameof(IsOptiFineTarget));
    }

    private string LoaderTitle(string id) => _localizer.Get(id switch
    {
        LoaderKinds.Fabric => "instances.loader.fabric",
        LoaderKinds.Forge => "instances.loader.forge",
        LoaderKinds.NeoForge => "instances.loader.neoforge",
        LoaderKinds.Cleanroom => "instances.loader.cleanroom",
        _ => "instances.loader.vanilla",
    });

    private string FormatVersionWithDate(string version, DateTimeOffset? releasedAt)
    {
        var date = releasedAt?.ToLocalTime().ToString("yyyy-MM-dd")
                   ?? _localizer.Get("instances.versionDateUnknown");
        return _localizer.Get("instances.versionWithDate", version, date);
    }

    private static string NormalizeLoader(string? loader) =>
        LoaderKinds.TryParse(loader, out var kind) ? LoaderKinds.ToId(kind) : (loader ?? string.Empty).Trim().ToLowerInvariant();

    partial void OnSelectedTargetVersionChanged(VersionChoiceViewModel? value)
    {
        if (_suppressTargetReload || IsDisposed)
        {
            return;
        }

        _suppressTargetReload = true;
        try
        {
            RebuildLoaderChoices();
        }
        finally
        {
            _suppressTargetReload = false;
        }

        NotifyComponentPresentation();
        PendingTargetRefresh = RefreshTargetAsync();
    }

    partial void OnSelectedTargetLoaderChanged(LoaderChoiceViewModel? value)
    {
        if (_suppressTargetReload || IsDisposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsFabricTarget));
        OnPropertyChanged(nameof(IsOptiFineTarget));
        NotifyComponentPresentation();
        PendingTargetRefresh = RefreshTargetAsync();
    }

    partial void OnSelectedTargetLoaderVersionChanged(VersionChoiceViewModel? value)
    {
        if (!_suppressTargetReload)
        {
            ResetPlan();
        }
    }

    partial void OnSelectedTargetOptiFineChanged(VersionChoiceViewModel? value)
    {
        OnPropertyChanged(nameof(TargetComponentsText));
        OnPropertyChanged(nameof(TargetOptiFineText));
        if (!_suppressTargetReload)
        {
            ResetPlan();
        }
    }

    /// <summary>
    /// 组件区域的可见性只由“目标加载器是否支持该组件”和“目标版本是否有可选文件”决定；
    /// 不支持的组合不显示可编辑控件，也不伪造版本号。
    /// </summary>
    private void NotifyComponentPresentation()
    {
        OnPropertyChanged(nameof(IsFabricTarget));
        OnPropertyChanged(nameof(IsOptiFineTarget));
        OnPropertyChanged(nameof(ShowComponentsSection));
        OnPropertyChanged(nameof(ShowFabricApiRow));
        OnPropertyChanged(nameof(ShowOptiFineRow));
        OnPropertyChanged(nameof(ShowOptiFineControl));
        OnPropertyChanged(nameof(TargetFabricApiText));
        OnPropertyChanged(nameof(TargetOptiFineText));
    }

    partial void OnPlanStateChanged(InstanceModifyPlanState value)
    {
        OnPropertyChanged(nameof(PlanStateText));
        OnPropertyChanged(nameof(IsPlanExecutable));
        OnPropertyChanged(nameof(IsPlanBlocked));
        OnPropertyChanged(nameof(IsPlanNeedsAction));
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsExecuting));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailedRolledBack));
        OnPropertyChanged(nameof(IsFailedNeedsRecovery));
        OnPropertyChanged(nameof(IsFailedBeforeChange));
        OnPropertyChanged(nameof(IsTargetEditable));
        OnPropertyChanged(nameof(HasExecutionResult));
        OnPropertyChanged(nameof(HasExecutionStage));
        OnPropertyChanged(nameof(HasSnapshotSummary));
        OnPropertyChanged(nameof(HasDiagnosticNote));
        OnPropertyChanged(nameof(HasDiagnosticFile));
        ExecuteCommand.NotifyCanExecuteChanged();
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
    }

    partial void OnExecutionStageChanged(string value) => OnPropertyChanged(nameof(HasExecutionStage));

    partial void OnExecutionResultChanged(string value) => OnPropertyChanged(nameof(HasExecutionResult));

    partial void OnSnapshotSummaryChanged(string value) => OnPropertyChanged(nameof(HasSnapshotSummary));

    partial void OnDiagnosticNoteChanged(string value)
    {
        OnPropertyChanged(nameof(HasDiagnosticNote));
        OnPropertyChanged(nameof(HasDiagnosticFile));
    }

    partial void OnRestoreStatusChanged(string value) => OnPropertyChanged(nameof(HasRestoreStatus));

    partial void OnTargetStatusChanged(string value) => OnPropertyChanged(nameof(HasTargetStatus));

    partial void OnLoaderFilterNoteChanged(string value) => OnPropertyChanged(nameof(HasLoaderFilterNote));

    partial void OnFabricApiTextChanged(string value) => OnPropertyChanged(nameof(HasFabricApiNote));

    partial void OnOptiFineNoteChanged(string value) => OnPropertyChanged(nameof(HasOptiFineNote));

    private bool CanGeneratePlan() =>
        !IsDisposed &&
        !IsLoadingTarget &&
        !IsExecuting &&
        !IsRestoring &&
        PlanState != InstanceModifyPlanState.Checking;

    /// <summary>
    /// 生成只读变更计划。前置检查未通过时只显示准确状态与原因；
    /// 本方法不调用任何写服务，也不创建快照或下载产物。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGeneratePlan))]
    private async Task GeneratePlanAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        _planCancellation?.Cancel();
        _planCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _planCancellation = cancellation;
        var token = cancellation.Token;
        var generation = _generation;

        PlanItems.Clear();
        ActionItems.Clear();
        Blockers.Clear();
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasActionItems));
        OnPropertyChanged(nameof(HasBlockers));

        // 重新生成计划会替换上一次的结论；但“需要恢复”的快照是恢复材料，必须保留。
        if (!IsFailedNeedsRecovery)
        {
            ClearExecutionResult();
        }

        PlanState = InstanceModifyPlanState.Checking;
        PlanStateDetail = ModifyPlanPresentation.StateDetail(InstanceModifyPlanState.Checking, _localizer);

        try
        {
            var plan = await BuildPlanAsync(token).ConfigureAwait(true);
            if (!IsCurrent(generation) || token.IsCancellationRequested)
            {
                return;
            }

            ApplyPlan(plan);
        }
        catch (OperationCanceledException)
        {
            // 选择变化或页面关闭：保留未检查状态，不显示过期计划。
        }
        catch (Exception exception)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            PlanItems.Clear();
            ActionItems.Clear();
            Blockers.Clear();
            Blockers.Add(_localizer.Get("instance.modify.plan.failed", exception.Message));
            PlanState = InstanceModifyPlanState.NotExecutable;
            PlanStateDetail = ModifyPlanPresentation.StateDetail(InstanceModifyPlanState.NotExecutable, _localizer);
            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(HasActionItems));
            OnPropertyChanged(nameof(HasBlockers));
        }
    }

    private void ApplyPlan(PlanBuild plan)
    {
        PlanItems.Clear();
        foreach (var item in plan.Items)
        {
            PlanItems.Add(item);
        }

        ActionItems.Clear();
        foreach (var action in plan.Actions)
        {
            ActionItems.Add(action);
        }

        Blockers.Clear();
        foreach (var blocker in plan.Blockers)
        {
            Blockers.Add(blocker);
        }

        // 只有“可执行”的计划才允许提交；同时记录生成计划时的目标组合，
        // 任何选择变化都会重置计划，因此提交前可以证明目标没有被换掉。
        _planTarget = plan.State == InstanceModifyPlanState.Executable ? CaptureTarget() : null;

        PlanState = plan.State;
        PlanStateDetail = ModifyPlanPresentation.StateDetail(plan.State, _localizer);
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasActionItems));
        OnPropertyChanged(nameof(HasBlockers));
    }

    private void ResetPlan()
    {
        _planCancellation?.Cancel();

        _planTarget = null;
        PlanItems.Clear();
        ActionItems.Clear();
        Blockers.Clear();
        ClearExecutionResult();
        PlanState = InstanceModifyPlanState.NotChecked;
        PlanStateDetail = ModifyPlanPresentation.StateDetail(InstanceModifyPlanState.NotChecked, _localizer);
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasActionItems));
        OnPropertyChanged(nameof(HasBlockers));
    }

    /// <summary>清理上一次执行/恢复的结果，避免新计划沿用旧结论。</summary>
    private void ClearExecutionResult()
    {
        ExecutionStage = string.Empty;
        ExecutionResult = string.Empty;
        SnapshotId = string.Empty;
        SnapshotSummary = string.Empty;
        DiagnosticNote = string.Empty;
        RestoreStatus = string.Empty;
        _diagnosticPath = null;
    }

    private ModifyExecutionTarget CaptureTarget() => new(
        SelectedTargetVersion?.Value ?? string.Empty,
        SelectedTargetLoader?.Id ?? LoaderKinds.Vanilla,
        IsLoaderVersionRequired ? SelectedTargetLoaderVersion?.Value : null,
        IsFabricTarget ? _targetFabricApiVersion : null,
        SelectedTargetOptiFine?.Value);

    /// <summary>
    /// 提交入口只在“计划可执行 + 目标未变化 + 不在执行/恢复中”时可用（`instance-modify.md` §3、§6）。
    /// </summary>
    private bool CanExecutePlan() =>
        !IsDisposed &&
        !IsExecuting &&
        !IsRestoring &&
        PlanState == InstanceModifyPlanState.Executable &&
        _planTarget is not null &&
        _planTarget == CaptureTarget();

    private bool CanRestoreSnapshot() =>
        !IsDisposed &&
        !IsRestoring &&
        PlanState == InstanceModifyPlanState.FailedNeedsRecovery &&
        !string.IsNullOrWhiteSpace(SnapshotId);

    /// <summary>
    /// 受控执行（`instance-modify.md` §6）：确认 → 创建并校验完整实例快照 → 原地变更。
    /// 不提供强制取消入口；页面关闭不会取消已提交的任务。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecutePlan))]
    private async Task ExecuteAsync()
    {
        if (IsDisposed || !CanExecutePlan() || _planTarget is not { } target)
        {
            return;
        }

        var confirmed = await ConfirmExecutionAsync(target).ConfigureAwait(true);
        if (!confirmed || IsDisposed)
        {
            return;
        }

        // 计划可能是在几分钟前生成的：写入前重新核对运行中的游戏进程与写入任务。
        var running = FindRunningSessions(InstanceId);
        var writeTasks = FindActiveWriteOperations();
        if (running.Count > 0 || writeTasks > 0)
        {
            PlanState = InstanceModifyPlanState.NeedsAction;
            PlanStateDetail = running.Count > 0
                ? _localizer.Get("instance.modify.block.runningChanged")
                : _localizer.Get("instance.modify.block.tasksChanged");
            if (running.Count > 0)
            {
                ActionItems.Add(_localizer.Get("instance.modify.process.running", running[0]));
            }

            if (writeTasks > 0)
            {
                ActionItems.Add(_localizer.Get("instance.modify.tasks.conflict", writeTasks));
            }

            OnPropertyChanged(nameof(HasActionItems));
            return;
        }

        ClearExecutionResult();
        PlanState = InstanceModifyPlanState.Executing;
        PlanStateDetail = ModifyPlanPresentation.StateDetail(InstanceModifyPlanState.Executing, _localizer);
        ExecutionStage = _localizer.Get("instance.modify.execution.starting");

        await RunExecutionAsync(target).ConfigureAwait(true);
    }

    private async Task RunExecutionAsync(ModifyExecutionTarget target)
    {
        try
        {
            // 1. 可验证的完整实例快照：失败/不完整时不得开始安装。
            ExecutionStage = _localizer.Get("instance.modify.execution.snapshot");
            var snapshotCreated = await CreateSnapshotAsync().ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            if (!snapshotCreated.IsSuccess)
            {
                FailBeforeChange(
                    _localizer.Get("instance.modify.execution.snapshotFailed", snapshotCreated.Error?.Message ?? string.Empty));
                return;
            }

            var created = snapshotCreated.Value!;
            SnapshotId = created.SnapshotId;
            SnapshotSummary = _localizer.Get(
                "instance.modify.snapshot.created",
                created.SnapshotId,
                created.FileCount);
            ExecutionStage = _localizer.Get("instance.modify.execution.verify");

            var verified = await _snapshots.VerifyAsync(InstanceId, created.SnapshotId).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            if (!verified.IsSuccess)
            {
                FailBeforeChange(_localizer.Get(
                    "instance.modify.execution.snapshotRejected",
                    verified.Error?.Message ?? string.Empty));
                return;
            }

            var verification = verified.Value!;
            if (!verification.IsValid)
            {
                SnapshotSummary = _localizer.Get("instance.modify.snapshot.invalid", created.FileCount);
                FailBeforeChange(_localizer.Get(
                    "instance.modify.execution.snapshotRejected",
                    _localizer.Get("instance.modify.execution.snapshotIncomplete")));
                return;
            }

            SnapshotSummary = _localizer.Get(
                "instance.modify.snapshot.verified",
                created.SnapshotId,
                verification.VerifiedObjects);

            // 2. 原地变更：安装服务自己登记 Operation 并转发进度，本页不重复登记任务。
            ExecutionStage = _localizer.Get("instance.modify.execution.install");
            var progress = new Progress<InstallProgress>(update =>
            {
                if (IsDisposed || !IsExecuting)
                {
                    return;
                }

                ExecutionStage = update.TotalFiles > 0
                    ? $"{update.Message} ({update.CompletedFiles}/{update.TotalFiles})"
                    : update.Message;
            });

            var outcome = await _install
                .ReconfigureAsync(
                    InstanceId,
                    target.Version,
                    target.Loader,
                    target.LoaderVersion,
                    target.FabricApiVersion,
                    target.OptiFineVersion,
                    progress)
                .ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            if (outcome.IsSuccess)
            {
                ExecutionStage = string.Empty;
                ExecutionResult = _localizer.Get("instance.modify.execution.succeeded", DescribeTarget(target));
                await RefreshWorkspaceAsync().ConfigureAwait(true);
                if (IsDisposed)
                {
                    return;
                }

                PlanState = InstanceModifyPlanState.Succeeded;
                PlanStateDetail = ModifyPlanPresentation.StateDetail(InstanceModifyPlanState.Succeeded, _localizer);
                return;
            }

            ApplyFailure(outcome.Failure!);
        }
        catch (Exception exception)
        {
            if (IsDisposed)
            {
                return;
            }

            // 没有领域失败结果就无法证明回滚状态：按“需要恢复”处理，并保留已校验的快照。
            ExecutionStage = string.Empty;
            ExecutionResult = _localizer.Get("instance.modify.execution.unknown", exception.Message);
            DiagnosticNote = _localizer.Get("instance.modify.diagnostic.unavailable");
            PlanState = InstanceModifyPlanState.FailedNeedsRecovery;
            PlanStateDetail = _localizer.Get("instance.modify.state.failedNeedsRecoveryDetail");
        }
    }

    /// <summary>创建变更前完整快照；进度登记到统一 Operation，失败时返回真实原因。</summary>
    private async Task<Result<SnapshotManifest>> CreateSnapshotAsync()
    {
        var progress = new Progress<SnapshotProgress>(update =>
        {
            if (IsDisposed || !IsExecuting)
            {
                return;
            }

            ExecutionStage = update.Total > 0
                ? _localizer.Get("instance.modify.execution.snapshotProgress", update.Processed, update.Total)
                : _localizer.Get("instance.modify.execution.snapshot");
        });

        using var bridge = new OperationProgressBridge<SnapshotProgress>(
            _operations,
            new OperationStart
            {
                Kind = OperationKind.Backup,
                Title = _localizer.Get("instance.modify.execution.snapshotTask", InstanceName),
                StageId = "snapshot",
                CanCancel = false,
                CanRetry = false,
            },
            progress,
            MapSnapshotProgress);

        var result = await _snapshots
            .CreateAsync(InstanceId, new SnapshotCreateOptions(SnapshotScope.Full), bridge)
            .ConfigureAwait(true);

        if (!result.IsSuccess)
        {
            bridge.Fail(new OperationError(
                OperationErrorKind.LocalIO,
                result.Error?.Code ?? "snapshot.failed",
                result.Error?.Message ?? string.Empty));
        }
        else if (result.Value!.Status != SnapshotStatus.Complete)
        {
            bridge.Fail(new OperationError(
                OperationErrorKind.LocalIO,
                "snapshot.incomplete",
                _localizer.Get("instance.modify.execution.snapshotIncomplete")));
        }
        else
        {
            bridge.Complete();
        }

        return result;
    }

    /// <summary>执行前中止：实例还没有写入，快照与已生成的计划保留用于重试或放弃。</summary>
    private void FailBeforeChange(string message)
    {
        ExecutionStage = string.Empty;
        ExecutionResult = message;
        PlanState = InstanceModifyPlanState.FailedBeforeChange;
        PlanStateDetail = ModifyPlanPresentation.StateDetail(InstanceModifyPlanState.FailedBeforeChange, _localizer);
    }

    /// <summary>按领域返回的回滚信号显示结果；没有信号时不得显示「已回滚」。</summary>
    private void ApplyFailure(InstallFailure failure)
    {
        ExecutionStage = string.Empty;
        _diagnosticPath = failure.DiagnosticPath;
        DiagnosticNote = failure.DiagnosticPath is not null
            ? _localizer.Get("instance.modify.diagnostic.written")
            : _localizer.Get("instance.modify.diagnostic.unavailable");

        var message = DescribeInstallError(failure.Error);
        if (failure.RecoveryRequired)
        {
            var problems = failure.Rollback.Failures.Count + (failure.MetadataRestored ? 0 : 1);
            ExecutionResult = _localizer.Get(
                "instance.modify.execution.failedNeedsRecovery",
                message,
                problems);
            PlanState = InstanceModifyPlanState.FailedNeedsRecovery;
            PlanStateDetail = _localizer.Get("instance.modify.state.failedNeedsRecoveryDetail");
            return;
        }

        ExecutionResult = _localizer.Get("instance.modify.execution.failedRolledBack", message);
        PlanState = InstanceModifyPlanState.FailedRolledBack;
        PlanStateDetail = _localizer.Get("instance.modify.state.failedRolledBackDetail");
    }

    /// <summary>只展示不含本地路径的错误文案；其余细节交给诊断文件。</summary>
    private string DescribeInstallError(EclError? error)
    {
        if (error is null)
        {
            return _localizer.Get("instance.modify.execution.failedGeneric");
        }

        return SafeInstallErrorCodes.Contains(error.Code)
            ? error.Message
            : _localizer.Get("instance.modify.execution.failedGeneric");
    }

    private string DescribeTarget(ModifyExecutionTarget target)
    {
        var loader = LoaderText(target.Loader, target.LoaderVersion);
        var components = DescribeTargetComponents(target.Loader, target.OptiFineVersion);
        return _localizer.Get(
            "instance.modify.execution.target",
            target.Version,
            loader,
            components);
    }

    /// <summary>受控恢复：只使用本次变更前已校验的快照，并在恢复前创建安全快照。</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreSnapshot))]
    private async Task RestoreSnapshotAsync()
    {
        var snapshotId = SnapshotId;
        if (IsDisposed || string.IsNullOrWhiteSpace(snapshotId) || !CanRestoreSnapshot())
        {
            return;
        }

        var confirmed = await ConfirmRestoreAsync(snapshotId).ConfigureAwait(true);
        if (!confirmed || IsDisposed)
        {
            return;
        }

        IsRestoring = true;
        RestoreStatus = _localizer.Get("instance.modify.restore.running");
        try
        {
            var result = await _snapshots.RestoreAsync(InstanceId, snapshotId).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                RestoreStatus = _localizer.Get(
                    "instance.modify.restore.failed",
                    result.Error?.Message ?? string.Empty);
                return;
            }

            var restore = result.Value!;
            var warnings = restore.Warnings.Count > 0
                ? " " + _localizer.Get("instance.modify.restore.warnings", restore.Warnings.Count)
                : string.Empty;
            RestoreStatus = _localizer.Get(
                "instance.modify.restore.succeeded",
                snapshotId,
                restore.Written,
                restore.Deleted) + warnings;

            await RefreshWorkspaceAsync().ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            ExecutionResult = _localizer.Get("instance.modify.restore.result", snapshotId);
            PlanState = InstanceModifyPlanState.FailedRolledBack;
            PlanStateDetail = _localizer.Get("instance.modify.state.restoredDetail", snapshotId);
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                RestoreStatus = _localizer.Get("instance.modify.restore.failed", exception.Message);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                IsRestoring = false;
            }
        }
    }

    private async Task RefreshWorkspaceAsync()
    {
        if (WorkspaceRefresh is not { } refresh)
        {
            return;
        }

        try
        {
            await refresh().ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            NotifyIdentityChanged();
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                DiagnosticNote = _localizer.Get("instance.modify.refresh.failed", exception.Message);
            }
        }
    }

    /// <summary>身份快照被替换后重新读取页面顶部的当前值。</summary>
    private void NotifyIdentityChanged()
    {
        OnPropertyChanged(nameof(InstanceName));
        OnPropertyChanged(nameof(InstanceMeta));
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(CurrentLoaderText));
        OnPropertyChanged(nameof(CurrentLoaderVersionText));
        OnPropertyChanged(nameof(CurrentOptiFineText));
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(CurrentComponentsText));
        OnPropertyChanged(nameof(TargetComponentsText));
    }

    private async Task<bool> ConfirmExecutionAsync(ModifyExecutionTarget target)
    {
        if (!_dialogs.IsAvailable)
        {
            // 无法展示确认时不写入：宁可拒绝，也不在没有确认的情况下变更实例。
            ExecutionResult = _localizer.Get("instance.modify.confirm.unavailable");
            OnPropertyChanged(nameof(HasExecutionResult));
            return false;
        }

        var impacts = CountImpacts();
        var content = new ModifyConfirmDialogViewModel(
            [
                _localizer.Get("instance.modify.confirm.target", DescribeTarget(target)),
                _localizer.Get(
                    "instance.modify.confirm.impact",
                    impacts.Certain,
                    impacts.Possible,
                    impacts.Unknown,
                    impacts.Warning),
            ],
            _localizer.Get("instance.modify.confirm.snapshot", SnapshotScopeText()),
            _localizer.Get("instance.modify.confirm.notice"),
            _localizer.Get("instance.modify.confirm.execute"),
            _localizer.Get("dialog.cancel"));

        var result = await _dialogs.ShowAsync(new DialogRequest
        {
            Title = _localizer.Get("instance.modify.confirm.title"),
            Content = content,
            InitialFocus = DialogInitialFocus.Cancel,
            Severity = DialogSeverity.Warning,
            MinWidth = 420,
            MaxWidth = 620,
            MaxHeight = 640,
        }).ConfigureAwait(true);

        return result.IsPrimary;
    }

    private async Task<bool> ConfirmRestoreAsync(string snapshotId)
    {
        if (!_dialogs.IsAvailable)
        {
            RestoreStatus = _localizer.Get("instance.modify.confirm.unavailable");
            return false;
        }

        var result = await _dialogs.ShowAsync(new DialogRequest
        {
            Title = _localizer.Get("instance.modify.restore.title"),
            Content = new MessageDialogViewModel(
                _localizer.Get("instance.modify.restore.confirm", snapshotId, SnapshotScopeText()),
                _localizer.Get("instance.modify.restore.confirmYes"),
                cancelText: _localizer.Get("dialog.cancel"),
                severity: DialogSeverity.Warning,
                isDestructive: true),
            InitialFocus = DialogInitialFocus.Cancel,
            Severity = DialogSeverity.Warning,
        }).ConfigureAwait(true);

        return result.IsPrimary;
    }

    private (int Certain, int Possible, int Unknown, int Warning) CountImpacts()
    {
        var certain = PlanItems.Count(item => item.Impact == ModifyPlanImpact.Certain);
        var possible = PlanItems.Count(item => item.Impact == ModifyPlanImpact.Possible);
        var unknown = PlanItems.Count(item => item.Impact == ModifyPlanImpact.Unknown);
        var warning = PlanItems.Count(item => item.Impact == ModifyPlanImpact.Warning);
        return (certain, possible, unknown, warning);
    }

    private string SnapshotScopeText() => _localizer.Get("instance.modify.snapshot.scope");

    /// <summary>打开安装诊断文件所在目录；页面不展示路径本身。</summary>
    [RelayCommand]
    private void OpenDiagnosticsFolder()
    {
        if (_diagnosticPath is not { } path || IsDisposed)
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DiagnosticNote = _localizer.Get("instance.modify.diagnostic.openFailed");
        }
    }

    private static OperationProgressUpdate MapSnapshotProgress(SnapshotProgress progress) => new()
    {
        StageId = "snapshot",
        Completed = progress.Processed,
        Total = progress.Total > 0 ? progress.Total : null,
    };

    private async Task<PlanBuild> BuildPlanAsync(CancellationToken token)
    {
        var model = _state.Model;
        var targetVersion = SelectedTargetVersion?.Value ?? string.Empty;
        var targetLoader = SelectedTargetLoader?.Id ?? LoaderKinds.Vanilla;
        var targetLoaderVersion = IsLoaderVersionRequired ? SelectedTargetLoaderVersion?.Value : null;
        var targetOptiFine = SelectedTargetOptiFine?.Value;
        var currentLoader = CurrentLoaderId;

        var items = new List<ModifyPlanItemViewModel>();
        var blockers = new List<string>();
        var actions = new List<string>();

        items.Add(PlanItem(
            ModifyPlanItemKind.Instance,
            "instance.modify.item.instance",
            _localizer.Get("instance.modify.detail.identity", model.Id),
            ModifyPlanImpact.Info));
        items.Add(PlanItem(
            ModifyPlanItemKind.Directory,
            "instance.modify.item.directory",
            _localizer.Get("instance.modify.detail.directory", _state.Item.PathText),
            ModifyPlanImpact.Info));

        var versionChanged = !string.Equals(targetVersion, model.GameVersion, StringComparison.Ordinal);
        var loaderChanged = !string.Equals(targetLoader, currentLoader, StringComparison.Ordinal);
        var loaderVersionChanged = IsLoaderVersionRequired &&
                                   !string.Equals(targetLoaderVersion, model.LoaderVersion, StringComparison.Ordinal);
        var optiFineChanged = !string.Equals(targetOptiFine, model.OptiFineVersion, StringComparison.Ordinal);

        items.Add(PlanItem(
            ModifyPlanItemKind.Version,
            "instance.modify.item.version",
            versionChanged
                ? _localizer.Get("instance.modify.detail.versionChange", model.GameVersion, targetVersion)
                : _localizer.Get("instance.modify.detail.versionUnchanged", model.GameVersion),
            versionChanged ? ModifyPlanImpact.Certain : ModifyPlanImpact.Info));

        items.Add(PlanItem(
            ModifyPlanItemKind.Loader,
            "instance.modify.item.loader",
            loaderChanged || loaderVersionChanged
                ? _localizer.Get(
                    "instance.modify.detail.loaderChange",
                    LoaderText(currentLoader, model.LoaderVersion),
                    LoaderText(targetLoader, targetLoaderVersion))
                : _localizer.Get("instance.modify.detail.loaderUnchanged", LoaderText(currentLoader, model.LoaderVersion)),
            loaderChanged || loaderVersionChanged ? ModifyPlanImpact.Certain : ModifyPlanImpact.Info));

        items.Add(PlanItem(
            ModifyPlanItemKind.Components,
            "instance.modify.item.components",
            _localizer.Get(
                "instance.modify.detail.components",
                CurrentComponentsText,
                DescribeTargetComponents(targetLoader, targetOptiFine)),
            ModifyPlanImpact.Info));

        if (!versionChanged && !loaderChanged && !loaderVersionChanged && !optiFineChanged)
        {
            actions.Add(_localizer.Get("instance.modify.block.noChange"));
            return new PlanBuild(InstanceModifyPlanState.NeedsAction, items, blockers, actions);
        }

        if (!Directory.Exists(_state.Item.PathText))
        {
            blockers.Add(_localizer.Get("instance.modify.block.directoryMissing"));
        }

        if (SelectedTargetVersion is null)
        {
            blockers.Add(_localizer.Get("instance.modify.block.versionMissing"));
        }

        if (SelectedTargetLoader is null)
        {
            blockers.Add(_localizer.Get("instance.modify.block.loaderMissing"));
        }

        if (string.Equals(targetLoader, LoaderKinds.Cleanroom, StringComparison.Ordinal) &&
            !string.Equals(targetVersion, "1.12.2", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(_localizer.Get("instance.modify.block.cleanroomVersion"));
        }

        if (!LoaderKinds.TryParse(targetLoader, out _))
        {
            blockers.Add(_localizer.Get("instance.modify.block.unsupportedLoader"));
        }

        if (IsLoaderVersionRequired && string.IsNullOrWhiteSpace(targetLoaderVersion))
        {
            blockers.Add(TargetLoadError.Length > 0
                ? _localizer.Get("instance.modify.block.loaderVersionUnavailable", TargetLoadError)
                : _localizer.Get("instance.modify.block.loaderVersionMissing"));
        }

        if (string.Equals(targetLoader, LoaderKinds.Fabric, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(_targetFabricApiVersion))
        {
            blockers.Add(_localizer.Get("instance.modify.block.fabricApiMissing"));
        }

        var java = await ResolveJavaAsync(token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();

        items.Add(DescribeCurrentJava(java));
        items.Add(DescribeTargetJava(targetVersion, targetLoader, java));

        if (RequiresProcessorJava && java?.Runtime is null)
        {
            blockers.Add(_localizer.Get("instance.modify.block.javaMissingForProcessors"));
        }

        var running = FindRunningSessions(model.Id);
        if (running.Count > 0)
        {
            actions.Add(_localizer.Get("instance.modify.process.running", running[0]));
        }

        items.Add(running.Count > 0
            ? PlanItem(
                ModifyPlanItemKind.RunningProcess,
                "instance.modify.item.process",
                _localizer.Get("instance.modify.process.running", running[0]),
                ModifyPlanImpact.Warning)
            : PlanItem(
                ModifyPlanItemKind.RunningProcess,
                "instance.modify.item.process",
                _localizer.Get("instance.modify.process.none"),
                ModifyPlanImpact.Info));

        var writeTasks = FindActiveWriteOperations();
        if (writeTasks > 0)
        {
            actions.Add(_localizer.Get("instance.modify.tasks.conflict", writeTasks));
        }

        items.Add(writeTasks > 0
            ? PlanItem(
                ModifyPlanItemKind.WriteTasks,
                "instance.modify.item.tasks",
                _localizer.Get("instance.modify.tasks.conflict", writeTasks),
                ModifyPlanImpact.Warning)
            : PlanItem(
                ModifyPlanItemKind.WriteTasks,
                "instance.modify.item.tasks",
                _localizer.Get("instance.modify.tasks.none"),
                ModifyPlanImpact.Info));

        // 只有目标版本或加载器真正变化时才可能改变内容兼容性；仅更新加载器小版本或
        // OptiFine 时不得把“当前已知的模组问题”包装成这次变更造成的影响。
        var contentEnvironmentChanged = versionChanged || loaderChanged;
        ModsImpact mods;
        if (contentEnvironmentChanged)
        {
            mods = await DescribeModsAsync(targetVersion, targetLoader, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            items.AddRange(mods.Items);

            var packs = await DescribePacksAsync(token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            items.AddRange(packs.Items);

            items.Add(PlanItem(
                ModifyPlanItemKind.DataPacks,
                "instance.modify.item.datapacks",
                _localizer.Get("instance.modify.datapacks.unknown"),
                ModifyPlanImpact.Unknown));
        }
        else
        {
            mods = new ModsImpact([], []);
            items.Add(PlanItem(
                ModifyPlanItemKind.Mods,
                "instance.modify.item.mods",
                _localizer.Get("instance.modify.mods.unchanged"),
                ModifyPlanImpact.Info));
            items.Add(PlanItem(
                ModifyPlanItemKind.Packs,
                "instance.modify.item.packs",
                _localizer.Get("instance.modify.packs.unchanged"),
                ModifyPlanImpact.Info));
            items.Add(PlanItem(
                ModifyPlanItemKind.DataPacks,
                "instance.modify.item.datapacks",
                _localizer.Get("instance.modify.datapacks.unchanged"),
                ModifyPlanImpact.Info));
        }

        foreach (var download in DescribeDownloads(targetLoader, targetOptiFine))
        {
            items.Add(download);
        }

        items.Add(PlanItem(
            ModifyPlanItemKind.Snapshot,
            "instance.modify.item.snapshot",
            string.Join(Environment.NewLine, new[]
            {
                _localizer.Get("instance.modify.snapshot.required"),
                _localizer.Get("instance.modify.snapshot.scope"),
            }),
            ModifyPlanImpact.Info));

        foreach (var manual in DescribeManualItems(targetLoader, targetOptiFine, mods))
        {
            items.Add(manual);
        }

        var state = blockers.Count > 0
            ? InstanceModifyPlanState.NotExecutable
            : actions.Count > 0
                ? InstanceModifyPlanState.NeedsAction
                : InstanceModifyPlanState.Executable;

        return new PlanBuild(state, items, blockers, actions);
    }

    private async Task<JavaSelectionResult?> ResolveJavaAsync(CancellationToken token)
    {
        try
        {
            MinecraftVersion? version = null;
            var resolved = await _versionResolver.ResolveAsync(_state.Model.VersionId, token).ConfigureAwait(true);
            if (resolved.IsSuccess)
            {
                version = resolved.Value;
            }

            return await _javaSelection.ResolveAsync(_state.Model, version, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private ModifyPlanItemViewModel DescribeCurrentJava(JavaSelectionResult? java)
    {
        if (java is null)
        {
            return PlanItem(
                ModifyPlanItemKind.JavaCurrent,
                "instance.modify.item.javaCurrent",
                _localizer.Get("instance.modify.java.currentUnavailable"),
                ModifyPlanImpact.Unknown);
        }

        var text = JavaSelectionPresentation.Describe(java, _localizer);
        var impact = java.Compatibility?.Status == JavaCompatibilityStatus.Compatible
            ? ModifyPlanImpact.Info
            : ModifyPlanImpact.Warning;
        return PlanItem(ModifyPlanItemKind.JavaCurrent, "instance.modify.item.javaCurrent", text, impact);
    }

    private ModifyPlanItemViewModel DescribeTargetJava(
        string targetVersion,
        string targetLoader,
        JavaSelectionResult? java)
    {
        var inferred = JavaRequirements.TryInferMinimumMajor(targetVersion);
        if (inferred is null)
        {
            return PlanItem(
                ModifyPlanItemKind.JavaTarget,
                "instance.modify.item.javaTarget",
                _localizer.Get("instance.modify.java.targetUnknown"),
                ModifyPlanImpact.Unknown);
        }

        var requirement = new JavaRequirement(inferred.Value);
        var compatibility = JavaCompatibility.Evaluate(requirement, java?.Runtime);
        var runtimeText = java?.Runtime?.Version?.Raw ?? java?.Runtime?.DisplayName;

        if (string.IsNullOrWhiteSpace(runtimeText))
        {
            return PlanItem(
                ModifyPlanItemKind.JavaTarget,
                "instance.modify.item.javaTarget",
                _localizer.Get("instance.modify.java.targetNoRuntime", requirement.Describe()),
                ModifyPlanImpact.Unknown);
        }

        var processorNote = RequiresProcessorJava
            ? Environment.NewLine + _localizer.Get("instance.modify.java.processorNote")
            : string.Empty;

        return compatibility.Status switch
        {
            JavaCompatibilityStatus.Compatible => PlanItem(
                ModifyPlanItemKind.JavaTarget,
                "instance.modify.item.javaTarget",
                _localizer.Get(
                    "instance.modify.java.targetInferredCompatible",
                    requirement.Describe(),
                    runtimeText) + processorNote,
                ModifyPlanImpact.Possible),
            JavaCompatibilityStatus.TooOld or JavaCompatibilityStatus.TooNew
                or JavaCompatibilityStatus.UnsupportedArchitecture => PlanItem(
                    ModifyPlanItemKind.JavaTarget,
                    "instance.modify.item.javaTarget",
                    _localizer.Get(
                        "instance.modify.java.targetInferredIncompatible",
                        requirement.Describe(),
                        runtimeText,
                        DescribeCompatibility(compatibility.Status)) + processorNote,
                    ModifyPlanImpact.Warning),
            _ => PlanItem(
                ModifyPlanItemKind.JavaTarget,
                "instance.modify.item.javaTarget",
                _localizer.Get(
                    "instance.modify.java.targetInferredUnknown",
                    requirement.Describe(),
                    runtimeText) + processorNote,
                ModifyPlanImpact.Unknown),
        };
    }

    private string DescribeCompatibility(JavaCompatibilityStatus status) => _localizer.Get(status switch
    {
        JavaCompatibilityStatus.TooOld => "instance.modify.java.status.tooOld",
        JavaCompatibilityStatus.TooNew => "instance.modify.java.status.tooNew",
        JavaCompatibilityStatus.UnsupportedArchitecture => "instance.modify.java.status.unsupportedArchitecture",
        _ => "instance.modify.java.status.unknown",
    });

    private async Task<ModsImpact> DescribeModsAsync(
        string targetVersion,
        string targetLoader,
        CancellationToken token)
    {
        var items = new List<ModifyPlanItemViewModel>();
        Result<ModScanReport> report;
        try
        {
            report = await _mods.ScanAsync(_state.InstanceId, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            items.Add(PlanItem(
                ModifyPlanItemKind.Mods,
                "instance.modify.item.mods",
                _localizer.Get("instance.modify.mods.scanFailed", exception.Message),
                ModifyPlanImpact.Unknown));
            return new ModsImpact(items, []);
        }

        if (!report.IsSuccess)
        {
            items.Add(PlanItem(
                ModifyPlanItemKind.Mods,
                "instance.modify.item.mods",
                _localizer.Get("instance.modify.mods.scanFailed", report.Error?.Message ?? string.Empty),
                ModifyPlanImpact.Unknown));
            return new ModsImpact(items, []);
        }

        var enabled = report.Value!.Mods.Where(entry => entry.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            items.Add(PlanItem(
                ModifyPlanItemKind.Mods,
                "instance.modify.item.mods",
                _localizer.Get("instance.modify.mods.none"),
                ModifyPlanImpact.Info));
            return new ModsImpact(items, []);
        }

        var incompatible = new List<string>();
        var unknown = new List<string>();
        var compatible = 0;
        foreach (var entry in enabled)
        {
            switch (ClassifyMod(entry, targetVersion, targetLoader))
            {
                case ModTargetImpact.Incompatible:
                    incompatible.Add(entry.FileName);
                    break;
                case ModTargetImpact.Compatible:
                    compatible++;
                    break;
                default:
                    unknown.Add(entry.FileName);
                    break;
            }
        }

        if (incompatible.Count > 0)
        {
            items.Add(PlanItem(
                ModifyPlanItemKind.Mods,
                "instance.modify.item.mods",
                _localizer.Get(
                    "instance.modify.mods.certainIncompatible",
                    incompatible.Count,
                    DescribeNames(incompatible)),
                ModifyPlanImpact.Certain));
        }

        if (unknown.Count > 0)
        {
            items.Add(PlanItem(
                ModifyPlanItemKind.Mods,
                "instance.modify.item.mods",
                _localizer.Get("instance.modify.mods.unknown", unknown.Count, DescribeNames(unknown)),
                ModifyPlanImpact.Unknown));
        }

        if (compatible > 0)
        {
            items.Add(PlanItem(
                ModifyPlanItemKind.Mods,
                "instance.modify.item.mods",
                _localizer.Get("instance.modify.mods.compatible", compatible),
                ModifyPlanImpact.Info));
        }

        return new ModsImpact(items, incompatible);
    }

    /// <summary>
    /// 单个模组对目标组合的影响。只有加载器平台与 <c>minecraft</c> 依赖的版本范围都能证明时
    /// 才给出“兼容/不兼容”；缺少元数据或范围无法解析时一律视为未知。
    /// </summary>
    private static ModTargetImpact ClassifyMod(ModEntry entry, string targetVersion, string targetLoader)
    {
        var metadata = entry.Metadata;
        if (metadata is null)
        {
            return ModTargetImpact.Unknown;
        }

        var loaderState = ModCompatibility.EvaluateLoader(targetLoader, metadata);
        var minecraft = metadata.Dependencies.FirstOrDefault(dependency =>
            string.Equals(dependency.ModId, "minecraft", StringComparison.OrdinalIgnoreCase));
        bool? versionMatches = string.IsNullOrWhiteSpace(minecraft?.VersionRange)
            ? null
            : ModVersionRange.Matches(targetVersion, minecraft!.VersionRange);

        if (loaderState == CompatibilityState.Incompatible || versionMatches == false)
        {
            return ModTargetImpact.Incompatible;
        }

        return loaderState == CompatibilityState.Compatible && versionMatches == true
            ? ModTargetImpact.Compatible
            : ModTargetImpact.Unknown;
    }

    private async Task<PacksImpact> DescribePacksAsync(CancellationToken token)
    {
        var items = new List<ModifyPlanItemViewModel>();
        var resourcePacks = await ListPacksAsync(ContentPackKind.ResourcePack, token).ConfigureAwait(true);
        var shaderPacks = await ListPacksAsync(ContentPackKind.ShaderPack, token).ConfigureAwait(true);

        if (resourcePacks is null && shaderPacks is null)
        {
            items.Add(PlanItem(
                ModifyPlanItemKind.Packs,
                "instance.modify.item.packs",
                _localizer.Get("instance.modify.packs.failed"),
                ModifyPlanImpact.Unknown));
            return new PacksImpact(items);
        }

        if (resourcePacks.GetValueOrDefault() == 0 && shaderPacks.GetValueOrDefault() == 0)
        {
            items.Add(PlanItem(
                ModifyPlanItemKind.Packs,
                "instance.modify.item.packs",
                _localizer.Get("instance.modify.packs.none"),
                ModifyPlanImpact.Info));
            return new PacksImpact(items);
        }

        items.Add(PlanItem(
            ModifyPlanItemKind.Packs,
            "instance.modify.item.packs",
            _localizer.Get(
                "instance.modify.packs.unknown",
                resourcePacks?.ToString() ?? _localizer.Get("instance.modify.packs.unavailable"),
                shaderPacks?.ToString() ?? _localizer.Get("instance.modify.packs.unavailable")),
            ModifyPlanImpact.Unknown));
        return new PacksImpact(items);
    }

    /// <summary>返回 null 表示读取失败；0 表示确实没有内容。</summary>
    private async Task<int?> ListPacksAsync(ContentPackKind kind, CancellationToken token)
    {
        try
        {
            var result = await _contentPacks.ListAsync(_state.InstanceId, kind, token).ConfigureAwait(true);
            return result.IsSuccess ? result.Value!.Packs.Count : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private IReadOnlyList<ModifyPlanItemViewModel> DescribeDownloads(string targetLoader, string? targetOptiFine)
    {
        var items = new List<ModifyPlanItemViewModel>
        {
            PlanItem(
                ModifyPlanItemKind.Downloads,
                "instance.modify.item.downloads",
                _localizer.Get("instance.modify.downloads.client"),
                ModifyPlanImpact.Info),
            PlanItem(
                ModifyPlanItemKind.Downloads,
                "instance.modify.item.downloads",
                _localizer.Get("instance.modify.downloads.assets"),
                ModifyPlanImpact.Info),
        };

        if (!string.Equals(targetLoader, LoaderKinds.Vanilla, StringComparison.Ordinal))
        {
            items.Add(PlanItem(
                ModifyPlanItemKind.Downloads,
                "instance.modify.item.downloads",
                _localizer.Get(
                    "instance.modify.downloads.loader",
                    LoaderTitle(targetLoader),
                    DescribeLoaderSource(targetLoader)),
                ModifyPlanImpact.Info));
        }

        if (string.Equals(targetLoader, LoaderKinds.Fabric, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_targetFabricApiVersion))
        {
            items.Add(PlanItem(
                ModifyPlanItemKind.Downloads,
                "instance.modify.item.downloads",
                _localizer.Get("instance.modify.downloads.fabricApi", _targetFabricApiVersion),
                ModifyPlanImpact.Info));
        }

        if (!string.IsNullOrWhiteSpace(targetOptiFine))
        {
            items.Add(PlanItem(
                ModifyPlanItemKind.Downloads,
                "instance.modify.item.downloads",
                _localizer.Get("instance.modify.downloads.optiFine", targetOptiFine),
                ModifyPlanImpact.Info));
        }

        items.Add(PlanItem(
            ModifyPlanItemKind.Downloads,
            "instance.modify.item.downloads",
            _localizer.Get("instance.modify.downloads.note"),
            ModifyPlanImpact.Info));

        return items;
    }

    private string DescribeLoaderSource(string loaderId) => loaderId switch
    {
        LoaderKinds.Fabric => "Fabric Meta",
        LoaderKinds.Cleanroom => "Cleanroom Maven",
        _ => "Mojang / Forge Maven",
    };

    private IReadOnlyList<ModifyPlanItemViewModel> DescribeManualItems(
        string targetLoader,
        string? targetOptiFine,
        ModsImpact mods)
    {
        var items = new List<ModifyPlanItemViewModel>();

        if (!string.IsNullOrWhiteSpace(_state.Model.OptiFineVersion))
        {
            if (!IsOptiFineSupportedBy(targetLoader))
            {
                items.Add(ManualItem("instance.modify.manual.optiFineRemoved"));
            }
            else if (string.IsNullOrWhiteSpace(targetOptiFine))
            {
                // 目标 OptiFine 列表读取失败或当前版本在目标版本下没有文件时，
                // 页面已有 OptiFineNote 说明原因；这里只补一条“需要手动决定”的处理项。
                items.Add(OptiFineNote.Length > 0
                    ? ManualItem("instance.modify.manual.optiFineMissingForTarget", _state.Model.OptiFineVersion)
                    : ManualItem("instance.modify.manual.optiFineNotSelected", _state.Model.OptiFineVersion));
            }
        }

        if (!string.IsNullOrWhiteSpace(_state.Model.FabricApiVersion) &&
            !string.Equals(targetLoader, LoaderKinds.Fabric, StringComparison.Ordinal))
        {
            items.Add(ManualItem("instance.modify.manual.fabricApiStale"));
        }

        if (mods.Incompatible.Count > 0)
        {
            items.Add(ManualItem(
                "instance.modify.manual.mods",
                mods.Incompatible.Count,
                DescribeNames(mods.Incompatible)));
        }

        return items;
    }

    private static bool IsOptiFineSupportedBy(string loaderId) =>
        string.Equals(loaderId, LoaderKinds.Forge, StringComparison.Ordinal) ||
        string.Equals(loaderId, LoaderKinds.Cleanroom, StringComparison.Ordinal);

    private string DescribeTargetComponents(string targetLoader, string? targetOptiFine)
    {
        var parts = new List<string>();
        if (string.Equals(targetLoader, LoaderKinds.Fabric, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_targetFabricApiVersion))
        {
            parts.Add($"Fabric API {_targetFabricApiVersion}");
        }

        if (!string.IsNullOrWhiteSpace(targetOptiFine))
        {
            parts.Add($"OptiFine {targetOptiFine}");
        }

        return parts.Count == 0 ? _localizer.Get("instance.modify.componentsNone") : string.Join(" · ", parts);
    }

    private string LoaderText(string loaderId, string? loaderVersion)
    {
        var title = LoaderTitle(loaderId);
        return string.IsNullOrWhiteSpace(loaderVersion) ? title : $"{title} {loaderVersion}";
    }

    private string DescribeNames(IReadOnlyList<string> names)
    {
        var visible = names.Take(MaxModNamesInPlan).ToArray();
        var text = string.Join(_localizer.Get("instance.modify.namesSeparator"), visible);
        return names.Count > visible.Length
            ? $"{text}{_localizer.Get("instance.modify.more", names.Count - visible.Length)}"
            : text;
    }

    /// <summary>
    /// 活动游戏进程：只有会话状态处于启动/运行且进程仍存活时才视为运行中。
    /// 会话清单可能因上次异常退出而过期，因此有 PID 时必须验证进程。
    /// </summary>
    private IReadOnlyList<string> FindRunningSessions(string instanceId)
    {
        IReadOnlyList<GameLogSession> sessions;
        try
        {
            sessions = _sessions.List();
        }
        catch (Exception)
        {
            return [];
        }

        var results = new List<string>();
        foreach (var session in sessions)
        {
            var manifest = session.Manifest;
            if (!string.Equals(manifest.InstanceId, instanceId, StringComparison.Ordinal) ||
                !ActiveSessionStates.Contains(manifest.State))
            {
                continue;
            }

            if (manifest.ProcessId is { } processId && !IsProcessAlive(processId))
            {
                continue;
            }

            results.Add(manifest.SessionId);
        }

        return results;
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 进行中的写入类任务。任务记录没有 InstanceId，因此只能证明“存在任务”，
    /// 不能证明属于本实例；UI 与计划文案必须按“无法确认归属”呈现。
    /// </summary>
    private int FindActiveWriteOperations() =>
        _operations.Store.Snapshots.Count(snapshot =>
            !snapshot.IsTerminal && WriteOperationKinds.Contains(snapshot.Kind));

    private ModifyPlanItemViewModel PlanItem(
        ModifyPlanItemKind kind,
        string titleKey,
        string detail,
        ModifyPlanImpact impact) =>
        new(kind, _localizer.Get(titleKey), detail, impact, ModifyPlanPresentation.ImpactText(impact, _localizer));

    private ModifyPlanItemViewModel ManualItem(string detailKey, params object?[] arguments) =>
        PlanItem(
            ModifyPlanItemKind.Manual,
            "instance.modify.item.manual",
            _localizer.Get(detailKey, arguments),
            ModifyPlanImpact.Warning);

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _generation++;

        _targetCancellation?.Cancel();
        _targetCancellation?.Dispose();
        _targetCancellation = null;
        _planCancellation?.Cancel();
        _planCancellation?.Dispose();
        _planCancellation = null;

        PlanItems.Clear();
        ActionItems.Clear();
        Blockers.Clear();
        TargetVersions.Clear();
        LoaderVersions.Clear();
        OptiFineVersions.Clear();
        LoaderChoices.Clear();
        _planTarget = null;
        WorkspaceRefresh = null;
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasActionItems));
        OnPropertyChanged(nameof(HasBlockers));
        GeneratePlanCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
    }

    private enum ModTargetImpact
    {
        Compatible,
        Incompatible,
        Unknown,
    }

    private sealed record PlanBuild(
        InstanceModifyPlanState State,
        IReadOnlyList<ModifyPlanItemViewModel> Items,
        IReadOnlyList<string> Blockers,
        IReadOnlyList<string> Actions);

    /// <summary>
    /// 生成计划时的目标组合（`instance-modify.md` §4）。提交前必须与当前选择完全一致，
    /// 避免用旧计划执行新目标；任何选择变化都会把计划重置为未检查。
    /// </summary>
    private sealed record ModifyExecutionTarget(
        string Version,
        string Loader,
        string? LoaderVersion,
        string? FabricApiVersion,
        string? OptiFineVersion);

    private sealed record ModsImpact(
        IReadOnlyList<ModifyPlanItemViewModel> Items,
        IReadOnlyList<string> Incompatible);

    private sealed record PacksImpact(IReadOnlyList<ModifyPlanItemViewModel> Items);
}
