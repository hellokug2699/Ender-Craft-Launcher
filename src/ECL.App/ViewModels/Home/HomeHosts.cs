// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.ComponentModel;
using Ecl.Minecraft.Launch;

namespace Ecl.App.ViewModels.Home;

/// <summary>
/// 主页所需的实例数据来源（只读视图 + 选择写入）。生产实现直接转调共享的
/// <see cref="InstancesViewModel"/>，不复制第二套实例状态；测试可以替换为最小假实现。
/// </summary>
public interface IHomeInstanceSource
{
    IReadOnlyList<InstanceItemViewModel> Instances { get; }

    InstanceItemViewModel? SelectedInstance { get; set; }

    bool HasInstanceLoadError { get; }

    event NotifyCollectionChangedEventHandler? InstancesChanged;

    event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>主页启动动作的入口。唯一实现转调 <see cref="InstancesViewModel.LaunchForAsync"/>。</summary>
public interface IHomeLaunchHost
{
    bool IsBusy { get; }

    event PropertyChangedEventHandler? PropertyChanged;

    Task LaunchAsync(
        InstanceItemViewModel instance,
        InstanceOperationState state,
        bool dryRun,
        IProgress<LaunchStepProgress>? progress);
}

public sealed class InstancesInstanceSource(InstancesViewModel launcher) : IHomeInstanceSource
{
    private readonly InstancesViewModel _launcher = launcher
        ?? throw new ArgumentNullException(nameof(launcher));

    public IReadOnlyList<InstanceItemViewModel> Instances => _launcher.Instances;

    public InstanceItemViewModel? SelectedInstance
    {
        get => _launcher.SelectedInstance;
        set => _launcher.SelectedInstance = value;
    }

    public bool HasInstanceLoadError => _launcher.HasInstanceLoadError;

    public event NotifyCollectionChangedEventHandler? InstancesChanged
    {
        add => _launcher.Instances.CollectionChanged += value;
        remove => _launcher.Instances.CollectionChanged -= value;
    }

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add => _launcher.PropertyChanged += value;
        remove => _launcher.PropertyChanged -= value;
    }
}

public sealed class InstancesLaunchHost(InstancesViewModel launcher) : IHomeLaunchHost
{
    private readonly InstancesViewModel _launcher = launcher
        ?? throw new ArgumentNullException(nameof(launcher));

    public bool IsBusy => _launcher.IsBusy;

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add => _launcher.PropertyChanged += value;
        remove => _launcher.PropertyChanged -= value;
    }

    public Task LaunchAsync(
        InstanceItemViewModel instance,
        InstanceOperationState state,
        bool dryRun,
        IProgress<LaunchStepProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return _launcher.LaunchForAsync(instance, state, dryRun, progress);
    }
}
