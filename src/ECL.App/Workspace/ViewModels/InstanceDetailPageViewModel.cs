// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.ViewModels;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 实例工作区页面的数据上下文：持有该工作区自有的身份快照，
/// 并引用既有的实例与内容 ViewModel 提供命令与查询，不复制领域业务逻辑。
///
/// B2 步骤 1：页面身份（名称、版本、路径、创建/游玩时间）只读 <see cref="State"/>，
/// 不再读全局 <c>SelectedInstance</c>；剩存的共享编辑字段在后续子步骤迁移到工作区作用域。
/// </summary>
public sealed class InstanceDetailPageViewModel : IDisposable
{
    public InstanceDetailPageViewModel(
        InstanceWorkspaceState state,
        InstancesViewModel instances,
        ModsViewModel mods)
    {
        State = state;
        Instances = instances;
        Mods = mods;
    }

    public InstanceWorkspaceState State { get; }

    public InstancesViewModel Instances { get; }

    public ModsViewModel Mods { get; }

    public string InstanceId => State.InstanceId;

    public string InstanceName => State.Item.Name;

    public string InstanceMeta => State.Item.VersionText;

    public string InstanceComponents => Instances.GetInstanceComponents(State.Model);

    public string InstancePath => State.Item.PathText;

    public string InstanceCreated => State.Item.CreatedText;

    public string InstanceMinecraftVersion => State.Item.MinecraftVersionText;

    public string InstanceLoaderText => State.Item.LoaderText;

    public string InstanceLastPlayed => State.Item.LastPlayedText;

    public void Dispose() => State.Dispose();
}
