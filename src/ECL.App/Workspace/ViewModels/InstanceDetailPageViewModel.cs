// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.ViewModels;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 实例工作区页面的数据上下文：持有该工作区自有的身份快照与运行设置（Java 覆盖、内存、JVM），
/// 并引用既有的实例与内容 ViewModel 提供命令与查询，不复制领域业务逻辑。
///
/// B2 步骤 1：页面身份（名称、版本、路径、创建/游玩时间）只读 <see cref="State"/>，
/// 不再读全局 <c>SelectedInstance</c>；B2 步骤 1c 起可编辑字段也由本页面自有对象持有，
/// 两个实例工作区同时打开时互不覆盖。关闭标签时 <see cref="Dispose"/> 释放这些状态。
/// </summary>
public sealed class InstanceDetailPageViewModel : IDisposable
{
    public InstanceDetailPageViewModel(
        InstanceWorkspaceState state,
        InstancesViewModel instances,
        ModsViewModel mods,
        InstanceRuntimeSettingsViewModel runtime)
    {
        State = state;
        Instances = instances;
        Mods = mods;
        Runtime = runtime;
    }

    public InstanceWorkspaceState State { get; }

    public InstancesViewModel Instances { get; }

    public ModsViewModel Mods { get; }

    /// <summary>本工作区自有的 Java/内存/JVM 编辑状态；不与其它实例或主页共享。</summary>
    public InstanceRuntimeSettingsViewModel Runtime { get; }

    public string InstanceId => State.InstanceId;

    public string InstanceName => State.Item.Name;

    public string InstanceMeta => State.Item.VersionText;

    public string InstanceComponents => Instances.GetInstanceComponents(State.Model);

    public string InstancePath => State.Item.PathText;

    public string InstanceCreated => State.Item.CreatedText;

    public string InstanceMinecraftVersion => State.Item.MinecraftVersionText;

    public string InstanceLoaderText => State.Item.LoaderText;

    public string InstanceLastPlayed => State.Item.LastPlayedText;

    /// <summary>加载本页面的实例作用域状态；失败只影响本页文案。</summary>
    public Task InitializeAsync() => Runtime.InitializeAsync();

    public void Dispose()
    {
        Runtime.Dispose();
        State.Dispose();
    }
}
