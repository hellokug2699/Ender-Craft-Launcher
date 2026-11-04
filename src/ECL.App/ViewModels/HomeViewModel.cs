// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Minecraft.World;

namespace Ecl.App.ViewModels;

/// <summary>
/// 主页是启动与日常操作工作台，不承载 Minecraft 版本发布资讯。
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly WorldWorkbenchService _workbench;

    public InstancesViewModel Launcher { get; }

    public ObservableCollection<RecentItemViewModel> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    public string RecentSummary => HasRecent
        ? "最近查看的存档、查询过的服务器；与游戏启动记录分开。"
        : "还没有最近访问记录。打开存档详情或查询服务器后会出现在这里。";

    /// <summary>由主窗口注入页面导航行为，避免主页依赖主窗口 ViewModel。</summary>
    public Action<string>? Navigate { get; set; }

    /// <summary>打开当前选中实例的工作区（模组与内容在其中管理）。</summary>
    public Action? OpenInstanceWorkspace { get; set; }

    public HomeViewModel(InstancesViewModel launcher, WorldWorkbenchService workbench)
    {
        Launcher = launcher;
        _workbench = workbench;
    }

    public async Task InitializeAsync()
    {
        var entries = await _workbench.RecentAsync();
        Recent.Clear();
        foreach (var entry in entries.Take(5))
        {
            Recent.Add(new RecentItemViewModel(entry));
        }

        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(RecentSummary));
    }

    [RelayCommand]
    private void OpenDownloads() => Navigate?.Invoke("download");

    [RelayCommand]
    private void OpenInstances() => Navigate?.Invoke("instances");

    [RelayCommand]
    private void OpenWorkspace() => OpenInstanceWorkspace?.Invoke();
}
