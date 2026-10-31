// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ecl.App.ViewModels;

/// <summary>
/// 主页是启动与日常操作工作台，不承载 Minecraft 版本发布资讯。
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    public InstancesViewModel Launcher { get; }

    /// <summary>由主窗口注入页面导航行为，避免主页依赖主窗口 ViewModel。</summary>
    public Action<string>? Navigate { get; set; }

    /// <summary>打开当前选中实例的工作区（模组与内容在其中管理）。</summary>
    public Action? OpenInstanceWorkspace { get; set; }

    public HomeViewModel(InstancesViewModel launcher)
    {
        Launcher = launcher;
    }

    [RelayCommand]
    private void OpenDownloads() => Navigate?.Invoke("download");

    [RelayCommand]
    private void OpenInstances() => Navigate?.Invoke("instances");

    [RelayCommand]
    private void OpenWorkspace() => OpenInstanceWorkspace?.Invoke();
}
