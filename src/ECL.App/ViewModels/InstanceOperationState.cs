// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ecl.App.ViewModels;

/// <summary>
/// 一次实例级操作（启动、健康检查、修复、崩溃分析与诊断导出）的呈现状态。
///
/// 操作实现只有一份（<see cref="InstancesViewModel"/> 上以显式实例为参数的内部方法），
/// 但状态可以有多份：主页启动面板与每个实例工作区各自持有自己的
/// <see cref="InstanceOperationState"/>，因此两个工作区同时执行操作时，
/// 结果只出现在发起操作的那一个工作区里（B2 步骤 1c）。
/// </summary>
public sealed partial class InstanceOperationState : ObservableObject
{
    public ObservableCollection<LaunchStepItemViewModel> LaunchSteps { get; } = [];

    [ObservableProperty]
    private string _launchStatus = string.Empty;

    [ObservableProperty]
    private string _launchCommandPreview = string.Empty;

    [ObservableProperty]
    private string _healthReportText = string.Empty;

    [ObservableProperty]
    private string _crashAnalysisText = string.Empty;

    [ObservableProperty]
    private string _diagnosticStatus = string.Empty;

    [ObservableProperty]
    private string _installStatus = string.Empty;

    /// <summary>
    /// 切换到另一个实例或开始新的启动前清空上一次的启动报告，
    /// 避免把旧实例的步骤显示成当前实例的报告。
    /// </summary>
    public void ResetLaunchReport()
    {
        LaunchSteps.Clear();
        LaunchStatus = string.Empty;
        LaunchCommandPreview = string.Empty;
        InstallStatus = string.Empty;
    }
}
