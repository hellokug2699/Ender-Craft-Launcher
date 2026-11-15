// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>
/// 宿主掌握的工作区状态。工具内部视图状态、文档修改与后台任务不存放在这里；
/// <see cref="IsDirty"/> 只是工具上报的关闭保护信号。
/// </summary>
public sealed class WorkspaceEntry
{
    internal WorkspaceEntry(WorkspaceDescriptor descriptor)
    {
        Descriptor = descriptor;
        State = WorkspaceLifecycleState.Created;
    }

    public WorkspaceDescriptor Descriptor { get; }

    public WorkspaceId Id => Descriptor.Id;

    public string ToolId => Descriptor.ToolId;

    public string Title => Descriptor.Title;

    public string? TargetResource => Descriptor.TargetResource;

    public WorkspaceLifecycleState State { get; internal set; }

    /// <summary>工具上报的未保存状态；宿主用它决定关闭是否需要用户确认。</summary>
    public bool IsDirty { get; internal set; }

    public bool IsDisposed => State == WorkspaceLifecycleState.Disposed;

    /// <summary>是否仍由宿主持有（可激活、可关闭）。</summary>
    public bool IsOpen => State is not WorkspaceLifecycleState.Disposed;

    /// <summary>异常状态不参与普通激活流程，但仍需要展示原因。</summary>
    public bool IsFaulted => State is WorkspaceLifecycleState.Crashed or WorkspaceLifecycleState.Unavailable;

    internal static bool CanTransition(WorkspaceLifecycleState from, WorkspaceLifecycleState to) => (from, to) switch
    {
        (WorkspaceLifecycleState.Created, WorkspaceLifecycleState.Opening) => true,
        (WorkspaceLifecycleState.Opening, WorkspaceLifecycleState.Ready) => true,
        (WorkspaceLifecycleState.Opening, WorkspaceLifecycleState.Crashed) => true,
        (WorkspaceLifecycleState.Opening, WorkspaceLifecycleState.Unavailable) => true,
        (WorkspaceLifecycleState.Ready, WorkspaceLifecycleState.Suspended) => true,
        (WorkspaceLifecycleState.Ready, WorkspaceLifecycleState.Closing) => true,
        (WorkspaceLifecycleState.Ready, WorkspaceLifecycleState.Crashed) => true,
        (WorkspaceLifecycleState.Ready, WorkspaceLifecycleState.Unavailable) => true,
        (WorkspaceLifecycleState.Suspended, WorkspaceLifecycleState.Ready) => true,
        (WorkspaceLifecycleState.Suspended, WorkspaceLifecycleState.Closing) => true,
        (WorkspaceLifecycleState.Suspended, WorkspaceLifecycleState.Crashed) => true,
        (WorkspaceLifecycleState.Suspended, WorkspaceLifecycleState.Unavailable) => true,
        (WorkspaceLifecycleState.Closing, WorkspaceLifecycleState.Disposed) => true,
        (WorkspaceLifecycleState.Closing, WorkspaceLifecycleState.Crashed) => true,
        _ => false,
    };
}
