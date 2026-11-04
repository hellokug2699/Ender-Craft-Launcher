// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

public sealed class WorkspaceEventArgs(WorkspaceEntry entry) : EventArgs
{
    public WorkspaceEntry Entry { get; } = entry;
}

public sealed class WorkspaceClosedEventArgs(WorkspaceDescriptor descriptor, WorkspaceEntry? active) : EventArgs
{
    public WorkspaceDescriptor Descriptor { get; } = descriptor;

    /// <summary>关闭后仍然活动的工作区；没有剩余工作区时为 null。</summary>
    public WorkspaceEntry? Active { get; } = active;
}

/// <summary>一次关闭尝试的结果。未关闭时 <see cref="Decision"/> 说明拒绝或待确认的原因。</summary>
public sealed record WorkspaceCloseAttempt(bool Closed, WorkspaceCloseDecision Decision)
{
    public static WorkspaceCloseAttempt Refused(WorkspaceCloseDecision decision) => new(false, decision);
}

/// <summary>
/// 宿主工作区登记与生命周期（ADR 0055 §1、§4）。不承载任何 UI 类型，也不负责工具业务状态。
/// </summary>
public interface IWorkspaceHost
{
    /// <summary>按打开顺序返回的工作区；关闭后移除。</summary>
    IReadOnlyList<WorkspaceEntry> Workspaces { get; }

    /// <summary>当前可见布局；单工作区模式下最多一个叶节点。</summary>
    WorkspaceLayout Layout { get; }

    WorkspaceEntry? Active { get; }

    event EventHandler<WorkspaceEventArgs>? WorkspaceOpened;

    event EventHandler<WorkspaceEventArgs>? WorkspaceActivated;

    event EventHandler<WorkspaceClosedEventArgs>? WorkspaceClosed;

    event EventHandler? LayoutChanged;

    /// <summary>登记新工作区（Created → Opening），返回宿主条目。</summary>
    WorkspaceEntry Open(WorkspaceDescriptor descriptor);

    WorkspaceEntry? Find(WorkspaceId id);

    WorkspaceEntry? FindByTarget(string toolId, string? targetResource);

    /// <summary>激活工作区（Opening 阶段之后可激活 Ready / Suspended 的工作区）。</summary>
    bool Activate(WorkspaceId id);

    bool MarkReady(WorkspaceId id);

    bool MarkSuspended(WorkspaceId id);

    bool MarkFaulted(WorkspaceId id, WorkspaceLifecycleState fault);

    bool MarkDirty(WorkspaceId id, bool isDirty);

    /// <summary>只做 Close Guard 判定，不改变状态；用于先询问用户再关闭。</summary>
    WorkspaceCloseDecision RequestClose(WorkspaceId id);

    /// <summary>
    /// 执行关闭。需要确认的关闭必须在 <paramref name="confirmed"/> 为 true 时才生效；
    /// 关闭只移除工作区，不会取消任何后台 Task。
    /// </summary>
    WorkspaceCloseAttempt Close(WorkspaceId id, bool confirmed = false);

    /// <summary>应用宿主计算的可见布局；拒绝超过深度/数量约束或引用未知工作区的布局。</summary>
    bool TrySetLayout(WorkspaceLayout layout);
}
