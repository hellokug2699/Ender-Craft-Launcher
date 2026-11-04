// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Shell.Inspector;
using Ecl.App.Shell.Output;
using Ecl.App.ViewModels;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Core.Workspace;

namespace Ecl.App.Shell;

/// <summary>
/// Application Shell 的 ViewModel：全局导航、工作区标签、Inspector、Output/Task 面板与弹窗层的组合。
/// 工作区身份与生命周期由 <see cref="IWorkspaceHost"/> 持有；这里只保存页面 ViewModel 与呈现状态。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IWorkspaceHost _workspaces;
    private readonly IDialogEngine _dialogs;
    private readonly ILocalizer _localizer;
    private readonly HomeViewModel _home;
    private readonly DownloadViewModel _download;
    private readonly InstancesViewModel _instances;
    private readonly ModsViewModel _mods;
    private readonly SettingsViewModel _settings;
    private readonly ToolsViewModel _tools;
    private bool _suppressNavigation;

    public ShellViewModel(
        HomeViewModel home,
        DownloadViewModel download,
        InstancesViewModel instances,
        StorageViewModel storage,
        ModsViewModel mods,
        SettingsViewModel settings,
        JavaManagementViewModel javaManagement,
        LogSessionsViewModel logSessions,
        ToolsViewModel tools,
        AboutViewModel about,
        InspectorViewModel inspector,
        OutputPanelViewModel output,
        DialogLayerViewModel dialogLayer,
        IWorkspaceHost workspaces,
        IDialogEngine dialogs,
        IAppPaths paths,
        ILocalizer localizer)
    {
        _home = home;
        _download = download;
        _instances = instances;
        _mods = mods;
        _settings = settings;
        _tools = tools;
        _localizer = localizer;
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

        Inspector.CloseWorkspaceCommand = CloseActiveWorkspaceCommand;

        _home.Navigate = NavigateTo;
        _home.OpenInstanceWorkspace = OpenSelectedInstanceWorkspace;
        instances.GetModHealthSummary = mods.GetHealthSummaryAsync;
        mods.SearchDependency = OpenDependencySearch;
        instances.NavigateToDetails = OpenInstanceWorkspace;
        instances.NavigateBackFromDetails = NavigateToInstances;

        VersionText = $"ECL {BuildInfo.Version}";
        BuildTimeText = string.IsNullOrWhiteSpace(BuildInfo.BuildTime)
            ? string.Empty
            : localizer.Get("app.buildTime", BuildInfo.BuildTime);
        DataDirectory = paths.Root;
        GameDirectory = paths.GameRoot;

        _selectedNavigationItem = NavigationItems.FirstOrDefault(item =>
                                    string.Equals(item.Key, Environment.GetEnvironmentVariable("ECL_START_PAGE"), StringComparison.OrdinalIgnoreCase))
                                ?? NavigationItems[0];
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
        await _mods.InitializeAsync().ConfigureAwait(true);
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

    /// <summary>导航入口：选中目的地后由其工作区承载内容；重复进入同一目的地只激活已有标签。</summary>
    private void NavigateTo(string key)
    {
        if (NavigationItems.FirstOrDefault(item => item.Key == key) is { } target)
        {
            SelectedNavigationItem = target;
        }
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

    private void NavigateToInstances() => NavigateTo("instances");

    private void OpenSelectedInstanceWorkspace()
    {
        if (_instances.SelectedInstance is { } instance)
        {
            OpenInstanceWorkspace(instance.Model.Id);
            return;
        }

        NavigateTo("instances");
    }

    private void OpenDependencySearch(string dependencyId, string instanceId) =>
        _ = OpenDependencySearchAsync(dependencyId, instanceId);

    private async Task OpenDependencySearchAsync(string dependencyId, string instanceId)
    {
        await _download.SearchDependencyAsync(dependencyId, instanceId).ConfigureAwait(true);
        NavigateTo("download");
    }

    /// <summary>实例工作区按实例 ID 复用标签；重复打开不会创建第二个工作区。</summary>
    private void OpenInstanceWorkspace(string instanceId)
    {
        if (_instances.Instances.FirstOrDefault(item => item.Model.Id == instanceId) is { } instance)
        {
            _instances.SelectedInstance = instance;
        }

        _ = _mods.ActivateAsync();

        var existing = Workspaces.FirstOrDefault(workspace =>
            workspace.ToolId == "instance" && string.Equals(workspace.TargetResource, instanceId, StringComparison.Ordinal));
        if (existing is not null)
        {
            ActiveWorkspace = existing;
            return;
        }

        var page = new InstanceDetailPageViewModel(_instances, _mods);
        var title = string.IsNullOrWhiteSpace(_instances.SelectedInstanceName) ? instanceId : _instances.SelectedInstanceName;
        OpenWorkspace("instance", title, instanceId, page, _localizer.Get("nav.instances"));
    }

    private void OpenDestination(ShellNavigationItem destination)
    {
        var existing = Workspaces.FirstOrDefault(workspace =>
            workspace.ToolId == destination.ToolId && workspace.TargetResource is null);
        if (existing is not null)
        {
            ActiveWorkspace = existing;
            return;
        }

        OpenWorkspace(destination.ToolId, destination.Title, null, destination.Page, destination.Title);
    }

    private void OpenWorkspace(string toolId, string title, string? targetResource, object page, string toolDisplayName)
    {
        var entry = _workspaces.Open(WorkspaceDescriptor.Create(toolId, title, targetResource));
        _workspaces.MarkReady(entry.Id);

        var tab = new WorkspaceTabViewModel(entry, page, toolDisplayName);
        Workspaces.Add(tab);
        OnPropertyChanged(nameof(HasWorkspaces));
        ActiveWorkspace = tab;
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
        Inspector.Show(value.Entry, value.ToolDisplayName);

        // 导航高亮跟随活动工作区；实例工作区没有独立导航项，因此清空高亮。
        SyncNavigationSelection(value.TargetResource is null ? value.ToolId : null);

        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
    }
}
