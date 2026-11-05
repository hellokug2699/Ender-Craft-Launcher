// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.App.Views;
using Ecl.App.Workspace.ViewModels;
using Ecl.Auth;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Download;
using Ecl.Java;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Eula;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;

namespace Ecl.App.ViewModels;

public sealed partial class InstancesViewModel : ObservableObject
{
    private readonly IInstanceService _instances;
    private readonly IAccountService _accounts;
    private readonly IAuthServerService _authServers;
    private readonly IMicrosoftAuthenticationService _microsoftAuthentication;
    private readonly ILaunchService _launchService;
    private readonly IVersionInstallService _installService;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly IAppPaths _paths;
    private readonly IJavaLocator _javaLocator;
    private readonly IJavaSelectionService _javaSelection;
    private readonly IJavaInstallService _javaInstall;
    private readonly IVersionResolver _versionResolver;
    private readonly IVersionCatalog _versionCatalog;
    private readonly IEulaService _eula;
    private readonly ILoaderCatalog _loaderCatalog;
    private readonly IDownloadController _downloads;
    private readonly ICrashReportService _crashReports;
    private readonly IDiagnosticPackageService _diagnostics;
    private readonly Ecl.Minecraft.World.WorldWorkbenchService _worldWorkbench;
    private readonly Ecl.Core.Storage.InstanceSourceService _instanceSources;
    private readonly Ecl.Core.Storage.IWorkspaceService _workspace;
    private readonly IDialogEngine _dialogsEngine;
    private bool _initialized;
    private bool _suppressAccountSelection;
    private bool _suppressVersionReload;
    private IReadOnlyList<MinecraftGameVersion> _allVersions = [];

    /// <summary>打开当前实例的独立工作区页面。</summary>
    public Action<string>? NavigateToDetails { get; set; }

    /// <summary>从实例工作区返回实例列表。</summary>
    public Action? NavigateBackFromDetails { get; set; }

    public Func<string, Task<string>>? GetModHealthSummary { get; set; }

    public ObservableCollection<InstanceItemViewModel> Instances { get; } = [];

    public ObservableCollection<InstanceSourceGroupViewModel> SourceGroups { get; } = [];

    public bool HasSourceGroups => SourceGroups.Count > 0;

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    public ObservableCollection<AuthServerItemViewModel> AuthServers { get; } = [];

    /// <summary>只有第三方账户才有“账户设置”可编辑（服务器地址与注册链接）。</summary>
    public bool CanOpenAccountSettings => SelectedAccount?.Model.Type == AccountType.ThirdParty;

    public ObservableCollection<LaunchStepItemViewModel> LaunchSteps => Operation.LaunchSteps;

    /// <summary>
    /// 主页启动面板与实例列表页共用的操作结果显示状态。
    /// 实例工作区不使用这一份，而是各自持有 <see cref="InstanceOperationState"/>（B2 步骤 1c）。
    /// </summary>
    public InstanceOperationState Operation { get; } = new();

    public ObservableCollection<LoaderChoiceViewModel> LoaderChoices { get; } = [];

    public ObservableCollection<GameVersionTypeChoice> VersionTypes { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> GameVersions { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> LoaderVersions { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> FabricApiVersions { get; } = [];

    public ObservableCollection<VersionChoiceViewModel> OptiFineVersions { get; } = [];

    [ObservableProperty]
    private InstanceItemViewModel? _selectedInstance;

    [ObservableProperty]
    private AccountItemViewModel? _selectedAccount;


    [ObservableProperty]
    private string _newInstanceName = string.Empty;

    [ObservableProperty]
    private GameVersionTypeChoice? _selectedVersionType;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedGameVersion;

    [ObservableProperty]
    private bool _isLoadingVersions;

    [ObservableProperty]
    private string _versionStatus = string.Empty;

    [ObservableProperty]
    private LoaderChoiceViewModel? _selectedNewLoader;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedLoaderVersion;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedFabricApiVersion;

    [ObservableProperty]
    private VersionChoiceViewModel? _selectedOptiFineVersion;

    [ObservableProperty]
    private bool _isLoadingComponents;

    [ObservableProperty]
    private string _componentStatus = string.Empty;

    [ObservableProperty]
    private bool _showFabricApiNotice;

    [ObservableProperty]
    private bool _showOptiFineSelector;

    [ObservableProperty]
    private bool _optiFineEnabled;

    [ObservableProperty]
    private string _newAccountName = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>实例列表正在读取（首次进入或刷新）；用于加载态，不表示任何长任务。</summary>
    [ObservableProperty]
    private bool _isLoadingInstances;

    /// <summary>实例读取失败原因；仅在没有可展示实例时呈现错误态。</summary>
    [ObservableProperty]
    private string _instanceLoadError = string.Empty;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private bool _installIndeterminate;

    [ObservableProperty]
    private double _installProgressValue;

    [ObservableProperty]
    private string _javaStatus = string.Empty;

    [ObservableProperty]
    private string _downloadSpeedText = string.Empty;

    [ObservableProperty]
    private string _downloadPauseButtonText = string.Empty;

    [ObservableProperty]
    private bool _isDownloadPaused;

    [ObservableProperty]
    private bool _canPauseDownload;

    [ObservableProperty]
    private bool _isCheckingHealth;

    [ObservableProperty]
    private bool _isAnalyzingCrash;

    public bool HasSelection => SelectedInstance is not null;

    public bool IsEmpty => Instances.Count == 0;

    /// <summary>实例列表加载中（此时不显示空态文案）。</summary>
    public bool ShowLoadingState => IsLoadingInstances && Instances.Count == 0;

    /// <summary>读取失败且没有任何可展示实例；失败原因通过 <see cref="InstanceLoadError"/> 呈现。</summary>
    public bool ShowErrorState => !IsLoadingInstances && HasInstanceLoadError && Instances.Count == 0;

    public bool HasInstanceLoadError => !string.IsNullOrWhiteSpace(InstanceLoadError);

    /// <summary>真正的空列表：读取完成、没有错误、也没有实例。</summary>
    public bool ShowEmptyState => !IsLoadingInstances && !HasInstanceLoadError && Instances.Count == 0;

    public string InstanceCountText => _localizer.Get("instances.count", Instances.Count);

    public string SelectedInstanceName => SelectedInstance?.Name ?? string.Empty;

    public string SelectedInstanceMeta => SelectedInstance?.VersionText ?? string.Empty;

    public string SelectedInstancePath => SelectedInstance is null
        ? string.Empty
        : InstancePaths.GetGameDirectory(_paths, SelectedInstance.Model);

    public string SelectedInstanceCreated => SelectedInstance is null
        ? string.Empty
        : _localizer.Get("instances.createdAt", SelectedInstance.Model.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd"));

    public string SelectedInstanceComponents => SelectedInstance is null
        ? string.Empty
        : GetInstanceComponents(SelectedInstance.Model);

    /// <summary>
    /// 组件摘要的统一入口；格式化实现见 <see cref="InstanceComponentsPresentation"/>，
    /// 实例列表、基本信息与修改页共用同一套文案。
    /// </summary>
    public string GetInstanceComponents(GameInstance model) =>
        InstanceComponentsPresentation.Describe(model, _localizer);

    public InstancesViewModel(
        IInstanceService instances,
        IAccountService accounts,
        IAuthServerService authServers,
        IMicrosoftAuthenticationService microsoftAuthentication,
        ILaunchService launchService,
        IVersionInstallService installService,
        ISettingsService settings,
        IDialogService dialogs,
        ILocalizer localizer,
        IAppPaths paths,
        IJavaLocator javaLocator,
        IJavaSelectionService javaSelection,
        IJavaInstallService javaInstall,
        IVersionResolver versionResolver,
        IVersionCatalog versionCatalog,
        IEulaService eula,
        ILoaderCatalog loaderCatalog,
        IDownloadController downloads,
        ICrashReportService crashReports,
        IDiagnosticPackageService diagnostics,
        Ecl.Minecraft.World.WorldWorkbenchService worldWorkbench,
        Ecl.Core.Storage.InstanceSourceService instanceSources,
        Ecl.Core.Storage.IWorkspaceService workspace,
        IDialogEngine dialogsEngine)
    {
        _instances = instances;
        _accounts = accounts;
        _authServers = authServers;
        _microsoftAuthentication = microsoftAuthentication;
        _launchService = launchService;
        _installService = installService;
        _settings = settings;
        _dialogs = dialogs;
        _localizer = localizer;
        _paths = paths;
        _javaLocator = javaLocator;
        _javaSelection = javaSelection;
        _javaInstall = javaInstall;
        _versionResolver = versionResolver;
        _versionCatalog = versionCatalog;
        _eula = eula;
        _loaderCatalog = loaderCatalog;
        _downloads = downloads;
        _crashReports = crashReports;
        _diagnostics = diagnostics;
        _worldWorkbench = worldWorkbench;
        _instanceSources = instanceSources;
        _workspace = workspace;
        _dialogsEngine = dialogsEngine;

        DownloadPauseButtonText = _localizer.Get("instances.download.pause");

        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.Vanilla, _localizer.Get("instances.loader.vanilla")));
        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.Fabric, _localizer.Get("instances.loader.fabric")));
        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.Forge, _localizer.Get("instances.loader.forge")));
        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.NeoForge, _localizer.Get("instances.loader.neoforge")));
        LoaderChoices.Add(new LoaderChoiceViewModel(LoaderKinds.Cleanroom, _localizer.Get("instances.loader.cleanroom")));
        _selectedNewLoader = LoaderChoices[0];

        VersionTypes.Add(new GameVersionTypeChoice(
            MinecraftVersionKind.Release,
            _localizer.Get("instances.versionType.release")));
        VersionTypes.Add(new GameVersionTypeChoice(
            MinecraftVersionKind.Snapshot,
            _localizer.Get("instances.versionType.snapshot")));
        VersionTypes.Add(new GameVersionTypeChoice(
            MinecraftVersionKind.AprilFools,
            _localizer.Get("instances.versionType.aprilFools")));
        _selectedVersionType = VersionTypes[0];
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await RefreshAuthServersAsync().ConfigureAwait(true);
        await RefreshAccountsAsync().ConfigureAwait(true);
        await RefreshInstancesAsync().ConfigureAwait(true);
        await UpdateJavaStatusAsync().ConfigureAwait(true);
        await LoadVersionsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RefreshVersionsAsync() => LoadVersionsAsync(refresh: true);

    /// <summary>
    /// 读取 Mojang 版本清单并按“正式版 / 快照版 / 愚人节版”过滤。
    /// 清单较长，服务内部有缓存；刷新按钮才会强制重新读取。
    /// </summary>
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
                GameVersions.Clear();
                SelectedGameVersion = null;
                VersionStatus = _localizer.Get("instances.version.failed", result.Error?.Message ?? string.Empty);
                return;
            }

            _allVersions = result.Value!;
            ApplyVersionFilter(keepSelection: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _allVersions = [];
            GameVersions.Clear();
            SelectedGameVersion = null;
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

        _ = RefreshComponentsAsync();
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
        if (_initialized && !_suppressVersionReload)
        {
            _ = RefreshComponentsAsync();
        }
    }

    partial void OnIsLoadingInstancesChanged(bool value) => RaiseInstanceStateChanged();

    partial void OnInstanceLoadErrorChanged(string value) => RaiseInstanceStateChanged();

    private void RaiseInstanceStateChanged()
    {
        OnPropertyChanged(nameof(ShowLoadingState));
        OnPropertyChanged(nameof(ShowErrorState));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasInstanceLoadError));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshAuthServersAsync().ConfigureAwait(true);
        await RefreshAccountsAsync().ConfigureAwait(true);
        await RefreshInstancesAsync().ConfigureAwait(true);
        await UpdateJavaStatusAsync().ConfigureAwait(true);
        await RefreshSourceGroupsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// “添加MC目录”（instances.md §4）：打开“已经选择的目录”弹窗。
    /// 弹窗内的解绑/删除都由领域服务执行；本方法只负责打开与刷新。
    /// </summary>
    [RelayCommand]
    private async Task AddDirectoryAsync()
    {
        if (!_dialogsEngine.IsAvailable)
        {
            return;
        }

        var dialog = new InstanceDirectoryDialogViewModel(
            _workspace,
            _instanceSources,
            _dialogsEngine,
            _dialogs,
            _localizer,
            DeleteInstancesInDirectoryAsync);

        await dialog.LoadAsync().ConfigureAwait(true);
        await _dialogsEngine.ShowAsync(new DialogRequest
        {
            Title = dialog.Title,
            Content = dialog,
            MinWidth = 520,
            MaxWidth = 680,
            MaxHeight = 640,
            InitialFocus = DialogInitialFocus.FirstFocusable,
        }).ConfigureAwait(true);

        await RefreshSourceGroupsAsync().ConfigureAwait(true);
        await RefreshInstancesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 目录行的“删除实例”：逐个使用实例卡片同一条删除计划与确认流程，
    /// 用户对每个实例都可以取消；外部只读版本不会被删除。
    /// </summary>
    private async Task DeleteInstancesInDirectoryAsync(string directoryPath)
    {
        var targets = Instances
            .Where(item => IsUnderDirectory(directoryPath, item.PathText))
            .ToArray();

        if (targets.Length == 0)
        {
            StatusMessage = _localizer.Get("instanceDirectories.noManagedInstances", directoryPath);
            return;
        }

        foreach (var target in targets)
        {
            SelectedInstance = target;
            await DeleteInstanceAsync(target).ConfigureAwait(true);
        }
    }

    private static bool IsUnderDirectory(string directoryPath, string candidatePath)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
            return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                   || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// “导入整合包”（instances.md §5.1）：只允许 .zip / .mrpack，读取清单后给出失败或成功状态。
    /// 成功后的“下载中心 → 实例版本 · 整合包模式”属于下载页迁移批次（B4），
    /// 当前构建不会伪造该流程，而是明确说明尚不可继续。
    /// </summary>
    [RelayCommand]
    private async Task ImportModpackAsync()
    {
        var archive = _dialogs.PickOpenFile(
            _localizer.Get("instances.modpack.pickTitle"),
            _localizer.Get("instances.modpack.filter"));
        if (string.IsNullOrWhiteSpace(archive))
        {
            return;
        }

        await ImportModpackFileAsync(archive).ConfigureAwait(true);
    }

    /// <summary>主动选择与全局拖拽共用的解析入口。</summary>
    public Task ImportModpackFileAsync(string archivePath)
    {
        var inspection = ModpackArchiveInspector.Read(archivePath);
        if (!inspection.IsSuccess)
        {
            _dialogs.ShowError(
                _localizer.Get("instances.modpack.title"),
                _localizer.Get("instances.modpack.notFound"));
            return Task.CompletedTask;
        }

        var metadata = inspection.Value!;
        _dialogs.ShowInfo(
            _localizer.Get("instances.modpack.title"),
            _localizer.Get("instances.modpack.notAvailable", metadata.Name));
        return Task.CompletedTask;
    }

    private async Task RefreshSourceGroupsAsync()
    {
        try
        {
            var catalog = await _instanceSources.ScanAllAsync();
            SourceGroups.Clear();
            foreach (var group in catalog.Groups)
            {
                SourceGroups.Add(new InstanceSourceGroupViewModel(
                    group.Source.Name,
                    group.Source.Path,
                    group.IsAccessible,
                    group.Instances.Select(instance => new InstanceSourceItemViewModel(
                        instance.Name,
                        instance.GameVersion ?? _localizer.Get("instances.source.versionUnknown"),
                        instance.Loader,
                        instance.CanLaunch)
                    {
                        Availability = instance.CanLaunch
                            ? _localizer.Get("instances.source.managed")
                            : _localizer.Get("instances.source.readOnly"),
                    }).ToArray(),
                    _localizer));
            }

            OnPropertyChanged(nameof(HasSourceGroups));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"实例来源扫描失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshComponentsAsync()
    {
        ClearComponentChoices();
        ComponentStatus = string.Empty;

        var loader = SelectedNewLoader?.Id ?? LoaderKinds.Vanilla;
        if (loader == LoaderKinds.Vanilla)
        {
            return;
        }

        var version = SelectedGameVersion?.Value;
        if (string.IsNullOrWhiteSpace(version))
        {
            ComponentStatus = _localizer.Get("instances.version.select");
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
                if (!loaders.IsSuccess)
                {
                    ComponentStatus = _localizer.Get("instances.components.failed", loaders.Error?.Message ?? string.Empty);
                    return;
                }

                foreach (var item in loaders.Value!)
                {
                    LoaderVersions.Add(FormatLoaderVersion(item));
                }

                SelectedLoaderVersion = LoaderVersions.FirstOrDefault();

                var apiVersions = await _loaderCatalog.GetFabricApiVersionsAsync(version).ConfigureAwait(true);
                if (!apiVersions.IsSuccess)
                {
                    ComponentStatus = _localizer.Get("instances.components.failed", apiVersions.Error?.Message ?? string.Empty);
                    return;
                }

                foreach (var item in apiVersions.Value!)
                {
                    FabricApiVersions.Add(new VersionChoiceViewModel(
                        item.Version,
                        FormatVersionWithDate(item.Version, item.ReleasedAt)));
                }

                SelectedFabricApiVersion = FabricApiVersions.FirstOrDefault();
                ComponentStatus = LoaderVersions.Count == 0
                    ? _localizer.Get("instances.loader.noVersions")
                    : FabricApiVersions.Count == 0
                        ? _localizer.Get("instances.fabricApi.none")
                        : string.Empty;
                return;
            }

            if ((loader == LoaderKinds.Forge || loader == LoaderKinds.NeoForge) &&
                LoaderKinds.TryParse(loader, out var installerKind))
            {
                var loaders = installerKind == LoaderKind.Forge
                    ? await _loaderCatalog.GetForgeVersionsAsync(version).ConfigureAwait(true)
                    : await _loaderCatalog.GetNeoForgeVersionsAsync(version).ConfigureAwait(true);

                if (!loaders.IsSuccess)
                {
                    ComponentStatus = _localizer.Get("instances.components.failed", loaders.Error?.Message ?? string.Empty);
                    return;
                }

                foreach (var item in loaders.Value!)
                {
                    LoaderVersions.Add(FormatLoaderVersion(item));
                }

                SelectedLoaderVersion = LoaderVersions.FirstOrDefault();

                if (installerKind == LoaderKind.Forge)
                {
                    var forgeOptiFineVersions = await _loaderCatalog.GetOptiFineVersionsAsync(version).ConfigureAwait(true);
                    if (!forgeOptiFineVersions.IsSuccess)
                    {
                        ComponentStatus = _localizer.Get("instances.components.failed", forgeOptiFineVersions.Error?.Message ?? string.Empty);
                        return;
                    }

                    OptiFineVersions.Add(new VersionChoiceViewModel(null, _localizer.Get("instances.optifine.none")));
                    foreach (var item in forgeOptiFineVersions.Value!)
                    {
                        OptiFineVersions.Add(new VersionChoiceViewModel(
                            item.Key,
                            FormatVersionWithDate(item.FileName, item.ReleasedAt)));
                    }

                    SelectedOptiFineVersion = OptiFineVersions[0];
                }

                ComponentStatus = LoaderVersions.Count == 0
                    ? _localizer.Get("instances.loader.noVersions")
                    : string.Empty;
                return;
            }

            var cleanroomVersions = await _loaderCatalog.GetCleanroomVersionsAsync(version).ConfigureAwait(true);
            if (!cleanroomVersions.IsSuccess)
            {
                ComponentStatus = _localizer.Get("instances.components.failed", cleanroomVersions.Error?.Message ?? string.Empty);
                return;
            }

            foreach (var item in cleanroomVersions.Value!)
            {
                LoaderVersions.Add(FormatLoaderVersion(item));
            }

            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();

            var optiFineVersions = await _loaderCatalog.GetOptiFineVersionsAsync(version).ConfigureAwait(true);
            if (!optiFineVersions.IsSuccess)
            {
                ComponentStatus = _localizer.Get("instances.components.failed", optiFineVersions.Error?.Message ?? string.Empty);
                return;
            }

            OptiFineVersions.Add(new VersionChoiceViewModel(null, _localizer.Get("instances.optifine.none")));
            foreach (var item in optiFineVersions.Value!)
            {
                OptiFineVersions.Add(new VersionChoiceViewModel(
                    item.Key,
                    FormatVersionWithDate(item.FileName, item.ReleasedAt)));
            }

            SelectedOptiFineVersion = OptiFineVersions[0];
            ComponentStatus = LoaderVersions.Count == 0
                ? _localizer.Get("instances.loader.noVersions")
                : string.Empty;
        }
        catch (Exception exception)
        {
            ComponentStatus = _localizer.Get("instances.components.failed", exception.Message);
        }
        finally
        {
            IsLoadingComponents = false;
        }
    }

    private void ClearComponentChoices()
    {
        LoaderVersions.Clear();
        FabricApiVersions.Clear();
        OptiFineVersions.Clear();
        SelectedLoaderVersion = null;
        SelectedFabricApiVersion = null;
        SelectedOptiFineVersion = null;
    }

    private VersionChoiceViewModel FormatLoaderVersion(LoaderVersionInfo version) =>
        new(version.Version, FormatVersionWithDate(version.Version, version.ReleasedAt));

    private string FormatVersionWithDate(string version, DateTimeOffset? releasedAt)
    {
        var date = releasedAt?.ToLocalTime().ToString("yyyy-MM-dd")
                   ?? _localizer.Get("instances.versionDateUnknown");
        return _localizer.Get("instances.versionWithDate", version, date);
    }

    [RelayCommand]
    private void ToggleDownloadPause()
    {
        if (!CanPauseDownload)
        {
            return;
        }

        if (IsDownloadPaused)
        {
            _downloads.Resume();
            IsDownloadPaused = false;
            DownloadPauseButtonText = _localizer.Get("instances.download.pause");
        }
        else
        {
            _downloads.Pause();
            IsDownloadPaused = true;
            DownloadPauseButtonText = _localizer.Get("instances.download.resume");
            DownloadSpeedText = _localizer.Get("instances.download.paused");
        }
    }

    private void BeginDownloadUi()
    {
        _downloads.Resume();
        CanPauseDownload = true;
        IsDownloadPaused = false;
        DownloadSpeedText = string.Empty;
        DownloadPauseButtonText = _localizer.Get("instances.download.pause");
    }

    private void EndDownloadUi()
    {
        _downloads.Resume();
        CanPauseDownload = false;
        IsDownloadPaused = false;
        DownloadSpeedText = string.Empty;
        DownloadPauseButtonText = _localizer.Get("instances.download.pause");
    }

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / 1024 / 1024:F1} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:F0} KB/s",
        > 0 => $"{bytesPerSecond:F0} B/s",
        _ => string.Empty,
    };

    [RelayCommand]
    private async Task CreateInstanceAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var version = SelectedGameVersion?.Value;
        var name = NewInstanceName.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            StatusMessage = _localizer.Get("instances.version.select");
            return;
        }

        // 实例名可选；留空时使用游戏版本作为默认名称。
        if (name.Length == 0)
        {
            name = version;
        }

        var loader = SelectedNewLoader?.Id ?? LoaderKinds.Vanilla;
        if (loader == LoaderKinds.Cleanroom &&
            !string.Equals(version, "1.12.2", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = _localizer.Get("instances.loader.cleanroomOnly");
            return;
        }

        string? loaderVersion = null;
        string? fabricApiVersion = null;
        string? optiFineVersion = null;

        if (loader != LoaderKinds.Vanilla)
        {
            loaderVersion = SelectedLoaderVersion?.Value;
            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                StatusMessage = _localizer.Get("instances.loader.versionRequired");
                return;
            }
        }

        if (loader == LoaderKinds.Fabric)
        {
            fabricApiVersion = SelectedFabricApiVersion?.Value;
            if (string.IsNullOrWhiteSpace(fabricApiVersion))
            {
                StatusMessage = _localizer.Get("instances.fabricApi.none");
                return;
            }
        }

        if (loader == LoaderKinds.Cleanroom || loader == LoaderKinds.Forge)
        {
            optiFineVersion = SelectedOptiFineVersion?.Value;
        }

        IsBusy = true;
        try
        {
            var result = await _instances
                .CreateAsync(name, version, loader, loaderVersion, fabricApiVersion, optiFineVersion)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.Message ?? string.Empty;
                return;
            }

            NewInstanceName = string.Empty;
            await RefreshInstancesAsync().ConfigureAwait(true);
            SelectedInstance = Instances.FirstOrDefault(item => item.Model.Id == result.Value!.Id) ?? Instances.FirstOrDefault();
            StatusMessage = _localizer.Get("instances.created", result.Value!.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteInstanceAsync(InstanceItemViewModel? instance)
    {
        instance ??= SelectedInstance;
        if (instance is null || IsBusy)
        {
            return;
        }

        var planResult = await _instances.GetDeletionPlanAsync(instance.Model.Id).ConfigureAwait(true);
        if (!planResult.IsSuccess)
        {
            StatusMessage = planResult.Error?.Message ?? string.Empty;
            return;
        }

        var plan = planResult.Value!;

        // 默认行为：有 ECL 托管数据时删除托管数据；导入实例只移除记录，外部目录永远保留。
        var mode = plan.HasManagedGameData
            ? InstanceDeletionMode.DeleteManagedData
            : InstanceDeletionMode.RemoveRecordOnly;

        if (_settings.Get<bool>("launcher.confirmBeforeDelete"))
        {
            var choice = _dialogs.ChooseInstanceDeletion(
                _localizer.Get("instances.deletePlanTitle"),
                BuildDeletionMessage(plan),
                plan.HasManagedGameData);

            if (choice == InstanceDeletionChoice.Cancel)
            {
                return;
            }

            mode = choice == InstanceDeletionChoice.DeleteManagedData
                ? InstanceDeletionMode.DeleteManagedData
                : InstanceDeletionMode.RemoveRecordOnly;
        }

        IsBusy = true;
        try
        {
            var result = await _instances
                .DeleteAsync(instance.Model.Id, new InstanceDeleteOptions { Mode = mode })
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.Message ?? string.Empty;
                return;
            }

            await RefreshInstancesAsync().ConfigureAwait(true);
            StatusMessage = mode == InstanceDeletionMode.DeleteManagedData
                ? _localizer.Get("instances.deleted", instance.Name)
                : _localizer.Get("instances.deletedRecordOnly", instance.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把结构化删除计划转成用户可读的影响预览，而不是只确认实例名称。</summary>
    private string BuildDeletionMessage(InstanceDeletionPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine(_localizer.Get("instances.deletePlanHeader", plan.InstanceName));
        builder.AppendLine();
        builder.AppendLine(_localizer.Get("instances.deletePlanRecord", plan.RecordDirectory));

        if (plan.Find(InstanceDeletionEntryKind.ManagedGameData) is { } managedGameData)
        {
            builder.AppendLine(_localizer.Get("instances.deletePlanManagedData", managedGameData.Path));
            builder.AppendLine();
            builder.Append(_localizer.Get("instances.deletePlanChoose"));
            return builder.ToString();
        }

        if (plan.Find(InstanceDeletionEntryKind.ExternalGameData) is { } externalGameData)
        {
            builder.AppendLine(_localizer.Get("instances.deletePlanExternal", externalGameData.Path));
            if (plan.IsShared)
            {
                builder.AppendLine(_localizer.Get(
                    "instances.deletePlanShared",
                    string.Join("、", plan.SharedWith.Select(reference => reference.Name))));
            }

            builder.AppendLine();
            builder.Append(_localizer.Get("instances.deletePlanRecordOnlyNote"));
        }

        return builder.ToString();
    }

    [RelayCommand]
    private Task LaunchAsync() => LaunchForAsync(SelectedInstance, Operation, dryRun: false);

    [RelayCommand]
    private Task DryRunAsync() => LaunchForAsync(SelectedInstance, Operation, dryRun: true);

    [RelayCommand]
    private Task AnalyzeCrashAsync() => AnalyzeCrashForAsync(SelectedInstance, Operation);

    /// <summary>
    /// 分析指定实例最近一次崩溃；进度写入调用方自己的状态，
    /// 因此实例工作区与主页/列表页同时使用时互不覆盖。
    /// </summary>
    internal async Task AnalyzeCrashForAsync(InstanceItemViewModel? item, InstanceOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (item is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        if (IsAnalyzingCrash)
        {
            return;
        }

        IsAnalyzingCrash = true;
        state.CrashAnalysisText = _localizer.Get("diagnostics.analyzing");
        try
        {
            var result = await _crashReports
                .AnalyzeLatestAsync(item.Model.Id)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                state.CrashAnalysisText = result.Error?.ToString() ?? _localizer.Get("diagnostics.failed");
                return;
            }

            if (result.Value is null)
            {
                state.CrashAnalysisText = _localizer.Get("diagnostics.noCrash");
                return;
            }

            var analysis = result.Value;
            var builder = new System.Text.StringBuilder();
            builder.AppendLine(analysis.Summary);
            if (!string.IsNullOrWhiteSpace(analysis.SourcePath))
            {
                builder.AppendLine(_localizer.Get("diagnostics.source", analysis.SourcePath));
            }

            if (analysis.Suspects.Count > 0)
            {
                builder.AppendLine(_localizer.Get("diagnostics.suspects"));
                foreach (var suspect in analysis.Suspects)
                {
                    builder.AppendLine($"  {suspect.Name ?? suspect.ModId} ({suspect.ModId}) · {suspect.Score}");
                    foreach (var evidence in suspect.Evidence.Take(2))
                    {
                        builder.AppendLine($"    {evidence}");
                    }
                }
            }

            if (analysis.KeyLines.Count > 0)
            {
                builder.AppendLine(_localizer.Get("diagnostics.keyLines"));
                foreach (var line in analysis.KeyLines.Take(6))
                {
                    builder.AppendLine($"  {line}");
                }
            }

            state.CrashAnalysisText = builder.ToString().TrimEnd();
        }
        catch (Exception exception)
        {
            state.CrashAnalysisText = exception.Message;
        }
        finally
        {
            IsAnalyzingCrash = false;
        }
    }

    [RelayCommand]
    private Task ExportDiagnosticsAsync() => ExportDiagnosticsForAsync(SelectedInstance, Operation);

    [RelayCommand]
    private Task RepairFilesAsync() => RepairForAsync(SelectedInstance, Operation);

    [RelayCommand]
    private Task CheckHealthAsync() => CheckHealthForAsync(SelectedInstance, Operation);

    /// <summary>导出指定实例的诊断包；结果写入调用方自己的状态。</summary>
    internal async Task ExportDiagnosticsForAsync(InstanceItemViewModel? item, InstanceOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (item is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        state.DiagnosticStatus = _localizer.Get("diagnostics.exporting");
        try
        {
            var result = await _diagnostics
                .ExportAsync(item.Model.Id)
                .ConfigureAwait(true);

            state.DiagnosticStatus = result.IsSuccess
                ? _localizer.Get("diagnostics.exported", result.Value!.OutputPath, result.Value.Entries)
                : result.Error?.ToString() ?? _localizer.Get("diagnostics.exportFailed");
        }
        catch (Exception exception)
        {
            state.DiagnosticStatus = exception.Message;
        }
    }

    /// <summary>补全/修复指定实例：确认后走与安装相同的受控流程。</summary>
    internal async Task RepairForAsync(InstanceItemViewModel? item, InstanceOperationState state)
    {
        if (item is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("instances.repairTitle"),
                _localizer.Get("instances.repairConfirm", item.Name)))
        {
            return;
        }

        await InstallForAsync(item, state).ConfigureAwait(true);
    }

    /// <summary>对指定实例做启动前健康检查（试运行 + 账户 + 模组摘要）。</summary>
    internal async Task CheckHealthForAsync(InstanceItemViewModel? item, InstanceOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (item is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        if (IsBusy || IsCheckingHealth)
        {
            return;
        }

        IsCheckingHealth = true;
        IsBusy = true;
        state.HealthReportText = _localizer.Get("instances.healthChecking");
        try
        {
            var account = await _accounts.GetDefaultAccountAsync().ConfigureAwait(true);
            var report = await _launchService
                .LaunchAsync(item.Model.Id, dryRun: true)
                .ConfigureAwait(true);
            state.LaunchSteps.Clear();
            foreach (var step in report.Steps)
            {
                state.LaunchSteps.Add(new LaunchStepItemViewModel(step, _localizer));
            }

            // 命令预览会展示给用户（也可能被截图），token 只保留两端、中间脱敏。
            state.LaunchCommandPreview = SecretRedaction.CommandLine(report.CommandLine ?? string.Empty);
            var launchReady = report.Success && report.Steps.All(step => step.Status is LaunchStepStatus.Success or LaunchStepStatus.Skipped);
            state.LaunchStatus = _localizer.Get(launchReady ? "instances.healthLaunchReady" : "instances.healthLaunchIssues");
            var modSummary = GetModHealthSummary is null
                ? _localizer.Get("instances.healthModsUnavailable")
                : await GetModHealthSummary(item.Model.Id).ConfigureAwait(true);
            var accountStatus = account is null
                ? _localizer.Get("instances.healthAccountMissing")
                : _localizer.Get("instances.healthAccountReady", account.Name);
            state.HealthReportText = $"{accountStatus}{Environment.NewLine}{modSummary}";
        }
        catch (Exception exception)
        {
            state.HealthReportText = _localizer.Get("instances.healthFailed", exception.Message);
        }
        finally
        {
            IsCheckingHealth = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task InstallAsync() => InstallForAsync(SelectedInstance, Operation);

    /// <summary>对指定实例执行版本/加载器补全安装；进度写入调用方自己的状态。</summary>
    internal async Task InstallForAsync(InstanceItemViewModel? item, InstanceOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (item is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        IsInstalling = true;
        InstallIndeterminate = true;
        InstallProgressValue = 0;
        state.InstallStatus = _localizer.Get("instances.installing");
        BeginDownloadUi();

        var progress = new Progress<InstallProgress>(update =>
        {
            InstallIndeterminate = update.IsIndeterminate;
            InstallProgressValue = update.Percent;
            state.InstallStatus = update.TotalFiles > 0
                ? $"{update.Message} ({update.CompletedFiles}/{update.TotalFiles})"
                : update.Message;
            if (update.BytesPerSecond > 0)
            {
                DownloadSpeedText = FormatSpeed(update.BytesPerSecond);
            }
        });

        try
        {
            var result = await _installService
                .InstallAsync(item.Model.Id, progress)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                state.InstallStatus = result.Error?.Message ?? _localizer.Get("instances.installFailed");
                StatusMessage = state.InstallStatus;
                return;
            }

            InstallIndeterminate = false;
            InstallProgressValue = 100;
            state.InstallStatus = _localizer.Get("instances.installSucceeded");
            StatusMessage = state.InstallStatus;
            await UpdateJavaStatusAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            state.InstallStatus = _localizer.Get("instances.installFailed");
            StatusMessage = _localizer.Get("install.failed", exception.Message);
        }
        finally
        {
            IsInstalling = false;
            IsBusy = false;
            EndDownloadUi();
        }
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        var name = NewAccountName.Trim();
        if (name.Length == 0)
        {
            StatusMessage = _localizer.Get("instances.accountNameRequired");
            return;
        }

        var result = await _accounts.AddOfflineAsync(name, makeDefault: Accounts.Count == 0).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            StatusMessage = result.Error?.Message ?? string.Empty;
            return;
        }

        NewAccountName = string.Empty;
        await RefreshAccountsAsync().ConfigureAwait(true);
        if (result.Value is not null)
        {
            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == result.Value.Id) ?? SelectedAccount;
        }

        StatusMessage = _localizer.Get("instances.accountAdded", result.Value!.Name);
    }

    /// <summary>
    /// 实机验证入口：只有用户主动点击才会打开浏览器并开始 Microsoft 登录。
    /// 启动器不会在启动时自动登录，也不会代替用户输入凭据。
    /// </summary>
    [RelayCommand]
    private async Task SignInMicrosoftAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var clientId = MicrosoftClientIdentity.DefaultClientId;

        IsBusy = true;
        StatusMessage = _localizer.Get("instances.microsoftSigningIn");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            Result<Account> result;
            try
            {
                result = await _microsoftAuthentication
                    .SignInAsync(new MicrosoftAuthenticationOptions { ClientId = clientId }, OpenBrowserAsync, timeout.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = _localizer.Get(
                    "instances.microsoftSignInFailed",
                    "auth.callback_timeout",
                    _localizer.Get("instances.microsoftSignInTimeout"));
                return;
            }

            if (!result.IsSuccess)
            {
                StatusMessage = _localizer.Get(
                    "instances.microsoftSignInFailed",
                    result.Error!.Code,
                    result.Error.Message);
                return;
            }

            var added = await _accounts
                .AddAsync(result.Value!, makeDefault: Accounts.Count == 0)
                .ConfigureAwait(true);
            if (!added.IsSuccess)
            {
                StatusMessage = added.Error!.Message;
                return;
            }

            await RefreshAccountsAsync().ConfigureAwait(true);
            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == added.Value!.Id) ?? SelectedAccount;
            StatusMessage = _localizer.Get("instances.microsoftSignedIn", added.Value!.Name);
        }
        catch (Exception exception)
        {
            StatusMessage = _localizer.Get(
                "instances.microsoftSignInFailed",
                "auth.exception",
                exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 第三方账户只在这里创建：认证服务器地址（必填）与注册链接（可选）只出现在该对话框内。
    /// </summary>
    [RelayCommand]
    private async Task AddThirdPartyAccountAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var dialog = new AuthlibAccountDialog(_authServers, _accounts, _localizer)
            {
                Owner = Application.Current?.MainWindow,
            };

            if (dialog.ShowDialog() != true || dialog.ResultAccount is null)
            {
                return;
            }

            await RefreshAuthServersAsync().ConfigureAwait(true);
            await RefreshAccountsAsync().ConfigureAwait(true);
            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == dialog.ResultAccount.Id) ?? SelectedAccount;
            StatusMessage = _localizer.Get("instances.thirdPartySignedIn", dialog.ResultAccount.Name);
        }
        catch (Exception exception)
        {
            StatusMessage = _localizer.Get("instances.thirdPartySignInFailed", "ui.exception", exception.Message);
        }
    }

    /// <summary>账户设置：编辑第三方账户所属服务器的地址与注册链接，可选重新登录。</summary>
    [RelayCommand]
    private async Task EditAccountSettingsAsync()
    {
        if (IsBusy || SelectedAccount is null || SelectedAccount.Model.Type != AccountType.ThirdParty)
        {
            return;
        }

        var account = SelectedAccount.Model;
        try
        {
            var server = string.IsNullOrWhiteSpace(account.AuthServerId)
                ? null
                : await _authServers.GetAsync(account.AuthServerId).ConfigureAwait(true);
            if (server is null)
            {
                StatusMessage = _localizer.Get("instances.authServerMissing");
                return;
            }

            var dialog = new AuthlibAccountDialog(_authServers, _accounts, _localizer, server, account)
            {
                Owner = Application.Current?.MainWindow,
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            await RefreshAuthServersAsync().ConfigureAwait(true);
            await RefreshAccountsAsync().ConfigureAwait(true);
            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == account.Id) ?? SelectedAccount;
            StatusMessage = _localizer.Get("instances.accountSettingsSaved");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizer.Get("instances.thirdPartySignInFailed", "ui.exception", exception.Message);
        }
    }

    /// <summary>扫描游戏目录的 versions 文件夹，把其它启动器安装的版本导入为实例。</summary>
    [RelayCommand]
    private async Task ImportVersionsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var detected = await _instances.ScanVersionsAsync().ConfigureAwait(true);
        if (detected.Count == 0)
        {
            StatusMessage = _localizer.Get("instances.importNone");
            return;
        }

        var dialog = new VersionImportDialog(_instances, detected, _localizer)
        {
            Owner = Application.Current?.MainWindow,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RefreshInstancesAsync().ConfigureAwait(true);
        StatusMessage = _localizer.Get("instances.imported", dialog.ImportedCount);
    }

    private static Task OpenBrowserAsync(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    partial void OnSelectedNewLoaderChanged(LoaderChoiceViewModel? value)
    {
        ShowFabricApiNotice = string.Equals(value?.Id, LoaderKinds.Fabric, StringComparison.OrdinalIgnoreCase);
        ShowOptiFineSelector = string.Equals(value?.Id, LoaderKinds.Cleanroom, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(value?.Id, LoaderKinds.Forge, StringComparison.OrdinalIgnoreCase);
        OptiFineEnabled = ShowOptiFineSelector;

        if (!_initialized)
        {
            ClearComponentChoices();
            return;
        }

        _ = RefreshComponentsAsync();
    }

    /// <summary>实例库只负责展示；打开工作区时才进入运行设置、模组、日志与诊断。</summary>
    [RelayCommand]
    private void OpenWorkspace(InstanceItemViewModel? instance)
    {
        instance ??= SelectedInstance;
        if (instance is null)
        {
            return;
        }

        SelectedInstance = instance;
        NavigateToDetails?.Invoke(instance.Model.Id);
    }

    [RelayCommand]
    private void BackToInstances() => NavigateBackFromDetails?.Invoke();

    [RelayCommand]
    private void OpenInstanceFolder() => OpenInstanceFolderFor(SelectedInstance);

    [RelayCommand]
    private void OpenModsFolder() => OpenModsFolderFor(SelectedInstance);

    [RelayCommand]
    private void ExportLatestLog()
    {
        var message = ExportLatestLogFor(SelectedInstance);
        if (message.Length > 0)
        {
            StatusMessage = message;
        }
    }

    /// <summary>打开指定实例的数据目录（实例工作区使用显式实例）。</summary>
    internal void OpenInstanceFolderFor(InstanceItemViewModel? item) =>
        OpenSelectedDirectory(item is null ? null : _paths.GetInstanceDirectory(item.Model.Id));

    /// <summary>打开指定实例的模组目录。</summary>
    internal void OpenModsFolderFor(InstanceItemViewModel? item) =>
        OpenSelectedDirectory(item is null ? null : Path.Combine(InstancePaths.GetGameDirectory(_paths, item.Model), "mods"));

    /// <summary>
    /// 导出指定实例的 latest.log，返回给调用方显示的结果文案（空字符串表示用户取消或无操作）。
    /// </summary>
    internal string ExportLatestLogFor(InstanceItemViewModel? item)
    {
        if (item is null)
        {
            return string.Empty;
        }

        var source = Path.Combine(InstancePaths.GetGameDirectory(_paths, item.Model), "logs", "latest.log");
        if (!File.Exists(source))
        {
            return "该实例尚无 latest.log；请先运行一次游戏。";
        }

        if (!_dialogs.Confirm(
                "导出运行日志",
                "latest.log 可能包含服务器地址、玩家名称或其他运行环境信息。请确认目标文件仅分享给可信任的人。"))
        {
            return string.Empty;
        }

        var destination = _dialogs.PickSaveFile(
            "导出最近一次运行日志",
            "日志文件 (*.log)|*.log|所有文件 (*.*)|*.*",
            $"{item.Name}-latest.log");
        if (destination is null)
        {
            return string.Empty;
        }

        try
        {
            File.Copy(source, destination, overwrite: true);
            return $"日志已导出：{destination}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"导出日志失败：{exception.Message}";
        }
    }

    /// <summary>打开"世界与服务器"工作台（列表页与主页共用的全局选中实例入口）。</summary>
    [RelayCommand]
    private void OpenWorldWorkbench() => OpenWorldWorkbenchFor(SelectedInstance);

    /// <summary>打开指定实例的“世界与服务器”工作台（显式实例，不读全局选中状态）。</summary>
    internal void OpenWorldWorkbenchFor(InstanceItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var window = new WorldWorkbenchWindow(
            _worldWorkbench,
            _dialogs,
            item.Model.Id,
            item.Name)
        {
            Owner = Application.Current.MainWindow,
        };
        window.ShowDialog();
    }

    private void OpenSelectedDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = $"无法打开目录：{exception.Message}";
        }
    }

    partial void OnSelectedInstanceChanged(InstanceItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedInstanceName));
        OnPropertyChanged(nameof(SelectedInstanceMeta));
        OnPropertyChanged(nameof(SelectedInstancePath));
        OnPropertyChanged(nameof(SelectedInstanceCreated));
        OnPropertyChanged(nameof(SelectedInstanceComponents));

        Operation.ResetLaunchReport();
        InstallProgressValue = 0;
        InstallIndeterminate = false;

        _ = RefreshJavaSelectionAsync();
    }

    partial void OnSelectedAccountChanged(AccountItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanOpenAccountSettings));

        if (_suppressAccountSelection || value is null || value.IsDefault)
        {
            return;
        }

        _ = SetDefaultAccountAsync(value.Id);
    }

    private bool EnsureEulaConsent()
    {
        if (_eula.HasAccepted)
        {
            return true;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("eula.title"),
                _localizer.Get("eula.prompt")))
        {
            return false;
        }

        var result = _eula.Accept();
        if (!result.IsSuccess)
        {
            _dialogs.ShowError(
                _localizer.Get("eula.saveFailedTitle"),
                result.Error?.Message ?? string.Empty);
            return false;
        }

        _dialogs.ShowInfo(
            _localizer.Get("eula.noticeTitle"),
            _localizer.Get("eula.multiplayerNotice"));

        return true;
    }

    private async Task<bool> ConfirmJavaBeforeLaunchAsync(GameInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var versionResult = await _versionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);
        if (!versionResult.IsSuccess)
        {
            return true;
        }

        var selection = await _javaSelection
            .ResolveAsync(instance, versionResult.Value)
            .ConfigureAwait(true);

        if (selection.IsBlocked)
        {
            var blockedMessage = selection.Compatibility?.Status switch
            {
                JavaCompatibilityStatus.UnsupportedArchitecture => _localizer.Get(
                    "instances.java.unsupported32",
                    selection.Runtime?.DisplayName ?? "Java"),
                JavaCompatibilityStatus.RequirementsConflict => _localizer.Get(
                    "instances.java.conflict",
                    selection.Decision?.Requirements?.Explain() ?? string.Empty),
                _ => selection.BlockReason ?? _localizer.Get("instances.java.blocked"),
            };
            _dialogs.ShowError(_localizer.Get("instances.java.blockedTitle"), blockedMessage);
            return false;
        }

        if (selection.Runtime is null)
        {
            var shouldDownload = _dialogs.Confirm(
                _localizer.Get("instances.java.downloadTitle"),
                _localizer.Get("instances.java.downloadPrompt"));

            if (shouldDownload)
            {
                await DownloadJavaForLaunchAsync(instance).ConfigureAwait(true);
            }

            return false;
        }

        if (selection.Compatibility is null ||
            selection.Compatibility.IsCompatible)
        {
            return true;
        }

        // 未知要求不是不兼容；按优先级选择后只提示，不弹风险确认。
        if (selection.Compatibility.Status == JavaCompatibilityStatus.UnknownRequirement)
        {
            return true;
        }

        // 自动选择永远不应选中已知不兼容的运行时；如果出现，这是内部错误而不是用户风险。
        if (!selection.IsExplicitOverride)
        {
            _dialogs.ShowError(
                _localizer.Get("instances.java.blockedTitle"),
                _localizer.Get("instances.java.autoSelectionIncompatible"));
            return false;
        }

        var actual = selection.Runtime.Version?.Raw ?? selection.Runtime.DisplayName;
        var required = selection.Requirement?.Describe() ?? "Java";
        var message = selection.Compatibility.Status == JavaCompatibilityStatus.Unknown
            ? _localizer.Get("instances.java.unknownPrompt", actual)
            : _localizer.Get("instances.java.incompatiblePrompt", actual, required);
        return _dialogs.Confirm(
            _localizer.Get(
                selection.Compatibility.Status == JavaCompatibilityStatus.Unknown
                    ? "instances.java.unknownTitle"
                    : "instances.java.incompatibleTitle"),
            message,
            selection.CompatibilityAcknowledgementToken);
    }

    /// <summary>
    /// 启动前的 Java 补装：把当前实例需要的运行时安装到本机并写回该实例的覆盖路径。
    /// 页面上的“下载 Java”按钮属于实例工作区自有的运行设置（B2 步骤 1c），这里只服务启动流程。
    /// </summary>
    private async Task DownloadJavaForLaunchAsync(GameInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = _localizer.Get("instances.java.downloading");
        BeginDownloadUi();
        try
        {
            var progress = new Progress<JavaInstallProgress>(update =>
            {
                if (update.BytesPerSecond > 0)
                {
                    DownloadSpeedText = FormatSpeed(update.BytesPerSecond);
                }
            });
            StatusMessage = await ProvisionJavaAsync(instance, progress).ConfigureAwait(true);
            await UpdateJavaStatusAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            EndDownloadUi();
        }
    }

    /// <summary>
    /// 为指定实例准备 Java 运行时并返回结果文案（内容下载页复用）。
    /// 目标实例由调用方显式给出，不读取共享的选择状态。
    /// </summary>
    public async Task<string> EnsureJavaForInstanceAsync(string instanceId)
    {
        var instance = Instances.FirstOrDefault(item =>
            string.Equals(item.Model.Id, instanceId, StringComparison.Ordinal));
        if (instance is null)
        {
            return _localizer.Get("instances.selectFirst");
        }

        try
        {
            var status = await ProvisionJavaAsync(instance.Model, progress: null).ConfigureAwait(true);
            await UpdateJavaStatusAsync().ConfigureAwait(true);
            return status;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private async Task<string> ProvisionJavaAsync(GameInstance instance, IProgress<JavaInstallProgress>? progress)
    {
        var versionResult = await _versionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);
        var version = versionResult.IsSuccess ? versionResult.Value : null;
        var requirements = await _javaSelection
            .ResolveRequirementsAsync(instance, version)
            .ConfigureAwait(true);
        if (requirements.HasConflicts)
        {
            return _localizer.Get("instances.java.conflict", requirements.Explain());
        }

        var result = await _javaInstall
            .EnsureAsync(requirements.ToRequirement(), progress)
            .ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            return result.Error?.Message ?? _localizer.Get("instances.java.downloadFailed");
        }

        if (result.Value is not null)
        {
            _settings.TrySetRaw("instance.java.executablePath", result.Value.ExecutablePath, instance.Id, out _);
        }

        return _localizer.Get("instances.java.downloadSucceeded");
    }

    /// <summary>
    /// 按工作区作用域创建该实例自有的 Java/内存/JVM 编辑状态（B2 步骤 1c）：
    /// 页面编辑不再写共享的 <see cref="SelectedInstance"/> 字段。
    /// </summary>
    internal InstanceRuntimeSettingsViewModel CreateRuntimeSettings(GameInstance instance) =>
        new(
            instance,
            _settings,
            _javaLocator,
            _javaSelection,
            _javaInstall,
            _versionResolver,
            _downloads,
            _dialogs,
            _localizer);

    private async Task RefreshJavaSelectionAsync() => await UpdateJavaStatusAsync().ConfigureAwait(true);

    private async Task UpdateJavaStatusAsync()
    {
        if (SelectedInstance is null)
        {
            JavaStatus = string.Empty;
            return;
        }

        try
        {
            var instance = SelectedInstance.Model;
            var versionResult = await _versionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);
            if (!versionResult.IsSuccess)
            {
                JavaStatus = _localizer.Get("instances.java.installFirst");
                return;
            }

            var selection = await _javaSelection
                .ResolveAsync(instance, versionResult.Value)
                .ConfigureAwait(true);
            JavaStatus = JavaSelectionPresentation.Describe(selection, _localizer);
        }
        catch (Exception exception)
        {
            JavaStatus = exception.Message;
        }
    }

    /// <summary>
    /// 启动指定实例（dryRun 只生成命令预览并校验），进度与结果写入调用方自己的状态。
    /// 实例工作区传入自己的实例与状态，因此两个工作区同时启动时结果不会互相覆盖。
    /// </summary>
    internal async Task LaunchForAsync(InstanceItemViewModel? item, InstanceOperationState state, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (item is null)
        {
            StatusMessage = _localizer.Get("instances.selectFirst");
            return;
        }

        if (IsBusy)
        {
            return;
        }

        if (!dryRun && !EnsureEulaConsent())
        {
            return;
        }

        if (!dryRun && !await ConfirmJavaBeforeLaunchAsync(item.Model).ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        state.LaunchSteps.Clear();
        state.LaunchCommandPreview = string.Empty;
        state.LaunchStatus = _localizer.Get("instances.launching");

        try
        {
            var report = await _launchService.LaunchAsync(item.Model.Id, dryRun).ConfigureAwait(true);
            foreach (var step in report.Steps)
            {
                state.LaunchSteps.Add(new LaunchStepItemViewModel(step, _localizer));
            }

            state.LaunchCommandPreview = SecretRedaction.CommandLine(report.CommandLine ?? string.Empty);
            state.LaunchStatus = report.Success
                ? _localizer.Get(dryRun ? "instances.dryRunSucceeded" : "instances.launchSucceeded")
                : _localizer.Get("instances.launchFailed");
        }
        catch (Exception exception)
        {
            state.LaunchStatus = _localizer.Get("instances.launchException", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 重新读取单个实例并原地替换列表项（B2 步骤 3b-4b）：受控变更成功后实例的版本/加载器
    /// 已变化，工作区必须读到新记录；只替换目标实例，不清空其它实例卡片或当前选择。
    /// </summary>
    internal async Task<InstanceItemViewModel?> ReloadInstanceAsync(string instanceId)
    {
        var instance = await _instances.GetAsync(instanceId).ConfigureAwait(true);
        if (instance is null)
        {
            return null;
        }

        var item = new InstanceItemViewModel(instance, _localizer, _paths);
        var index = -1;
        for (var i = 0; i < Instances.Count; i++)
        {
            if (string.Equals(Instances[i].Model.Id, instanceId, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            Instances[index] = item;
        }
        else
        {
            Instances.Add(item);
        }

        if (SelectedInstance is { } selected &&
            string.Equals(selected.Model.Id, instanceId, StringComparison.Ordinal))
        {
            SelectedInstance = item;
        }

        return item;
    }

    private async Task RefreshInstancesAsync()
    {
        IsLoadingInstances = true;
        InstanceLoadError = string.Empty;
        try
        {
            var selectedId = SelectedInstance?.Model.Id;
            var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);

            Instances.Clear();
            foreach (var instance in instances)
            {
                Instances.Add(new InstanceItemViewModel(instance, _localizer, _paths));
            }

            SelectedInstance = Instances.FirstOrDefault(item => item.Model.Id == selectedId) ?? Instances.FirstOrDefault();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 读取失败时保留已有卡片：不得因为一次读取失败就清空用户看到的实例记录。
            InstanceLoadError = _localizer.Get("instances.loadFailed", exception.Message);
        }
        finally
        {
            IsLoadingInstances = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(InstanceCountText));
            OnPropertyChanged(nameof(ShowLoadingState));
            OnPropertyChanged(nameof(ShowErrorState));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(HasInstanceLoadError));
        }
    }

    private async Task RefreshAuthServersAsync()
    {
        var servers = await _authServers.GetServersAsync().ConfigureAwait(true);

        AuthServers.Clear();
        foreach (var server in servers)
        {
            AuthServers.Add(new AuthServerItemViewModel(server));
        }
    }

    private async Task RefreshAccountsAsync()
    {
        _suppressAccountSelection = true;
        try
        {
            var accounts = await _accounts.GetAccountsAsync().ConfigureAwait(true);
            var defaultAccount = await _accounts.GetDefaultAccountAsync().ConfigureAwait(true);
            var serverNames = AuthServers.ToDictionary(
                server => server.Id,
                server => server.DisplayName,
                StringComparer.Ordinal);

            Accounts.Clear();
            foreach (var account in accounts)
            {
                var serverName = account.AuthServerId is { Length: > 0 } id && serverNames.TryGetValue(id, out var name)
                    ? name
                    : null;
                Accounts.Add(new AccountItemViewModel(account, account.Id == defaultAccount?.Id, _localizer, serverName));
            }

            SelectedAccount = Accounts.FirstOrDefault(item => item.IsDefault) ?? Accounts.FirstOrDefault();
        }
        finally
        {
            _suppressAccountSelection = false;
        }
    }

    private async Task SetDefaultAccountAsync(string accountId)
    {
        try
        {
            var result = await _accounts.SetDefaultAsync(accountId).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.Message ?? string.Empty;
                return;
            }

            foreach (var account in Accounts)
            {
                account.IsDefault = account.Id == accountId;
            }
        }
        catch (Exception exception)
        {
            StatusMessage = _localizer.Get("instances.launchException", exception.Message);
        }
    }
}

public sealed record InstanceSourceItemViewModel(string Name, string GameVersion, string Loader, bool CanLaunch)
{
    public string Meta => $"{GameVersion} · {Loader}";

    /// <summary>实例是否由 ECL 托管；文案已本地化，由调用方在构造前决定语言。</summary>
    public string Availability { get; init; } = string.Empty;
}

public sealed partial class InstanceSourceGroupViewModel : ObservableObject
{
    public InstanceSourceGroupViewModel(
        string name,
        string path,
        bool isAccessible,
        IReadOnlyList<InstanceSourceItemViewModel> instances,
        ILocalizer localizer)
    {
        Name = name;
        Path = path;
        Status = isAccessible
            ? localizer.Get("instances.source.accessible")
            : localizer.Get("instances.source.inaccessible");
        Instances = instances;
        CountText = localizer.Get("instances.source.count", instances.Count);
    }

    public string Name { get; }

    public string Path { get; }

    public string Status { get; }

    public IReadOnlyList<InstanceSourceItemViewModel> Instances { get; }

    public string CountText { get; }

    /// <summary>折叠只影响展示，不改变实例选择、扫描状态或清理目标。</summary>
    [ObservableProperty]
    private bool _isExpanded = true;
}

public sealed class InstanceItemViewModel
{
    public InstanceItemViewModel(GameInstance model, ILocalizer localizer, IAppPaths paths)
    {
        Model = model;
        Name = model.Name;
        MinecraftVersionText = localizer.Get("instances.minecraftVersion", model.GameVersion);
        LoaderText = FormatLoader(model, localizer);
        VersionText = $"{MinecraftVersionText} · {LoaderText}";
        LastPlayedText = model.LastPlayedAt is null
            ? localizer.Get("instances.neverPlayed")
            : localizer.Get("instances.lastPlayedLabel", model.LastPlayedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        CreatedText = localizer.Get("instances.createdAt", model.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd"));
        PathText = InstancePaths.GetGameDirectory(paths, model);
        LoaderKind = model.Loader.ToLowerInvariant() switch
        {
            "fabric" => "fabric",
            "forge" => "forge",
            "neoforge" => "neoforge",
            "cleanroom" => "cleanroom",
            _ => "unknown",
        };
    }

    public GameInstance Model { get; }

    public string Name { get; }

    /// <summary>明确的 Minecraft 版本标记，例如“Minecraft 1.21.1”。</summary>
    public string MinecraftVersionText { get; }

    /// <summary>加载器名称与版本，例如“Fabric 0.16.10”；原版显示为“原版”。</summary>
    public string LoaderText { get; }

    /// <summary>加载器类型键：fabric / forge / neoforge / cleanroom / unknown（用于颜色标识）。</summary>
    public string LoaderKind { get; }

    public string VersionText { get; }

    public string LastPlayedText { get; }

    /// <summary>创建日期；按实例页规划作为卡片的独立信息行。</summary>
    public string CreatedText { get; }

    public string PathText { get; }

    private static string FormatLoader(GameInstance model, ILocalizer localizer)
    {
        var loaderName = model.Loader.ToLowerInvariant() switch
        {
            "fabric" => localizer.Get("instances.loader.fabric"),
            "forge" => localizer.Get("instances.loader.forge"),
            "neoforge" => localizer.Get("instances.loader.neoforge"),
            "cleanroom" => localizer.Get("instances.loader.cleanroom"),
            "vanilla" => localizer.Get("instances.loader.vanilla"),
            _ => model.Loader,
        };

        return string.IsNullOrWhiteSpace(model.LoaderVersion)
            ? loaderName
            : $"{loaderName} {model.LoaderVersion}";
    }
}

public sealed partial class AccountItemViewModel : ObservableObject
{
    public AccountItemViewModel(Account model, bool isDefault, ILocalizer localizer, string? authServerName = null)
    {
        Model = model;
        Id = model.Id;
        IsDefault = isDefault;
        var typeLabel = localizer.Get(model.Type switch
        {
            AccountType.Microsoft => "accounts.microsoft",
            AccountType.ThirdParty => "accounts.thirdParty",
            _ => "accounts.offline",
        });
        DisplayName = model.Type == AccountType.ThirdParty && !string.IsNullOrWhiteSpace(authServerName)
            ? $"{model.Name} · {typeLabel}（{authServerName}）"
            : $"{model.Name} · {typeLabel}";
    }

    public Account Model { get; }

    public string Id { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isDefault;
}

public sealed class AuthServerItemViewModel
{
    public AuthServerItemViewModel(AuthServer server)
    {
        Id = server.Id;
        var host = Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) ? uri.Host : server.Url;
        DisplayName = $"{server.Name} · {host}";
    }

    public string Id { get; }

    public string DisplayName { get; }
}

public sealed record JavaOptionViewModel(JavaRuntime? Runtime, string DisplayName);

public sealed record JvmPresetOptionViewModel(string Value, string DisplayName);

public sealed record LoaderChoiceViewModel(string Id, string DisplayName);

public sealed record GameVersionTypeChoice(MinecraftVersionKind Kind, string Title);

public sealed record VersionChoiceViewModel(string? Value, string DisplayName);

public sealed class LaunchStepItemViewModel
{
    public LaunchStepItemViewModel(LaunchStepResult step, ILocalizer localizer)
    {
        Status = step.Status;
        Title = localizer.Get(step.TitleKey);
        Message = step.Message;
        StatusText = localizer.Get($"launch.status.{step.Status.ToString().ToLowerInvariant()}");
        DurationText = $"{step.Duration.TotalMilliseconds:F0} ms";
    }

    public LaunchStepStatus Status { get; }

    public string Title { get; }

    public string Message { get; }

    public string StatusText { get; }

    public string DurationText { get; }
}
