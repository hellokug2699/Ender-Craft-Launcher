// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.App.Shell.Inspector;
using Ecl.App.Shell.Output;
using Ecl.App.ViewModels;
using Ecl.App.Workspace;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Core.Workspace;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Storage;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Shell;

/// <summary>
/// Application Shell 的 ViewModel：全局导航、工作区标签、Inspector、Output/Task 面板与弹窗层的组合。
/// 工作区身份与生命周期由 <see cref="IWorkspaceHost"/> 持有；这里只保存页面 ViewModel 与呈现状态。
/// 宿主状态变化通过 <see cref="IWorkspaceChangeSource"/> 推送，不轮询。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceHost _workspaces;
    private readonly IDialogEngine _dialogs;
    private readonly ILocalizer _localizer;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly HomeViewModel _home;
    private readonly DownloadViewModel _download;
    private readonly InstancesViewModel _instances;
    private readonly ModsPageFactory _modsFactory;
    private readonly SettingsViewModel _settings;
    private readonly ToolsViewModel _tools;
    private readonly StorageViewModel _storage;
    private readonly StorageAnalysisService _storageAnalysis;
    private readonly IMemoryPressureMonitor _memoryPressure;
    private readonly InstanceModifyPageFactory _modifyFactory;
    private readonly ContentPackPageFactory _contentPackFactory;
    private readonly ScreenshotPageFactory _screenshotFactory;
    private readonly SchematicPageFactory _schematicFactory;
    private readonly ExportPageFactory _exportFactory;
    private readonly IReadOnlyList<string> _navigationKeys;
    private readonly IDisposable _workspaceChanges;
    private bool _suppressNavigation;

    public ShellViewModel(
        HomeViewModel home,
        DownloadViewModel download,
        InstancesViewModel instances,
        StorageViewModel storage,
        StorageAnalysisService storageAnalysis,
        ModsPageFactory modsFactory,
        InstanceModifyPageFactory modifyFactory,
        ContentPackPageFactory contentPackFactory,
        ScreenshotPageFactory screenshotFactory,
        SchematicPageFactory schematicFactory,
        ExportPageFactory exportFactory,
        SettingsViewModel settings,
        JavaManagementViewModel javaManagement,
        LogSessionsViewModel logSessions,
        ToolsViewModel tools,
        AboutViewModel about,
        InspectorViewModel inspector,
        OutputPanelViewModel output,
        DialogLayerViewModel dialogLayer,
        IMemoryPressureMonitor memoryPressure,
        IWorkspaceHost workspaces,
        IDialogEngine dialogs,
        IAppPaths paths,
        ILocalizer localizer,
        ILogger<ShellViewModel> logger)
    {
        _home = home;
        _download = download;
        _instances = instances;
        _modsFactory = modsFactory;
        _modifyFactory = modifyFactory;
        _contentPackFactory = contentPackFactory;
        _screenshotFactory = screenshotFactory;
        _schematicFactory = schematicFactory;
        _exportFactory = exportFactory;
        _settings = settings;
        _tools = tools;
        _storage = storage;
        _storageAnalysis = storageAnalysis;
        _memoryPressure = memoryPressure;
        _localizer = localizer;
        _logger = logger;
        _workspaces = workspaces;
        _dialogs = dialogs;

        Inspector = inspector;
        Output = output;
        Dialogs = dialogLayer;

        var primaryGroup = localizer.Get("nav.group.primary");
        var toolGroup = localizer.Get("nav.group.tools");
        NavigationItems =
        [
            new ShellNavigationItem("home", "home", localizer.Get("nav.home"), primaryGroup, home),
            new ShellNavigationItem("instances", "instances", localizer.Get("nav.instances"), primaryGroup, instances),
            new ShellNavigationItem("download", "download", localizer.Get("nav.download"), primaryGroup, download),
            new ShellNavigationItem("settings", "settings", localizer.Get("nav.settings"), primaryGroup, settings),
            new ShellNavigationItem("tools", "tools", localizer.Get("nav.tools"), toolGroup, tools),
            new ShellNavigationItem("storage", "storage", localizer.Get("nav.storage"), toolGroup, storage),
            new ShellNavigationItem("java", "java", localizer.Get("nav.java"), toolGroup, javaManagement),
            new ShellNavigationItem("logs", "logs", localizer.Get("nav.logs"), toolGroup, logSessions),
            new ShellNavigationItem("about", "about", localizer.Get("nav.about"), toolGroup, about),
        ];

        NavigationView = CollectionViewSource.GetDefaultView(NavigationItems);
        NavigationView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ShellNavigationItem.Group)));

        _navigationKeys = NavigationItems.Select(item => item.Key).ToArray();

        Inspector.CloseWorkspaceCommand = CloseActiveWorkspaceCommand;

        _home.Navigate = key => ActivateDestination(key);
        _home.OpenInstanceWorkspace = OpenSelectedInstanceWorkspace;
        instances.GetModHealthSummary = modsFactory.DescribeHealthAsync;
        modsFactory.OpenModBrowser = OpenModBrowser;
        contentPackFactory.OpenPackBrowser = OpenPackBrowser;
        instances.OpenModpackDeployment = OpenModpackDeploymentAsync;
        download.InstanceVersion.InstanceCreated = async instanceId =>
        {
            // 新建实例成功后立即出现在实例列表（Home 启动卡与实例页共用同一集合）。
            await _instances.ReloadInstanceAsync(instanceId).ConfigureAwait(true);
        };
        _exportFactory.OpenFolder = OpenExportFolder;
        instances.NavigateToDetails = instanceId => OpenInstanceWorkspace(instanceId);
        instances.NavigateBackFromDetails = NavigateToInstances;

        VersionText = $"ECL {BuildInfo.Version}";
        BuildTimeText = string.IsNullOrWhiteSpace(BuildInfo.BuildTime)
            ? string.Empty
            : localizer.Get("app.buildTime", BuildInfo.BuildTime);
        DataDirectory = paths.Root;
        GameDirectory = paths.GameRoot;

        // ECL_START_PAGE 同时接受导航 Key、ToolId 与旧别名；无法解析时回退首个目的地并记录日志。
        var rawStartPage = Environment.GetEnvironmentVariable("ECL_START_PAGE");
        var startPage = NavigationAliases.ResolveStartPage(rawStartPage, _navigationKeys, out var usedFallback);
        if (usedFallback)
        {
            _logger.LogWarning(
                "ECL_START_PAGE 无法解析，已回退到 {Fallback}：{RawValue}",
                startPage,
                rawStartPage);
        }

        _selectedNavigationItem = NavigationItems.First(item =>
            string.Equals(item.Key, startPage, StringComparison.Ordinal));
        _workspaceChanges = workspaces.Subscribe(OnWorkspaceChanged);
        _instances.PropertyChanged += OnInstancesPropertyChanged;
        OpenDestination(_selectedNavigationItem);
    }

    public ObservableCollection<ShellNavigationItem> NavigationItems { get; }

    public ICollectionView NavigationView { get; }

    public ObservableCollection<WorkspaceTabViewModel> Workspaces { get; } = [];

    public InspectorViewModel Inspector { get; }

    public OutputPanelViewModel Output { get; }

    /// <summary>弹窗层状态；主窗口用它控制遮罩与背景输入锁。</summary>
    public DialogLayerViewModel Dialogs { get; }

    [ObservableProperty]
    private ShellNavigationItem? _selectedNavigationItem;

    [ObservableProperty]
    private WorkspaceTabViewModel? _activeWorkspace;

    [ObservableProperty]
    private bool _isNavigationOpen = true;

    [ObservableProperty]
    private bool _isInspectorOpen = true;

    public bool HasWorkspaces => Workspaces.Count > 0;

    public bool HasActiveWorkspace => ActiveWorkspace is not null;

    public string HeaderTitle => ActiveWorkspace?.Title ?? _localizer.Get("shell.workspace.none");

    public string HeaderSubtitle => ActiveWorkspace is not { } workspace
        ? _localizer.Get("shell.workspace.noneHint")
        : string.Equals(workspace.Title, workspace.ToolDisplayName, StringComparison.Ordinal)
            ? _localizer.Get("shell.workspace.subtitle")
            : workspace.ToolDisplayName;

    public string VersionText { get; }

    public string BuildTimeText { get; }

    public string DataDirectory { get; }

    public string GameDirectory { get; }

    public async Task InitializeAsync()
    {
        await _home.InitializeAsync().ConfigureAwait(true);
        await _home.Launcher.InitializeAsync().ConfigureAwait(true);
        await _download.InitializeAsync().ConfigureAwait(true);
        await _settings.InitializeAsync().ConfigureAwait(true);
        if (SelectedNavigationItem?.Page is ToolsViewModel tools)
        {
            await tools.ActivateAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void ToggleNavigation() => IsNavigationOpen = !IsNavigationOpen;

    [RelayCommand]
    private void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    [RelayCommand]
    private void ToggleOutput() => Output.IsOpen = !Output.IsOpen;

    [RelayCommand]
    private void NextWorkspace() => CycleWorkspace(1);

    [RelayCommand]
    private void PreviousWorkspace() => CycleWorkspace(-1);

    [RelayCommand]
    private Task CloseActiveWorkspaceAsync() => CloseWorkspaceAsync(ActiveWorkspace);

    [RelayCommand]
    private async Task CloseWorkspaceAsync(WorkspaceTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        var decision = _workspaces.RequestClose(tab.Id);
        if (!decision.CanClose)
        {
            return;
        }

        var confirmed = false;
        if (decision.RequiresConfirmation)
        {
            // 没有弹窗宿主时不允许静默丢弃未保存状态；保持工作区打开更安全。
            if (!_dialogs.IsAvailable)
            {
                return;
            }

            var result = await _dialogs.ShowAsync(new DialogRequest
            {
                Title = _localizer.Get("workspace.close.title"),
                Content = new MessageDialogViewModel(
                    _localizer.Get("workspace.close.message", tab.Title),
                    _localizer.Get("workspace.close.discard"),
                    cancelText: _localizer.Get("dialog.cancel"),
                    severity: DialogSeverity.Warning,
                    isDestructive: true),
                InitialFocus = DialogInitialFocus.Cancel,
                Severity = DialogSeverity.Warning,
            }).ConfigureAwait(true);

            if (!result.IsPrimary)
            {
                return;
            }

            confirmed = true;
        }

        if (!_workspaces.Close(tab.Id, confirmed).Closed)
        {
            return;
        }

        Workspaces.Remove(tab);
        OnPropertyChanged(nameof(HasWorkspaces));

        // 关闭即释放该工作区拥有的页面状态与订阅（ADR 0055 §4、迁移规划 §8.3）。
        if (tab.Page is IDisposable disposablePage)
        {
            disposablePage.Dispose();
        }

        // 宿主已经按标签顺序选出新的活动工作区；UI 只跟随它。
        ActiveWorkspace = Workspaces.FirstOrDefault(candidate => candidate.Id == _workspaces.Active?.Id);
    }

    private void CycleWorkspace(int delta)
    {
        if (Workspaces.Count == 0)
        {
            return;
        }

        var current = ActiveWorkspace is null ? -1 : Workspaces.IndexOf(ActiveWorkspace);
        var next = ((current + delta) % Workspaces.Count + Workspaces.Count) % Workspaces.Count;
        ActiveWorkspace = Workspaces[next];
    }

    /// <summary>
    /// 统一目的地入口（迁移规划 §5.8）：解析导航 Key / ToolId / 旧别名，选中导航项，
    /// 由其工作区承载内容；重复进入同一目的地只激活已有标签。
    /// </summary>
    public bool ActivateDestination(string key)
    {
        if (!NavigationAliases.TryResolve(key, _navigationKeys, out var resolved))
        {
            _logger.LogWarning("无法激活未知导航目的地：{Key}", key);
            return false;
        }

        var destination = NavigationItems.First(item =>
            string.Equals(item.Key, resolved, StringComparison.Ordinal));
        SelectedNavigationItem = destination;
        return true;
    }

    /// <summary>只同步导航高亮，不触发打开/切换（活动工作区已经决定内容）。</summary>
    private void SyncNavigationSelection(string? key)
    {
        _suppressNavigation = true;
        SelectedNavigationItem = key is null
            ? null
            : NavigationItems.FirstOrDefault(item => item.Key == key);
        _suppressNavigation = false;
    }

    private void NavigateToInstances() => ActivateDestination("instances");

    private void OpenSelectedInstanceWorkspace()
    {
        if (_instances.SelectedInstance is { } instance)
        {
            OpenInstanceWorkspace(instance.Model.Id);
            return;
        }

        ActivateDestination("instances");
    }

    private void OpenModBrowser(string instanceId, string? query) =>
        _ = OpenModBrowserAsync(instanceId, query);

    /// <summary>
    /// 资源包/光影包页面的「下载 xxx」（resource-library-template.md §2）：
    /// 切到下载中心对应页签，并带入稳定实例 ID 作为已有实例目标。
    /// </summary>
    private void OpenPackBrowser(string instanceId, ContentPackKind kind) =>
        _ = OpenPackBrowserAsync(instanceId, kind);

    /// <summary>在系统文件管理器中打开导出所在目录；失败只记录日志，页面仍保留路径。</summary>
    private void OpenExportFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(exception, "无法打开导出目录：{Path}", path);
        }
    }

    private async Task OpenPackBrowserAsync(string instanceId, ContentPackKind kind)
    {
        // 页面使用资源包领域类型，下载中心使用内容类型；这里做唯一一次映射。
        var contentKind = kind == ContentPackKind.ShaderPack
            ? Ecl.Minecraft.Content.ContentKind.ShaderPack
            : Ecl.Minecraft.Content.ContentKind.ResourcePack;
        await _download
            .OpenContentBrowserAsync(CreateSearchContext(contentKind, instanceId, query: null))
            .ConfigureAwait(true);
        ActivateDestination("download");
    }

    /// <summary>
    /// 本地整合包解析成功后进入统一部署流程（instances.md §5.4）：
    /// 先激活下载中心，再按归档路径进入实例版本页的整合包模式。
    /// </summary>
    private async Task OpenModpackDeploymentAsync(string archivePath)
    {
        ActivateDestination("download");
        await _download.OpenModpackDeploymentAsync(archivePath).ConfigureAwait(true);
    }

    /// <summary>
    /// 依赖搜索上下文（B4b）：目标实例使用稳定 InstanceId，版本与加载器取自已加载的实例记录。
    /// 实例记录缺失时仍传入 InstanceId，下载中心会重新读取实例并明确报告目标失效。
    /// </summary>
    private ContentSearchContext CreateSearchContext(
        Ecl.Minecraft.Content.ContentKind kind,
        string instanceId,
        string? query)
    {
        var instance = _instances.Instances.FirstOrDefault(item =>
            string.Equals(item.Model.Id, instanceId, StringComparison.Ordinal));
        return ContentSearchContext.ForExistingInstance(
            kind,
            instanceId,
            instance?.Model.GameVersion,
            instance?.Model.Loader,
            query);
    }

    /// <summary>
    /// 全局拖拽入口（instances.md §5.2）：只接收 .zip / .mrpack，并走与主动选择相同的解析流程。
    /// 其它文件类型不触发任何导入，也不改变当前页面。
    /// </summary>
    public async Task HandleDroppedFilesAsync(IReadOnlyList<string> files)
    {
        var archive = files.FirstOrDefault(file =>
        {
            var extension = System.IO.Path.GetExtension(file);
            return string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(extension, ".mrpack", StringComparison.OrdinalIgnoreCase);
        });

        if (archive is null)
        {
            _logger.LogInformation("拖入的文件不是受支持的整合包，已忽略：{Count} 个文件。", files.Count);
            return;
        }

        await _instances.ImportModpackFileAsync(archive).ConfigureAwait(true);
    }

    private async Task OpenModBrowserAsync(string instanceId, string? query)
    {
        await _download
            .OpenContentBrowserAsync(CreateSearchContext(
                Ecl.Minecraft.Content.ContentKind.Mod,
                instanceId,
                query))
            .ConfigureAwait(true);
        ActivateDestination("download");
    }

    /// <summary>
    /// 实例工作区入口（迁移规划 §5.8）：组合 <c>instance</c> + 规范化实例身份 <c>instance:v1:&lt;id&gt;</c>。
    /// 裸实例 ID 与旧 <c>instance:&lt;id&gt;</c> 写法在解析时一次性规范化，等价身份不会打开两个标签。
    /// 页面状态的实例作用域隔离在 B2 步骤 1 完成。
    /// </summary>
    public bool OpenInstanceWorkspace(string instanceId)
    {
        if (!WorkspaceTargetSerializer.TryParse(instanceId, out var target)
            || target is not { Kind: WorkspaceTargetKind.Instance, StableInstanceId: { } stableInstanceId })
        {
            _logger.LogWarning("实例 ID 无法作为工作区目标解析：{InstanceId}", instanceId);
            return false;
        }

        // 实例工作区只按稳定 ID 打开；页面状态由工作区自有对象持有，
        // 不再把该实例写成全局“当前选中实例”（列表页与主页的选择同步由它们自己维护）。
        var instance = _instances.Instances.FirstOrDefault(item =>
            string.Equals(item.Model.Id, stableInstanceId, StringComparison.Ordinal));

        // 标签标题来自该实例的快照，不依赖全局 SelectedInstance 的更新顺序。
        var title = instance?.Name is { Length: > 0 } name ? name : stableInstanceId;
        return OpenWorkspace("instance", WorkspaceTargetSerializer.Serialize(target), title);
    }

    /// <summary>
    /// 通用工作区入口（迁移规划 §5.8）：去重键为 <c>(ToolId, 规范化 TargetResource)</c>；
    /// 语法错误、未知 ToolId 或没有页面工厂时返回 <c>false</c> 并记录原因，不创建空壳工作区。
    /// </summary>
    public bool OpenWorkspace(string toolId, string? targetResource, string title)
    {
        if (!WorkspaceToolId.TryNormalize(toolId, out var normalizedToolId))
        {
            _logger.LogWarning("ToolId 不合法，无法打开工作区：{ToolId}", toolId);
            return false;
        }

        var parsed = WorkspaceTargetSerializer.Parse(targetResource);
        if (!parsed.Success)
        {
            _logger.LogWarning(
                "工作区目标无法解析（{Error}），不打开工作区：{ToolId} / {TargetResource}",
                parsed.Error,
                normalizedToolId,
                targetResource);
            return false;
        }

        var key = WorkspaceDedupKey.Create(normalizedToolId, parsed.Target);
        if (FindWorkspace(key) is { } existing)
        {
            ActiveWorkspace = existing;
            return true;
        }

        if (!TryResolvePage(normalizedToolId, parsed.Target!, title, out var page, out var workspaceTitle, out var toolDisplayName))
        {
            _logger.LogWarning("没有可用的页面工厂，不打开工作区：{ToolId}", normalizedToolId);
            return false;
        }

        return OpenWorkspaceCore(key, normalizedToolId, parsed.Target!, workspaceTitle, toolDisplayName, page);
    }

    /// <summary>工作区页面由 Shell 统一解析；页面不得自行创建或缓存工作区。</summary>
    private bool TryResolvePage(
        string toolId,
        WorkspaceTarget target,
        string title,
        out object page,
        out string workspaceTitle,
        out string toolDisplayName)
    {
        page = null!;
        workspaceTitle = string.Empty;
        toolDisplayName = string.Empty;

        if (target.IsDestination)
        {
            if (NavigationItems.FirstOrDefault(item =>
                    string.Equals(item.ToolId, toolId, StringComparison.Ordinal)) is not { } destination)
            {
                return false;
            }

            page = destination.Page;
            toolDisplayName = destination.Title;
            workspaceTitle = string.IsNullOrWhiteSpace(title) ? destination.Title : title;
            return true;
        }

        if (string.Equals(toolId, "instance", StringComparison.Ordinal))
        {
            if (_instances.Instances.FirstOrDefault(item =>
                    string.Equals(item.Model.Id, target.StableInstanceId, StringComparison.Ordinal)) is not { } instance)
            {
                // 实例已不存在或尚未加载：工作区保持不可用，不伪造一个空白实例页。
                return false;
            }

            // 页面状态按实例作用域构造：身份快照与 Java/内存/JVM 编辑状态都由本工作区自有，
            // 两个实例同时打开时不共享可编辑字段（B2 步骤 1c）。
            var detail = new InstanceDetailPageViewModel(
                new InstanceWorkspaceState(instance),
                _instances,
                _modsFactory.Create(instance.Model.Id, instance.Name),
                _instances.CreateRuntimeSettings(instance.Model),
                _storageAnalysis,
                OpenStorageForInstance,
                _memoryPressure,
                _modifyFactory,
                _contentPackFactory,
                _screenshotFactory,
                _schematicFactory,
                _exportFactory,
                _localizer);
            _ = InitializeInstancePageAsync(detail);
            page = detail;
            toolDisplayName = _localizer.Get("nav.instances");
            workspaceTitle = string.IsNullOrWhiteSpace(title) ? target.StableInstanceId! : title;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 工作区页面初始化不是 Shell 就绪条件：显式跟踪并记录失败，避免未观察任务
    /// 在后台静默失败；页面自身的加载/错误状态仍由各页面文案表达。
    /// </summary>
    private async Task InitializeInstancePageAsync(InstanceDetailPageViewModel detail)
    {
        try
        {
            await detail.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "实例工作区初始化失败：{InstanceId}", detail.InstanceId);
        }
    }

    /// <summary>
    /// 从实例基本信息页「管理存储空间」进入存储中心，并定位到当前实例
    /// （`instance-workspace.md` §5.2）。存储页首次进入时才扫描。
    /// </summary>
    private void OpenStorageForInstance(string instanceId)
    {
        _storage.FocusInstance(instanceId);
        ActivateDestination("storage");
        _ = ActivateStorageAsync(_storage);
    }

    private async Task ActivateStorageAsync(StorageViewModel storage)
    {
        try
        {
            await storage.ActivateAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "存储中心激活失败。");
        }
    }

    private void OpenDestination(ShellNavigationItem destination)
    {
        var key = WorkspaceDedupKey.Create(destination.ToolId, WorkspaceTarget.Destination);
        if (FindWorkspace(key) is { } existing)
        {
            ActiveWorkspace = existing;
            ActivateDestinationPage(destination.Page);
            return;
        }

        OpenWorkspaceCore(key, destination.ToolId, WorkspaceTarget.Destination, destination.Title, destination.Title, destination.Page);
    }

    /// <summary>
    /// 进入目的地时的首次激活（迁移规划 C-03）：页面级的惰性加载只在这里触发，
    /// 不改变页面已配置的选择，也不在离开页面时做任何刷新。
    /// </summary>
    private void ActivateDestinationPage(object page)
    {
        if (page is DownloadViewModel download)
        {
            _ = ActivateDownloadAsync(download);
        }
    }

    private async Task ActivateDownloadAsync(DownloadViewModel download)
    {
        try
        {
            await download.ActivateAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "下载中心激活失败。");
        }
    }

    private WorkspaceTabViewModel? FindWorkspace(WorkspaceDedupKey key) =>
        Workspaces.FirstOrDefault(tab =>
            WorkspaceDedupKey.TryCreate(tab.ToolId, tab.TargetResource, out var candidate, out _)
            && candidate == key);

    private bool OpenWorkspaceCore(
        WorkspaceDedupKey key,
        string toolId,
        WorkspaceTarget target,
        string title,
        string toolDisplayName,
        object page)
    {
        if (FindWorkspace(key) is { } existing)
        {
            ActiveWorkspace = existing;
            return true;
        }

        var entry = _workspaces.Open(
            WorkspaceDescriptor.Create(toolId, title, WorkspaceTargetSerializer.Serialize(target)));
        _workspaces.MarkReady(entry.Id);

        var tab = new WorkspaceTabViewModel(entry, page, toolDisplayName);
        Workspaces.Add(tab);
        OnPropertyChanged(nameof(HasWorkspaces));
        ActiveWorkspace = tab;
        if (target == WorkspaceTarget.Destination)
        {
            ActivateDestinationPage(page);
        }

        return true;
    }

    partial void OnSelectedNavigationItemChanged(ShellNavigationItem? value)
    {
        if (_suppressNavigation || value is null)
        {
            return;
        }

        OpenDestination(value);
    }

    partial void OnActiveWorkspaceChanged(WorkspaceTabViewModel? value)
    {
        OnPropertyChanged(nameof(HasActiveWorkspace));
        if (value is null)
        {
            OnPropertyChanged(nameof(HeaderTitle));
            OnPropertyChanged(nameof(HeaderSubtitle));
            Inspector.Clear();

            // 清空导航选中态，否则再次点击同一目的地不会触发选择变化，无法重新打开工作区。
            SyncNavigationSelection(null);
            return;
        }

        value.SyncFromEntry();
        _workspaces.Activate(value.Id);

        // 选择层属于页面上下文：切换工作区必须先清空，避免把上一工作区的选中对象显示到新工作区。
        Inspector.ClearSelection();
        Inspector.Show(value.Entry, value.ToolDisplayName);
        UpdateInspectorSelection();

        // 导航高亮跟随活动工作区；实例工作区没有独立导航项，因此清空高亮。
        SyncNavigationSelection(value.TargetResource is null ? value.ToolId : null);

        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
    }

    /// <summary>
    /// 宿主状态推送：只更新对应标签的脏标记与活动工作区的 Inspector，不复制权威状态。
    /// 通知由宿主在 UI 线程同步发出，因此这里直接修改绑定属性。
    /// </summary>
    private void OnWorkspaceChanged(WorkspaceChangeNotification change)
    {
        if (Workspaces.FirstOrDefault(workspace => workspace.Id == change.WorkspaceId) is not { } tab)
        {
            return;
        }

        switch (change.Kind)
        {
            case WorkspaceChangeKind.DirtyStateChanged:
                tab.IsDirty = change.NewIsDirty;
                break;
            case WorkspaceChangeKind.LifecycleStateChanged
                when ReferenceEquals(tab, ActiveWorkspace) && change.NewState != WorkspaceLifecycleState.Disposed:
                Inspector.Show(tab.Entry, tab.ToolDisplayName);
                break;
        }
    }

    public void Dispose()
    {
        _instances.PropertyChanged -= OnInstancesPropertyChanged;
        _workspaceChanges.Dispose();
    }

    private void OnInstancesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstancesViewModel.SelectedInstance))
        {
            UpdateInspectorSelection();
        }
    }

    /// <summary>
    /// Inspector 选择层由 Shell 统一下发（ADR 0056 §3）：实例列表页只报告选中实例的只读摘要，
    /// 其它工作区切换时清空，避免跨工作区残留。
    /// </summary>
    private void UpdateInspectorSelection()
    {
        if (ActiveWorkspace is { TargetResource: null } workspace
            && string.Equals(workspace.ToolId, "instances", StringComparison.Ordinal)
            && _instances.SelectedInstance is { } instance)
        {
            Inspector.ShowSelection(new InspectorSelectionContext(
                instance.Name,
                instance.VersionText,
                [
                    new InspectorSelectionField(_localizer.Get("inspector.selection.loader"), instance.LoaderText),
                    new InspectorSelectionField(_localizer.Get("inspector.selection.created"), instance.CreatedText),
                    new InspectorSelectionField(_localizer.Get("inspector.selection.lastPlayed"), instance.LastPlayedText),
                ]));
            return;
        }

        Inspector.ClearSelection();
    }
}

