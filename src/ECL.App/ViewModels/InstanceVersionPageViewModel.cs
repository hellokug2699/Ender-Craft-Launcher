// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Core.Storage;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;

namespace Ecl.App.ViewModels;

/// <summary>
/// 下载中心「实例版本」页（`download-instance-versions.md`）：新建一个 Minecraft 实例并完成部署。
///
/// 边界：
/// - 本页只创建新实例；既有实例的版本/加载器变更走实例工作区的 Modify 页（ADR 0045/0047）。
/// - 游戏版本只能从版本清单选择；加载器/组件版本来自领域目录，不提供手输。
/// - 单项进度只映射安装服务真实上报的阶段（<see cref="InstallStage"/>），不虚构固定阶段或百分比。
/// - 文件大小只在领域提供时显示（Fabric API 目录带大小），其余显示「大小未知」，不估算。
/// - 安装位置固定为主工作区（`IAppPaths.GameRoot`）；已登记的其他 Minecraft 目录是只读来源
///   （ADR 0018/0046），本页如实说明，不提供会悄悄写到别处的假选择。
/// </summary>
public sealed partial class InstanceVersionPageViewModel : ObservableObject
{
    private readonly IInstanceService _instances;
    private readonly IVersionInstallService _install;
    private readonly IVersionCatalog _versionCatalog;
    private readonly ILoaderCatalog _loaderCatalog;
    private readonly IWorkspaceService _workspace;
    private readonly InstanceSourceService _sources;
    private readonly IDialogEngine _dialogEngine;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly IAppPaths _paths;

    private IReadOnlyList<MinecraftGameVersion> _allVersions = [];
    private Dictionary<string, long?> _fabricApiSizes = new(StringComparer.Ordinal);
    private List<string> _existingNames = [];
    private bool _initialized;
    private bool _suppressVersionReload;
    private int _componentGeneration;
    private string? _createdInstanceId;

    public InstanceVersionPageViewModel(
        IInstanceService instances,
        IVersionInstallService install,
        IVersionCatalog versionCatalog,
        ILoaderCatalog loaderCatalog,
        IWorkspaceService workspace,
        InstanceSourceService sources,
        IDialogEngine dialogEngine,
        IDialogService dialogs,
        ILocalizer localizer,
        IAppPaths paths)
    {
        _instances = instances;
        _install = install;
        _versionCatalog = versionCatalog;
        _loaderCatalog = loaderCatalog;
        _workspace = workspace;
        _sources = sources;
        _dialogEngine = dialogEngine;
        _dialogs = dialogs;
        _localizer = localizer;
        _paths = paths;

        VersionTypes.Add(new GameVersionTypeChoice(
            MinecraftVersionKind.Release,
            localizer.Get("instances.versionType.release")));
        VersionTypes.Add(new GameVersionTypeChoice(
            MinecraftVersionKind.Snapshot,
            localizer.Get("instances.versionType.snapshot")));
        VersionTypes.Add(new GameVersionTypeChoice(
            MinecraftVersionKind.AprilFools,
            localizer.Get("instances.versionType.aprilFools")));
        _selectedVersionType = VersionTypes[0];

        // download-instance-versions.md §6：无、Forge、NeoForge、Fabric、Cleanroom。
        LoaderChoices.Add(new LoaderKindOption(LoaderKinds.Vanilla, localizer.Get("instances.loader.vanilla")));
        LoaderChoices.Add(new LoaderKindOption(LoaderKinds.Forge, localizer.Get("instances.loader.forge")));
        LoaderChoices.Add(new LoaderKindOption(LoaderKinds.NeoForge, localizer.Get("instances.loader.neoforge")));
        LoaderChoices.Add(new LoaderKindOption(LoaderKinds.Fabric, localizer.Get("instances.loader.fabric")));
        LoaderChoices.Add(new LoaderKindOption(LoaderKinds.Cleanroom, localizer.Get("instances.loader.cleanroom")));
        _selectedLoader = LoaderChoices[0];

        GameItem = new DeploymentItemViewModel(
            InstanceDeploymentPresentation.GameItem,
            localizer.Get("downloads.deploy.item.game"),
            localizer);
        LoaderItem = new DeploymentItemViewModel(
            InstanceDeploymentPresentation.LoaderItem,
            localizer.Get("downloads.deploy.item.loader"),
            localizer);
        FabricApiItem = new DeploymentItemViewModel(
            InstanceDeploymentPresentation.FabricApiItem,
            localizer.Get("downloads.deploy.item.fabricApi"),
            localizer);
        OptiFineItem = new DeploymentItemViewModel(
            InstanceDeploymentPresentation.OptiFineItem,
            localizer.Get("downloads.deploy.item.optifine"),
            localizer);

        // 初次进入时还没有选择版本/加载器：四个下载项都是「未选择」，由选择变化逐个启用。
        GameItem.MarkNotSelected();
        LoaderItem.MarkNotSelected();
        FabricApiItem.MarkNotSelected();
        OptiFineItem.MarkNotSelected();
        InstallLocationText = localizer.Get("downloads.deploy.installLocationValue", paths.GameRoot);
    }

    public ObservableCollection<GameVersionTypeChoice> VersionTypes { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> GameVersions { get; } = [];

    public ObservableCollection<LoaderKindOption> LoaderChoices { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> LoaderVersions { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> FabricApiVersions { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> OptiFineVersions { get; } = [];

    public ObservableCollection<WorkspaceLocationRow> InstallLocations { get; } = [];

    public DeploymentItemViewModel GameItem { get; }

    public DeploymentItemViewModel LoaderItem { get; }

    public DeploymentItemViewModel FabricApiItem { get; }

    public DeploymentItemViewModel OptiFineItem { get; }

    /// <summary>下载项按页面顺序排列；视图用同一顺序呈现状态线。</summary>
    public IReadOnlyList<DeploymentItemViewModel> DownloadItems =>
        [GameItem, LoaderItem, FabricApiItem, OptiFineItem];

    public string Title => _localizer.Get("downloads.version.title");

    public string Subtitle => _localizer.Get("downloads.version.subtitle");

    public string GameVersionLabel => _localizer.Get("downloads.deploy.gameVersion");

    public string VersionTypeLabel => _localizer.Get("downloads.deploy.type");

    public string InstanceNameLabel => _localizer.Get("instances.name");

    public string InstanceNamePlaceholder => _localizer.Get("instances.namePlaceholder");

    public string InstallLocationLabel => _localizer.Get("downloads.deploy.installLocation");

    public string InstallLocationText { get; }

    public string InstallLocationHint => _localizer.Get("downloads.deploy.installLocationHint");

    public string InstallLocationLimitText => _localizer.Get("downloads.deploy.installLocationLimit");

    public string AddDirectoryText => _localizer.Get("instances.addDirectory");

    public string LoaderLabel => _localizer.Get("instances.loader.title");

    public string LoaderHistoryText => _localizer.Get("downloads.deploy.versionHistory");

    public string FabricApiNoticeText => _localizer.Get("downloads.deploy.fabricApiHint");

    public string OptiFineLabel => _localizer.Get("instances.optifine.title");

    public string StartDownloadText => _localizer.Get("downloads.deploy.start");

    public string RetryText => _localizer.Get("downloads.deploy.retry");

    public string ResetText => _localizer.Get("downloads.deploy.reset");

    public string ResetHintText => _localizer.Get("downloads.deploy.resetHint");

    public string ViewErrorText => _localizer.Get("downloads.deploy.viewError");

    public string RefreshVersionsText => _localizer.Get("instances.version.refresh");



    public string DeploymentStateText => InstanceDeploymentPresentation.DescribeState(DeploymentState, _localizer);

    /// <summary>最近一次加载器/组件列表刷新；视图与测试可以等待它完成。</summary>
    public Task PendingComponentRefresh { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private GameVersionTypeChoice? _selectedVersionType;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedGameVersion;

    [ObservableProperty]
    private LoaderKindOption? _selectedLoader;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedLoaderVersion;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedFabricApiVersion;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedOptiFineVersion;

    [ObservableProperty]
    private string _instanceName = string.Empty;

    [ObservableProperty]
    private bool _isNameDuplicate;

    [ObservableProperty]
    private bool _isLoadingVersions;

    [ObservableProperty]
    private string _versionStatus = string.Empty;

    [ObservableProperty]
    private bool _isLoadingComponents;

    [ObservableProperty]
    private string _componentStatus = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isDeploying;

    [ObservableProperty]
    private InstanceDeploymentState _deploymentState = InstanceDeploymentState.Configuring;

    [ObservableProperty]
    private bool _showProgress;

    [ObservableProperty]
    private bool _progressIndeterminate;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _progressPercentText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _showErrorInfo;

    /// <summary>部署已经开始且可以被重新布置（成功）或重试（创建成功后的失败）。</summary>
    public bool ShowResetButton => DeploymentState == InstanceDeploymentState.Succeeded;

    /// <summary>成功后页面只保留「重新布置」，不再显示开始下载。</summary>
    public bool ShowStartButton => DeploymentState != InstanceDeploymentState.Succeeded;

    public bool CanRetry => DeploymentState == InstanceDeploymentState.Failed && _createdInstanceId is not null;

    public bool IsConfigEnabled => !IsDeploying && DeploymentState != InstanceDeploymentState.Succeeded;

    public bool ShowFabricApi => string.Equals(SelectedLoader?.Id, LoaderKinds.Fabric, StringComparison.Ordinal);

    /// <summary>只有目录确实返回了加载器版本时才显示版本下拉，避免空控件。</summary>
    public bool HasLoaderVersions => LoaderVersions.Count > 0;

    public bool ShowOptiFine =>
        string.Equals(SelectedLoader?.Id, LoaderKinds.Forge, StringComparison.Ordinal)
        || string.Equals(SelectedLoader?.Id, LoaderKinds.Cleanroom, StringComparison.Ordinal);


    public string DuplicateNameMessage => _localizer.Get("downloads.deploy.nameDuplicate");

    public bool CanStartDownload =>
        IsConfigEnabled
        && !IsNameDuplicate
        && !IsLoadingVersions
        && !IsLoadingComponents
        && SelectedGameVersion?.Value is { Length: > 0 };

    /// <summary>首次进入下载中心时加载版本清单、实例名称与安装位置。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await RefreshInstanceNamesAsync().ConfigureAwait(true);
        await RefreshInstallLocationsAsync().ConfigureAwait(true);
        await LoadVersionsAsync().ConfigureAwait(true);
    }

    /// <summary>重新进入下载页时只刷新实例名称（同名校验）与位置登记，不重置已配置的选择。</summary>
    public async Task RefreshAsync()
    {
        await RefreshInstanceNamesAsync().ConfigureAwait(true);
        await RefreshInstallLocationsAsync().ConfigureAwait(true);
    }

    private async Task RefreshInstanceNamesAsync()
    {
        try
        {
            var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);
            _existingNames = instances.Select(instance => instance.Name).ToList();
        }
        catch (Exception)
        {
            // 名称列表读取失败时不阻止创建；提交时仍由领域层做同名校验。
            _existingNames = [];
        }

        ValidateName();
    }

    private async Task RefreshInstallLocationsAsync()
    {
        InstallLocations.Clear();
        foreach (var root in _workspace.Roots)
        {
            InstallLocations.Add(new WorkspaceLocationRow(
                root.Name,
                root.Path,
                string.Equals(_workspace.Primary?.Id, root.Id, StringComparison.Ordinal),
                root.Kind == WorkspaceKind.External,
                _localizer.Get("downloads.deploy.locationCurrent"),
                _localizer.Get("downloads.deploy.locationSource")));
        }
    }

    [RelayCommand]
    private Task RefreshVersionsAsync() => LoadVersionsAsync(refresh: true);

    private async Task LoadVersionsAsync(bool refresh = false)
    {
        if (IsLoadingVersions)
        {
            return;
        }

        IsLoadingVersions = true;
        VersionStatus = _localizer.Get("instances.version.loading");
        try
        {
            var result = await _versionCatalog.GetVersionsAsync(refresh).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                _allVersions = [];
                ApplyVersionFilter(keepSelection: false);
                VersionStatus = _localizer.Get("instances.version.failed", result.Error?.Message ?? string.Empty);
                return;
            }

            _allVersions = result.Value!;
            ApplyVersionFilter(keepSelection: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _allVersions = [];
            ApplyVersionFilter(keepSelection: false);
            VersionStatus = _localizer.Get("instances.version.failed", exception.Message);
        }
        finally
        {
            IsLoadingVersions = false;
        }
    }

    private void ApplyVersionFilter(bool keepSelection)
    {
        var kind = SelectedVersionType?.Kind ?? MinecraftVersionKind.Release;
        var previous = keepSelection ? SelectedGameVersion?.Value : null;

        _suppressVersionReload = true;
        try
        {
            GameVersions.Clear();
            foreach (var version in _allVersions.Where(version => version.Kind == kind))
            {
                GameVersions.Add(new VersionChoiceViewModel(
                    version.Id,
                    FormatVersionWithDate(version.Id, version.ReleasedAt)));
            }

            SelectedGameVersion = previous is null
                ? GameVersions.FirstOrDefault()
                : GameVersions.FirstOrDefault(version =>
                      string.Equals(version.Value, previous, StringComparison.Ordinal))
                  ?? GameVersions.FirstOrDefault();

            VersionStatus = GameVersions.Count == 0
                ? _localizer.Get("instances.version.empty")
                : string.Empty;
        }
        finally
        {
            _suppressVersionReload = false;
        }

        UpdateGameItem();
        PendingComponentRefresh = ReloadComponentsAsync();
    }

    [RelayCommand]
    private Task RefreshComponentsAsync()
    {
        PendingComponentRefresh = ReloadComponentsAsync();
        return PendingComponentRefresh;
    }

    private async Task ReloadComponentsAsync()
    {
        var generation = ++_componentGeneration;
        ResetComponentChoices();
        var loader = SelectedLoader?.Id ?? LoaderKinds.Vanilla;
        var version = SelectedGameVersion?.Value;

        if (string.IsNullOrWhiteSpace(version))
        {
            ComponentStatus = _localizer.Get("instances.version.select");
            return;
        }

        if (loader == LoaderKinds.Vanilla)
        {
            ComponentStatus = string.Empty;
            return;
        }

        if (loader == LoaderKinds.Cleanroom &&
            !string.Equals(version, "1.12.2", StringComparison.OrdinalIgnoreCase))
        {
            ComponentStatus = _localizer.Get("instances.loader.cleanroomOnly");
            return;
        }

        IsLoadingComponents = true;
        ComponentStatus = _localizer.Get("instances.components.loading");
        try
        {
            if (loader == LoaderKinds.Fabric)
            {
                var loaders = await _loaderCatalog.GetFabricLoadersAsync(version).ConfigureAwait(true);
                if (!IsCurrent(generation))
                {
                    return;
                }

                if (!loaders.IsSuccess)
                {
                    ComponentStatus = _localizer.Get("instances.components.failed", loaders.Error?.Message ?? string.Empty);
                    return;
                }

                FillLoaderVersions(loaders.Value!);

                var apiVersions = await _loaderCatalog.GetFabricApiVersionsAsync(version).ConfigureAwait(true);
                if (!IsCurrent(generation))
                {
                    return;
                }

                if (!apiVersions.IsSuccess)
                {
                    ComponentStatus = _localizer.Get("instances.components.failed", apiVersions.Error?.Message ?? string.Empty);
                    return;
                }

                _fabricApiSizes = new Dictionary<string, long?>(StringComparer.Ordinal);
                foreach (var item in apiVersions.Value!)
                {
                    _fabricApiSizes[item.Version] = item.Size;
                    FabricApiVersions.Add(new VersionChoiceViewModel(
                        item.Version,
                        FormatVersionWithDate(item.Version, item.ReleasedAt)));
                }

                SelectedFabricApiVersion = FabricApiVersions.FirstOrDefault();
                UpdateFabricApiItem();
                ComponentStatus = LoaderVersions.Count == 0
                    ? _localizer.Get("instances.loader.noVersions")
                    : FabricApiVersions.Count == 0
                        ? _localizer.Get("instances.fabricApi.none")
                        : string.Empty;
                return;
            }

            if (loader is LoaderKinds.Forge or LoaderKinds.NeoForge)
            {
                var loaders = loader == LoaderKinds.Forge
                    ? await _loaderCatalog.GetForgeVersionsAsync(version).ConfigureAwait(true)
                    : await _loaderCatalog.GetNeoForgeVersionsAsync(version).ConfigureAwait(true);
                if (!IsCurrent(generation))
                {
                    return;
                }

                if (!loaders.IsSuccess)
                {
                    ComponentStatus = _localizer.Get("instances.components.failed", loaders.Error?.Message ?? string.Empty);
                    return;
                }

                FillLoaderVersions(loaders.Value!);

                if (loader == LoaderKinds.Forge)
                {
                    var optiFine = await _loaderCatalog.GetOptiFineVersionsAsync(version).ConfigureAwait(true);
                    if (!IsCurrent(generation))
                    {
                        return;
                    }

                    if (!optiFine.IsSuccess)
                    {
                        ComponentStatus = _localizer.Get("instances.components.failed", optiFine.Error?.Message ?? string.Empty);
                        return;
                    }

                    FillOptiFineVersions(optiFine.Value!);
                }

                ComponentStatus = LoaderVersions.Count == 0
                    ? _localizer.Get("instances.loader.noVersions")
                    : string.Empty;
                return;
            }

            var cleanroom = await _loaderCatalog.GetCleanroomVersionsAsync(version).ConfigureAwait(true);
            if (!IsCurrent(generation))
            {
                return;
            }

            if (!cleanroom.IsSuccess)
            {
                ComponentStatus = _localizer.Get("instances.components.failed", cleanroom.Error?.Message ?? string.Empty);
                return;
            }

            FillLoaderVersions(cleanroom.Value!);

            var cleanroomOptiFine = await _loaderCatalog.GetOptiFineVersionsAsync(version).ConfigureAwait(true);
            if (!IsCurrent(generation))
            {
                return;
            }

            if (!cleanroomOptiFine.IsSuccess)
            {
                ComponentStatus = _localizer.Get("instances.components.failed", cleanroomOptiFine.Error?.Message ?? string.Empty);
                return;
            }

            FillOptiFineVersions(cleanroomOptiFine.Value!);
            ComponentStatus = LoaderVersions.Count == 0
                ? _localizer.Get("instances.loader.noVersions")
                : string.Empty;
        }
        catch (Exception exception)
        {
            if (IsCurrent(generation))
            {
                ComponentStatus = _localizer.Get("instances.components.failed", exception.Message);
            }
        }
        finally
        {
            if (IsCurrent(generation))
            {
                IsLoadingComponents = false;
            }
        }
    }

    private bool IsCurrent(int generation) => generation == _componentGeneration;

    private void FillLoaderVersions(IReadOnlyList<LoaderVersionInfo> versions)
    {
        foreach (var item in versions)
        {
            LoaderVersions.Add(new VersionChoiceViewModel(
                item.Version,
                FormatVersionWithDate(item.Version, item.ReleasedAt)));
        }

        SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
        OnPropertyChanged(nameof(HasLoaderVersions));
    }

    private void FillOptiFineVersions(IReadOnlyList<OptiFineVersionInfo> versions)
    {
        // 默认不安装（§7.2）：第一项是显式的不安装选项。
        OptiFineVersions.Add(new VersionChoiceViewModel(null, _localizer.Get("instances.optifine.none")));
        foreach (var item in versions)
        {
            OptiFineVersions.Add(new VersionChoiceViewModel(
                item.Key,
                FormatVersionWithDate(item.FileName, item.ReleasedAt)));
        }

        SelectedOptiFineVersion = OptiFineVersions[0];
    }

    private void ResetComponentChoices()
    {
        LoaderVersions.Clear();
        FabricApiVersions.Clear();
        OptiFineVersions.Clear();
        _fabricApiSizes = new Dictionary<string, long?>(StringComparer.Ordinal);
        SelectedLoaderVersion = null;
        SelectedFabricApiVersion = null;
        SelectedOptiFineVersion = null;
        if (SelectedLoader is { } loader)
        {
            loader.VersionText = string.Empty;
        }

        OnPropertyChanged(nameof(HasLoaderVersions));
        ResetComponentItems();
    }

    private void ResetComponentItems()
    {
        LoaderItem.VersionText = string.Empty;
        LoaderItem.SizeText = _localizer.Get("downloads.deploy.sizeUnknown");
        FabricApiItem.VersionText = string.Empty;
        FabricApiItem.SizeText = _localizer.Get("downloads.deploy.sizeUnknown");
        OptiFineItem.VersionText = string.Empty;
        OptiFineItem.SizeText = _localizer.Get("downloads.deploy.sizeUnknown");
        RefreshItemSelection();
    }

    private void UpdateGameItem()
    {
        if (SelectedGameVersion?.Value is { Length: > 0 } version)
        {
            GameItem.IsIncluded = true;
            GameItem.VersionText = version;
            GameItem.SizeText = _localizer.Get("downloads.deploy.sizeUnknown");
        }
        else
        {
            GameItem.MarkNotSelected();
        }

        RefreshItemSelection();
    }

    private void UpdateFabricApiItem()
    {
        if (!ShowFabricApi || SelectedFabricApiVersion?.Value is not { Length: > 0 } version)
        {
            FabricApiItem.MarkNotSelected();
            RefreshItemSelection();
            return;
        }

        FabricApiItem.IsIncluded = true;
        FabricApiItem.VersionText = version;
        FabricApiItem.SizeText = _fabricApiSizes.TryGetValue(version, out var size) && size is > 0
            ? StorageSizeFormatter.FormatBytes(size.Value)
            : _localizer.Get("downloads.deploy.sizeUnknown");
        RefreshItemSelection();
    }

    private void UpdateLoaderItem()
    {
        var loader = SelectedLoader?.Id ?? LoaderKinds.Vanilla;
        if (loader == LoaderKinds.Vanilla)
        {
            LoaderItem.MarkNotSelected();
            RefreshItemSelection();
            return;
        }

        LoaderItem.IsIncluded = true;
        LoaderItem.VersionText = SelectedLoaderVersion?.Value is { Length: > 0 } version
            ? $"{SelectedLoader?.Title} {version}"
            : SelectedLoader?.Title ?? string.Empty;
        LoaderItem.SizeText = _localizer.Get("downloads.deploy.sizeUnknown");
        RefreshItemSelection();
    }

    private void UpdateOptiFineItem()
    {
        if (!ShowOptiFine)
        {
            OptiFineItem.MarkNotSelected();
            RefreshItemSelection();
            return;
        }

        if (SelectedOptiFineVersion?.Value is not { Length: > 0 } version)
        {
            // 默认不安装：行仍在，但本次部署不包含它。
            OptiFineItem.MarkNotSelected();
            RefreshItemSelection();
            return;
        }

        OptiFineItem.IsIncluded = true;
        OptiFineItem.VersionText = version;
        OptiFineItem.SizeText = _localizer.Get("downloads.deploy.sizeUnknown");
        RefreshItemSelection();
    }

    private void RefreshItemSelection()
    {
        ApplyIdle(GameItem);
        ApplyIdle(LoaderItem);
        ApplyIdle(FabricApiItem);
        ApplyIdle(OptiFineItem);
        OnPropertyChanged(nameof(CanStartDownload));
    }

    private static void ApplyIdle(DeploymentItemViewModel item)
    {
        if (!item.IsIncluded)
        {
            item.MarkNotSelected();
            return;
        }

        if (item.Status == DeploymentItemStatus.NotSelected)
        {
            item.Status = DeploymentItemStatus.Idle;
        }
    }

    [RelayCommand]
    private void AddDirectory()
    {
        if (!_dialogEngine.IsAvailable)
        {
            StatusMessage = _localizer.Get("downloads.deploy.addDirectoryUnavailable");
            return;
        }

        _ = ShowDirectoryDialogAsync();
    }

    private async Task ShowDirectoryDialogAsync()
    {
        try
        {
            var dialog = new InstanceDirectoryDialogViewModel(
                _workspace,
                _sources,
                _dialogEngine,
                _dialogs,
                _localizer);
            await dialog.LoadAsync().ConfigureAwait(true);
            await _dialogEngine.ShowAsync(new DialogRequest
            {
                Title = dialog.Title,
                Content = dialog,
                MinWidth = 520,
                MaxWidth = 680,
                MaxHeight = 640,
                InitialFocus = DialogInitialFocus.FirstFocusable,
            }).ConfigureAwait(true);

            await RefreshInstallLocationsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusMessage = _localizer.Get("downloads.deploy.addDirectoryFailed", exception.Message);
        }
    }

    [RelayCommand]
    private Task StartDownloadAsync() => DeployAsync(retry: false);

    [RelayCommand]
    private Task RetryAsync() => DeployAsync(retry: true);

    private async Task DeployAsync(bool retry)
    {
        if (IsDeploying)
        {
            return;
        }

        if (!retry && !CanStartDownload)
        {
            StatusMessage = DescribeConfigurationProblem();
            return;
        }

        var loader = SelectedLoader?.Id ?? LoaderKinds.Vanilla;
        var version = SelectedGameVersion?.Value;
        if (string.IsNullOrWhiteSpace(version))
        {
            StatusMessage = _localizer.Get("instances.version.select");
            return;
        }

        if (loader == LoaderKinds.Cleanroom && !string.Equals(version, "1.12.2", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = _localizer.Get("instances.loader.cleanroomOnly");
            return;
        }

        string? loaderVersion = loader == LoaderKinds.Vanilla ? null : SelectedLoaderVersion?.Value;
        if (loader != LoaderKinds.Vanilla && string.IsNullOrWhiteSpace(loaderVersion))
        {
            StatusMessage = _localizer.Get("instances.loader.versionRequired");
            return;
        }

        string? fabricApiVersion = loader == LoaderKinds.Fabric ? SelectedFabricApiVersion?.Value : null;
        if (loader == LoaderKinds.Fabric && string.IsNullOrWhiteSpace(fabricApiVersion))
        {
            StatusMessage = _localizer.Get("instances.fabricApi.none");
            return;
        }

        string? optiFineVersion = loader is LoaderKinds.Forge or LoaderKinds.Cleanroom
            ? SelectedOptiFineVersion?.Value
            : null;

        if (!retry && IsNameDuplicate)
        {
            StatusMessage = DuplicateNameMessage;
            return;
        }

        IsDeploying = true;
        ShowErrorInfo = false;
        ErrorText = string.Empty;
        DeploymentState = InstanceDeploymentState.Deploying;
        RaiseDeploymentStateChanged();
        PrepareItemsForDeployment();

        try
        {
            if (!retry || _createdInstanceId is null)
            {
                var name = InstanceName.Trim();
                if (name.Length == 0)
                {
                    name = version;
                }

                StatusMessage = _localizer.Get("downloads.deploy.creating", name);
                var created = await _instances
                    .CreateAsync(name, version, loader, loaderVersion, fabricApiVersion, optiFineVersion)
                    .ConfigureAwait(true);
                if (!created.IsSuccess)
                {
                    FailBeforeInstall(created.Error?.Message ?? _localizer.Get("downloads.deploy.createFailed"));
                    return;
                }

                _createdInstanceId = created.Value!.Id;
                await RefreshInstanceNamesAsync().ConfigureAwait(true);
            }

            await RunInstallAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            FailBeforeInstall(_localizer.Get("downloads.failedDetail", exception.Message));
        }
        finally
        {
            IsDeploying = false;
            ShowProgress = false;
            RaiseDeploymentStateChanged();
            OnPropertyChanged(nameof(CanStartDownload));
        }
    }

    private void FailBeforeInstall(string message)
    {
        foreach (var item in DownloadItems)
        {
            if (item.IsIncluded)
            {
                item.Status = DeploymentItemStatus.Idle;
            }
        }

        DeploymentState = InstanceDeploymentState.Failed;
        StatusMessage = message;
        ErrorText = message;
        ShowErrorInfo = true;
        ProgressPercentText = string.Empty;
        ProgressText = message;
        RaiseDeploymentStateChanged();
    }

    private async Task RunInstallAsync()
    {
        if (_createdInstanceId is null)
        {
            return;
        }

        ShowProgress = true;
        ProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressPercentText = string.Empty;
        ProgressText = _localizer.Get("downloads.deploy.preparing");
        StatusMessage = ProgressText;

        var progress = new Progress<InstallProgress>(update =>
        {
            ProgressIndeterminate = update.IsIndeterminate && update.Stage != InstallStage.Completed;
            ProgressValue = update.Percent;
            ProgressPercentText = update.TotalFiles > 0 ? $"{update.Percent:F0}%" : string.Empty;
            ProgressText = update.TotalFiles > 0
                ? $"{update.Message} ({update.CompletedFiles}/{update.TotalFiles})"
                : update.Message;
            ApplyStage(update.Stage);
        });

        var result = await _install.InstallAsync(_createdInstanceId, progress).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MarkStageFailure();
            DeploymentState = InstanceDeploymentState.Failed;
            StatusMessage = _localizer.Get("downloads.deploy.failed", result.Error?.Message ?? string.Empty);
            ErrorText = StatusMessage;
            ShowErrorInfo = true;
            ProgressPercentText = string.Empty;
            ProgressText = StatusMessage;
            RaiseDeploymentStateChanged();
            return;
        }

        foreach (var item in DownloadItems)
        {
            item.MarkCompleted();
        }

        ProgressIndeterminate = false;
        ProgressValue = 100;
        ProgressPercentText = "100%";
        ProgressText = _localizer.Get("downloads.deploy.succeeded");
        StatusMessage = _localizer.Get("downloads.deploy.succeededInstance", ResolveInstanceName());
        DeploymentState = InstanceDeploymentState.Succeeded;
        RaiseDeploymentStateChanged();
    }

    private void PrepareItemsForDeployment()
    {
        foreach (var item in DownloadItems)
        {
            if (item.IsIncluded)
            {
                item.MarkQueued();
            }
        }
    }

    /// <summary>把安装服务的真实阶段映射到下载项状态；不改造阶段顺序，也不虚构阶段。</summary>
    private void ApplyStage(InstallStage stage)
    {
        if (stage == InstallStage.Completed)
        {
            foreach (var item in DownloadItems)
            {
                item.MarkCompleted();
            }

            return;
        }

        var order = InstanceDeploymentPresentation.StageOrder(stage);
        foreach (var item in DownloadItems)
        {
            if (!item.IsIncluded)
            {
                continue;
            }

            var items = InstanceDeploymentPresentation.ItemsForStage(stage);
            if (items.Contains(item.Key, StringComparer.Ordinal))
            {
                item.MarkDownloading();
                continue;
            }

            if (order > InstanceDeploymentPresentation.LastStageOrder(item.Key))
            {
                item.MarkCompleted();
            }
        }
    }

    private void MarkStageFailure()
    {
        var failed = DownloadItems.FirstOrDefault(item =>
            item.IsIncluded && item.Status == DeploymentItemStatus.Downloading);
        failed?.MarkFailed();

        foreach (var item in DownloadItems)
        {
            if (!item.IsIncluded || item == failed)
            {
                continue;
            }

            if (item.Status == DeploymentItemStatus.Downloading)
            {
                item.Status = DeploymentItemStatus.Queued;
            }
        }
    }

    private string ResolveInstanceName()
    {
        var name = InstanceName.Trim();
        return name.Length == 0 ? SelectedGameVersion?.Value ?? string.Empty : name;
    }

    private string DescribeConfigurationProblem()
    {
        if (IsNameDuplicate)
        {
            return DuplicateNameMessage;
        }

        return SelectedGameVersion?.Value is { Length: > 0 }
            ? _localizer.Get("downloads.deploy.configIncomplete")
            : _localizer.Get("instances.version.select");
    }

    [RelayCommand]
    private void ResetDeployment()
    {
        if (DeploymentState != InstanceDeploymentState.Succeeded)
        {
            return;
        }

        // download-instance-versions.md §13.2：保留安装位置，其余选项全部清空。
        _createdInstanceId = null;
        _suppressVersionReload = true;
        try
        {
            SelectedGameVersion = null;
        }
        finally
        {
            _suppressVersionReload = false;
        }

        InstanceName = string.Empty;
        SelectedLoader = LoaderChoices[0];
        ResetComponentChoices();
        UpdateGameItem();
        RefreshItemSelection();
        DeploymentState = InstanceDeploymentState.Configuring;
        ShowProgress = false;
        ProgressValue = 0;
        ProgressIndeterminate = false;
        ProgressText = string.Empty;
        ProgressPercentText = string.Empty;
        StatusMessage = string.Empty;
        ErrorText = string.Empty;
        ShowErrorInfo = false;
        ComponentStatus = string.Empty;
        RaiseDeploymentStateChanged();
    }

    [RelayCommand]
    private void OpenErrorInfo()
    {
        if (string.IsNullOrWhiteSpace(ErrorText))
        {
            return;
        }

        _dialogs.ShowError(_localizer.Get("downloads.deploy.errorTitle"), ErrorText);
    }

    partial void OnSelectedVersionTypeChanged(GameVersionTypeChoice? value)
    {
        if (!_initialized)
        {
            return;
        }

        ApplyVersionFilter(keepSelection: false);
    }

    partial void OnSelectedGameVersionChanged(VersionChoiceViewModel? value)
    {
        UpdateGameItem();
        if (_initialized && !_suppressVersionReload)
        {
            PendingComponentRefresh = ReloadComponentsAsync();
        }
    }

    partial void OnSelectedLoaderChanged(LoaderKindOption? value)
    {
        OnPropertyChanged(nameof(ShowFabricApi));
        OnPropertyChanged(nameof(ShowOptiFine));
        if (_initialized)
        {
            PendingComponentRefresh = ReloadComponentsAsync();
        }
    }

    partial void OnSelectedLoaderVersionChanged(VersionChoiceViewModel? value)
    {
        if (SelectedLoader is { } loader)
        {
            loader.VersionText = value?.Value is { Length: > 0 } version ? version : string.Empty;
        }

        UpdateLoaderItem();
    }

    partial void OnSelectedFabricApiVersionChanged(VersionChoiceViewModel? value) => UpdateFabricApiItem();

    partial void OnSelectedOptiFineVersionChanged(VersionChoiceViewModel? value) => UpdateOptiFineItem();

    partial void OnInstanceNameChanged(string value) => ValidateName();

    private void ValidateName()
    {
        var name = InstanceName.Trim();
        IsNameDuplicate = name.Length > 0
            && _existingNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(CanStartDownload));
    }

    partial void OnIsNameDuplicateChanged(bool value) => OnPropertyChanged(nameof(CanStartDownload));

    partial void OnIsDeployingChanged(bool value) => RaiseDeploymentStateChanged();

    partial void OnDeploymentStateChanged(InstanceDeploymentState value) => RaiseDeploymentStateChanged();

    partial void OnIsLoadingVersionsChanged(bool value) => OnPropertyChanged(nameof(CanStartDownload));

    partial void OnIsLoadingComponentsChanged(bool value) => OnPropertyChanged(nameof(CanStartDownload));

    private void RaiseDeploymentStateChanged()
    {
        OnPropertyChanged(nameof(DeploymentStateText));
        OnPropertyChanged(nameof(ShowResetButton));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(IsConfigEnabled));
        OnPropertyChanged(nameof(CanStartDownload));
    }

    private string FormatVersionWithDate(string version, DateTimeOffset? releasedAt)
    {
        var date = releasedAt?.ToLocalTime().ToString("yyyy-MM-dd")
                   ?? _localizer.Get("instances.versionDateUnknown");
        return _localizer.Get("instances.versionWithDate", version, date);
    }
}

/// <summary>加载器选项；<see cref="VersionText"/> 随所选加载器版本更新，用于在选择项上显示版本号。</summary>
public sealed partial class LoaderKindOption : ObservableObject
{
    public LoaderKindOption(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; }

    public string Title { get; }

    [ObservableProperty]
    private string _versionText = string.Empty;

    public bool HasVersion => !string.IsNullOrWhiteSpace(VersionText);

    /// <summary>UIA/无障碍名称只使用本地化标题与版本号，不暴露记录类型的 ToString()。</summary>
    public override string ToString() => HasVersion ? $"{Title} {VersionText}" : Title;

    partial void OnVersionTextChanged(string value) => OnPropertyChanged(nameof(HasVersion));
}

/// <summary>安装位置展开区的一行：已登记工作区根目录。外部来源只读，不承载新建实例。</summary>
public sealed record WorkspaceLocationRow(
    string Name,
    string Path,
    bool IsPrimary,
    bool IsExternal,
    string PrimaryHint,
    string SourceHint);





