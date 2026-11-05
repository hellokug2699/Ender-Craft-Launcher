// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.ViewModels;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 实例工作区「基本信息」二级页（§10 概览分组、默认页）。
///
/// 页面只组合本工作区自有的身份快照、运行设置与操作状态，并复用既有
/// <see cref="InstancesViewModel"/> 的领域命令；不复制实例业务逻辑。
/// 生命周期由 <see cref="InstanceDetailPageViewModel"/> 持有。
/// </summary>
public sealed class InstanceBasicInfoViewModel
{
    public InstanceBasicInfoViewModel(
        InstanceWorkspaceState state,
        InstancesViewModel instances,
        InstanceRuntimeSettingsViewModel runtime,
        InstanceOperationsViewModel operations)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(operations);

        State = state;
        Instances = instances;
        Runtime = runtime;
        Operations = operations;
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
}
