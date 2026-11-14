// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Additions;
using Ecl.App.Services;
using Ecl.App.Shell.Output;
using Ecl.App.ViewModels.Home;
using Ecl.Core.Localization;
using Ecl.Minecraft.Launch;

namespace Ecl.App.ViewModels;

/// <summary>
/// 主页（home.md）：启动卡、中央实例切换栏，以及下方账号 / 入口卡片 / Multiplayer 三个等高区域。
///
/// 边界：
/// - 主页不是实例工作区：实例创建、复制、目录导入、模组与内容管理仍属于实例页、下载中心与实例工作区。
/// - 启动卡只呈现一次启动的真实流程与结果；启动状态按实例持久化（<see cref="ILaunchStatusStore"/>）。
/// - 账号区域只管理 ECL 全局账户注册表与当前默认账号，不在实例工作区复制第二套账号列表。
/// - 主页只链接已存在的目标；日志入口携带准确 SessionId（logs.md §7.1）。
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    /// <summary>主页 Multiplayer 区域只服务这一个 Addition（home.md §5.3）。</summary>
    public const string MultiplayerAdditionId = "ecl.multiplayer";

    private readonly IAdditionsService _additions;
    private readonly ILocalizer _localizer;

    public HomeViewModel(
        InstancesViewModel launcher,
        ILaunchService launchService,
        ILaunchStatusStore launchStatuses,
        IHomeLayoutStore layoutStore,
        AppLogBuffer logBuffer,
        IAdditionsService additions,
        ILocalizer localizer,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(dispatcher);

        Launcher = launcher;
        _additions = additions;
        _localizer = localizer;

        Launch = new HomeLaunchCardViewModel(
            new InstancesLaunchHost(launcher),
            launchService,
            launchStatuses,
            logBuffer,
            localizer,
            dispatcher);
        Switcher = new HomeInstanceSwitcherViewModel(
            new InstancesInstanceSource(launcher),
            layoutStore,
            localizer);
        Launch.PropertyChanged += OnLaunchPropertyChanged;
        Launcher.PropertyChanged += OnLauncherPropertyChanged;
        Launcher.Accounts.CollectionChanged += (_, _) => RaiseAccountStateChanged();

        RefreshMultiplayerState();
    }

    /// <summary>共享的实例与账户状态（主页与实例页使用同一份来源，不复制第二套）。</summary>
    public InstancesViewModel Launcher { get; }

    public HomeLaunchCardViewModel Launch { get; }

    public HomeInstanceSwitcherViewModel Switcher { get; }

    public ObservableCollection<AccountItemViewModel> Accounts => Launcher.Accounts;

    /// <summary>由主窗口注入页面导航行为，避免主页依赖主窗口 ViewModel。</summary>
    public Action<string>? Navigate { get; set; }

    /// <summary>打开指定实例的工作区。</summary>
    public Action<string>? OpenInstanceWorkspace { get; set; }

    /// <summary>打开设置中心的扩展管理页（Multiplayer 卡片的管理入口）。</summary>
    public Action? OpenAdditionsManagement { get; set; }

    [ObservableProperty]
    private bool _isAccountMenuOpen;

    /// <summary>
    /// 窄窗口（例如 1080×720 且侧栏与 Inspector 同时展开）下的紧凑布局：
    /// 三个下方区域改为纵向排列并保持等高，实例切换栏改为两列，避免文字被裁切到不可读。
    /// 由视图在尺寸变化时设置；布局偏好不进入持久化状态。
    /// </summary>
    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private string _multiplayerStatus = string.Empty;

    [ObservableProperty]
    private string _multiplayerDetail = string.Empty;

    [ObservableProperty]
    private bool _showMultiplayerManagement;

    public bool HasAccount => Launcher.SelectedAccount is not null;

    public string CurrentAccountName => Launcher.SelectedAccount?.Model.Name ?? _localizer.Get("home.account.none");

    public string CurrentAccountTypeText => Launcher.SelectedAccount?.TypeText ?? string.Empty;

    public bool HasAccounts => Accounts.Count > 0;

    public string AccountMenuHint => _localizer.Get("home.account.menuHint");

    public string AvatarHint => _localizer.Get("home.account.avatarHint");

    public string AvatarPlaceholder => "?";

    public string SwitcherTitle => _localizer.Get("home.switcher.title");

    public string OpenWorkspaceText => _localizer.Get("home.entry.workspace");

    public string OpenWorkspaceDescription => _localizer.Get("home.entry.workspaceDesc");

    public string OpenDownloadsText => _localizer.Get("home.entry.downloads");

    public string OpenDownloadsDescription => _localizer.Get("home.entry.downloadsDesc");

    public string MultiplayerTitle => _localizer.Get("home.multiplayer.title");

    public string MultiplayerSubtitle => _localizer.Get("home.multiplayer.subtitle");

    public string MultiplayerDescription => _localizer.Get("home.multiplayer.description");

    public string MultiplayerManageText => _localizer.Get("home.multiplayer.manage");

    public string AccountMenuText => _localizer.Get(IsAccountMenuOpen ? "home.account.close" : "home.account.open");

    public async Task InitializeAsync()
    {
        // 先建立排列骨架：实例列表随后由 Shell 初始化，CollectionChanged 会把它对齐进来。
        Switcher.Load();
        Launch.Bind(Launcher.SelectedInstance);
        RefreshMultiplayerState();
        RaiseAccountStateChanged();
        await Task.CompletedTask;
    }

    /// <summary>重新进入主页时刷新（启动可能由实例页或 CLI 发起）。</summary>
    public void Activate()
    {
        Switcher.Load();
        Launch.Activate();
        RefreshMultiplayerState();
        RaiseAccountStateChanged();
    }

    [RelayCommand]
    private void ToggleAccountMenu() => IsAccountMenuOpen = !IsAccountMenuOpen;

    /// <summary>菜单可能由账号状态变化自动关闭，按钮文案必须跟随状态而不是只在点击时更新。</summary>
    partial void OnIsAccountMenuOpenChanged(bool value) => OnPropertyChanged(nameof(AccountMenuText));

    [RelayCommand]
    private void OpenWorkspace()
    {
        if (Launcher.SelectedInstance is { } instance)
        {
            OpenInstanceWorkspace?.Invoke(instance.Model.Id);
            return;
        }

        Navigate?.Invoke("instances");
    }

    [RelayCommand]
    private void OpenDownloads() => Navigate?.Invoke("download");

    [RelayCommand]
    private void OpenInstances() => Navigate?.Invoke("instances");

    [RelayCommand]
    private void OpenMultiplayerManagement()
    {
        if (ShowMultiplayerManagement)
        {
            OpenAdditionsManagement?.Invoke();
        }
    }

    private void OnLauncherPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(InstancesViewModel.SelectedInstance):
                Launch.Bind(Launcher.SelectedInstance);
                break;
            case nameof(InstancesViewModel.SelectedAccount):
                RaiseAccountStateChanged();
                if (!HasAccount)
                {
                    IsAccountMenuOpen = false;
                }

                break;
        }
    }

    private void OnLaunchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeLaunchCardViewModel.IsLaunching))
        {
            // 启动进行中禁止在主页切换实例：主页只呈现一个实例的启动状态（home.md §3.4）。
            Switcher.CanSwitch = !Launch.IsLaunching;
        }
    }

    private void RaiseAccountStateChanged()
    {
        OnPropertyChanged(nameof(HasAccount));
        OnPropertyChanged(nameof(CurrentAccountName));
        OnPropertyChanged(nameof(CurrentAccountTypeText));
        OnPropertyChanged(nameof(HasAccounts));
    }

    /// <summary>
    /// Multiplayer 卡片状态（home.md §5.3）。只陈述能核实的事实：
    /// 当前没有可验证的正式发布资产，因此不提供安装操作；已安装的本地版本按集成状态如实呈现，
    /// 但 Addition Runtime 还没有可打开的功能入口，所以不伪造目标页面。
    /// </summary>
    private void RefreshMultiplayerState()
    {
        InstalledAddition? installed = null;
        try
        {
            var load = _additions.LoadState();
            installed = load.Document.Additions.FirstOrDefault(addition =>
                string.Equals(addition.Id.Value, MultiplayerAdditionId, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 扩展状态读取失败不能变成虚假的“未安装”或“已安装”。
            MultiplayerStatus = _localizer.Get("home.multiplayer.stateUnknown");
            MultiplayerDetail = exception.Message;
            ShowMultiplayerManagement = false;
            return;
        }

        if (installed is null)
        {
            MultiplayerStatus = _localizer.Get("home.multiplayer.notReleased");
            MultiplayerDetail = _localizer.Get("home.multiplayer.notReleasedHint");
            ShowMultiplayerManagement = false;
            return;
        }

        ShowMultiplayerManagement = true;
        if (!installed.Enabled)
        {
            MultiplayerStatus = _localizer.Get("home.multiplayer.disabled", installed.Version.ToString());
            MultiplayerDetail = _localizer.Get("home.multiplayer.disabledHint");
            return;
        }

        var apiVersion = AdditionCoreApi.Version;
        var minimum = installed.CoreApiMin;
        var maximum = installed.CoreApiMax;
        var compatible = (minimum is null || apiVersion.CompareTo(minimum) >= 0)
                         && (maximum is null || apiVersion.CompareTo(maximum) <= 0);
        if (!compatible)
        {
            MultiplayerStatus = _localizer.Get("home.multiplayer.incompatible", installed.Version.ToString());
            MultiplayerDetail = _localizer.Get(
                "home.multiplayer.incompatibleHint",
                $"{minimum?.ToString() ?? "-"} – {maximum?.ToString() ?? "-"}",
                apiVersion.ToString());
            return;
        }

        MultiplayerStatus = _localizer.Get("home.multiplayer.installed", installed.Version.ToString());
        MultiplayerDetail = _localizer.Get("home.multiplayer.noEntry");
    }
}
