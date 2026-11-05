// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.ViewModels;
using Ecl.Minecraft.Instances;

namespace Ecl.App.Workspace;

/// <summary>
/// 一个实例工作区自有的状态快照（B2 步骤 1）。
///
/// 每个工作区在打开时捕获它对应的实例条目，页面身份（实例 ID、名称、版本、路径）
/// 只从这里读取，不再依赖全局 <c>SelectedInstance</c>：两个实例工作区同时打开时，
/// 各自的标题与元数据不会互相覆盖。
///
/// 生命周期：由 <see cref="Shell.ShellViewModel"/> 在关闭标签时释放；
/// 释放后不再参与 Inspector、标题或后续页面状态。
/// </summary>
public sealed class InstanceWorkspaceState : IDisposable
{
    public InstanceWorkspaceState(InstanceItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
    }

    /// <summary>打开工作区时的实例快照；不随列表刷新被替换，避免标签身份漂移。</summary>
    public InstanceItemViewModel Item { get; }

    public GameInstance Model => Item.Model;

    public string InstanceId => Item.Model.Id;

    public string Title => Item.Name;

    public bool IsDisposed { get; private set; }

    /// <summary>释放时触发，供页面释放订阅与定时器。</summary>
    public event EventHandler? Disposed;

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Disposed?.Invoke(this, EventArgs.Empty);
    }
}
