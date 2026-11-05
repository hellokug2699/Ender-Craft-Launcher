// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.Core.Localization;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 实例工作区「基本信息」二级页（§10 概览分组、默认页）。
///
/// 页面只组合本工作区自有的身份快照、运行设置与操作状态，并复用既有
/// <see cref="InstancesViewModel"/> 的领域命令；不复制实例业务逻辑。
/// 内存条读取与压力分级相同的系统内存采样，避免两套口径。
/// 生命周期由 <see cref="InstanceDetailPageViewModel"/> 持有。
/// </summary>
public sealed partial class InstanceBasicInfoViewModel : ObservableObject, IDisposable
{
    private readonly IMemoryPressureMonitor _memory;
    private readonly ILocalizer _localizer;

    public InstanceBasicInfoViewModel(
        InstanceWorkspaceState state,
        InstancesViewModel instances,
        InstanceRuntimeSettingsViewModel runtime,
        InstanceOperationsViewModel operations,
        IMemoryPressureMonitor memory,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(localizer);

        State = state;
        Instances = instances;
        Runtime = runtime;
        Operations = operations;
        _memory = memory;
        _localizer = localizer;

        Runtime.PropertyChanged += OnRuntimePropertyChanged;
        RefreshMemoryBar();
    }

    public InstanceWorkspaceState State { get; }

    public InstancesViewModel Instances { get; }

    /// <summary>本工作区自有的 Java/内存/JVM 编辑状态；不与其它实例或主页共享。</summary>
    public InstanceRuntimeSettingsViewModel Runtime { get; }

    /// <summary>本工作区自有的启动/健康检查/诊断/文件操作与结果状态。</summary>
    public InstanceOperationsViewModel Operations { get; }

    public string InstanceId => State.InstanceId;

    public string InstanceName => State.Item.Name;

    public string InstanceMeta => State.Item.VersionText;

    public string InstanceComponents => Instances.GetInstanceComponents(State.Model);

    public string InstancePath => State.Item.PathText;

    public string InstanceCreated => State.Item.CreatedText;

    public string InstanceMinecraftVersion => State.Item.MinecraftVersionText;

    public string InstanceLoaderText => State.Item.LoaderText;

    public string InstanceLastPlayed => State.Item.LastPlayedText;

    /// <summary>系统已用 + 游戏分配 + 剩余的三段式内存条（§4.1）。</summary>
    [ObservableProperty]
    private MemoryBarPresentation _memoryBar = MemoryBarPresentation.Unavailable;

    [ObservableProperty]
    private string _memorySummary = string.Empty;

    [ObservableProperty]
    private string _memoryWarning = string.Empty;

    public bool HasSystemMemory => MemoryBar.HasSystemMemory;

    public bool IsMemoryOverCapacity => MemoryBar.ExceedsCapacity;

    public bool ShowMemoryWarning => MemoryWarning.Length > 0;

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Runtime.PropertyChanged -= OnRuntimePropertyChanged;
    }

    private void OnRuntimePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstanceRuntimeSettingsViewModel.EffectiveMemoryMb)
            or nameof(InstanceRuntimeSettingsViewModel.MemoryMbText)
            or nameof(InstanceRuntimeSettingsViewModel.UseGlobalMemory)
            or nameof(InstanceRuntimeSettingsViewModel.IsMemoryAllocationValid))
        {
            RefreshMemoryBar();
        }
    }

    private void RefreshMemoryBar()
    {
        MemoryBar = MemoryBarPresentationFactory.Create(_memory.ReadSample(), Runtime.EffectiveMemoryMb);

        MemorySummary = MemoryBar.HasSystemMemory
            ? _localizer.Get("instance.memory.summary", MemoryBar.TotalMb, MemoryBar.UsedMb, MemoryBar.GameMb)
            : _localizer.Get("instance.memory.systemUnknown");

        MemoryWarning = !Runtime.IsMemoryAllocationValid
            ? _localizer.Get("instances.memory.invalid")
            : MemoryBar.ExceedsCapacity
                ? _localizer.Get("instance.memory.overflow")
                : string.Empty;

        OnPropertyChanged(nameof(HasSystemMemory));
        OnPropertyChanged(nameof(IsMemoryOverCapacity));
        OnPropertyChanged(nameof(ShowMemoryWarning));
    }
}
