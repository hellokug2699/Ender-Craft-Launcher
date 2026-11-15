// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>宿主工作区状态变化的种类。只描述“发生了什么”，不携带 UI 呈现信息。</summary>
public enum WorkspaceChangeKind
{
    /// <summary>生命周期状态迁移，例如 Opening → Ready、Ready → Closing。</summary>
    LifecycleStateChanged,

    /// <summary>工具上报的未保存状态发生变化。</summary>
    DirtyStateChanged,
}

/// <summary>
/// 一次工作区状态变化通知。包含稳定标识、变化种类与变化前后的值，
/// 订阅者不需要读取可变内部集合即可判断需要更新什么。
/// </summary>
public sealed record WorkspaceChangeNotification(
    WorkspaceId WorkspaceId,
    WorkspaceChangeKind Kind,
    WorkspaceLifecycleState OldState,
    WorkspaceLifecycleState NewState,
    bool OldIsDirty,
    bool NewIsDirty);

/// <summary>
/// 工作区状态变化订阅入口。与 <see cref="IWorkspaceHost"/> 分离，
/// 只读消费者（Shell、Inspector、标签）可以依赖该契约而不依赖宿主写入 API。
///
/// 线程与顺序契约：
/// - 通知在引起变化调用的同一线程上同步触发；Core 不切换线程、不排队。
/// - 通知在状态已提交之后发出，因此订阅者读取 <see cref="IWorkspaceHost.Active"/> 等状态时看到的是终态。
/// - 同一次变化按订阅顺序通知；某个订阅者抛出的异常会被记录并隔离，不影响其余订阅者，
///   也不会回滚已经提交的状态迁移。
/// - 订阅者不得在通知中再次调用宿主写入 API 造成递归依赖；需要修改状态时应通过显式调用完成。
/// </summary>
public interface IWorkspaceChangeSource
{
    /// <summary>订阅状态变化；返回的令牌释放后不再收到通知。</summary>
    IDisposable Subscribe(Action<WorkspaceChangeNotification> observer);
}
