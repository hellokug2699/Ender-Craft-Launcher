// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.ViewModels;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 一个实例工作区自有的实例级操作（B2 步骤 1c）。
///
/// 启动、健康检查、修复、崩溃分析、诊断/日志导出、打开目录与世界工作台都只作用于
/// 本工作区打开时的实例快照，结果写入本对象自有的 <see cref="Operation"/> 状态；
/// 两个实例工作区同时操作时互不覆盖，也不再依赖 Shell 设置的全局“当前选中实例”。
///
/// 生命周期由 <see cref="InstanceDetailPageViewModel"/> 持有，关闭工作区时释放。
/// </summary>
public sealed partial class InstanceOperationsViewModel : ObservableObject, IDisposable
{
    private readonly InstancesViewModel _instances;
    private readonly InstanceWorkspaceState _state;

    public InstanceOperationsViewModel(InstancesViewModel instances, InstanceWorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(state);
        _instances = instances;
        _state = state;
    }

    /// <summary>本工作区的操作结果状态；主页启动面板使用 <see cref="InstancesViewModel.Operation"/>，两者互不影响。</summary>
    public InstanceOperationState Operation { get; } = new();

    /// <summary>页面上的操作入口只在本工作区仍可用时启用。</summary>
    public bool CanOperate => !IsDisposed;

    public bool IsDisposed { get; private set; }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private Task LaunchAsync() => _instances.LaunchForAsync(_state.Item, Operation, dryRun: false);

    [RelayCommand]
    private Task CheckHealthAsync() => _instances.CheckHealthForAsync(_state.Item, Operation);

    [RelayCommand]
    private Task RepairFilesAsync() => _instances.RepairForAsync(_state.Item, Operation);

    [RelayCommand]
    private Task AnalyzeCrashAsync() => _instances.AnalyzeCrashForAsync(_state.Item, Operation);

    [RelayCommand]
    private Task ExportDiagnosticsAsync() => _instances.ExportDiagnosticsForAsync(_state.Item, Operation);

    [RelayCommand]
    private void ExportLatestLog()
    {
        var message = _instances.ExportLatestLogFor(_state.Item);
        if (message.Length > 0)
        {
            StatusMessage = message;
        }
    }

    [RelayCommand]
    private void OpenInstanceFolder() => _instances.OpenInstanceFolderFor(_state.Item);

    [RelayCommand]
    private void OpenModsFolder() => _instances.OpenModsFolderFor(_state.Item);

    [RelayCommand]
    private void OpenWorldWorkbench() => _instances.OpenWorldWorkbenchFor(_state.Item);

    /// <summary>删除目标仍走列表页同一删除计划与确认流程，但目标是本工作区的实例。</summary>
    [RelayCommand]
    private Task DeleteAsync() => _instances.DeleteInstanceCommand.ExecuteAsync(_state.Item);

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        OnPropertyChanged(nameof(CanOperate));
        Operation.ResetLaunchReport();
        Operation.HealthReportText = string.Empty;
        Operation.CrashAnalysisText = string.Empty;
        Operation.DiagnosticStatus = string.Empty;
    }
}
