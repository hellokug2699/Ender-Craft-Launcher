// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>关闭被拒绝或需要确认的原因；用户可见文案由 UI 层按本地化资源呈现。</summary>
public enum WorkspaceCloseReason
{
    None,
    AlreadyClosed,
    UnsavedChanges,
    OperationInProgress,
}

/// <summary>
/// Close Guard 的判定结果。工作区、后台任务、文档与运行时是不同动作，
/// 该结果只回答“能否关闭工作区”，不代表取消任务。
/// </summary>
public sealed record WorkspaceCloseDecision(
    bool CanClose,
    bool RequiresConfirmation,
    WorkspaceCloseReason Reason)
{
    public static WorkspaceCloseDecision Allow { get; } = new(true, false, WorkspaceCloseReason.None);

    public static WorkspaceCloseDecision Confirm(WorkspaceCloseReason reason) => new(true, true, reason);

    public static WorkspaceCloseDecision Block(WorkspaceCloseReason reason) => new(false, false, reason);
}

/// <summary>
/// 工作区关闭保护。工具只能上报 Dirty State 和关闭建议，不能绕过宿主判定。
/// </summary>
public interface IWorkspaceCloseGuard
{
    WorkspaceCloseDecision Evaluate(WorkspaceEntry entry);
}

/// <summary>当前实现：未保存状态需要确认，已关闭或正在关闭的工作区拒绝重复关闭。</summary>
public sealed class DefaultWorkspaceCloseGuard : IWorkspaceCloseGuard
{
    public WorkspaceCloseDecision Evaluate(WorkspaceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.State switch
        {
            WorkspaceLifecycleState.Disposed => WorkspaceCloseDecision.Block(WorkspaceCloseReason.AlreadyClosed),
            WorkspaceLifecycleState.Closing => WorkspaceCloseDecision.Block(WorkspaceCloseReason.AlreadyClosed),
            _ when entry.IsDirty => WorkspaceCloseDecision.Confirm(WorkspaceCloseReason.UnsavedChanges),
            _ => WorkspaceCloseDecision.Allow,
        };
    }
}
