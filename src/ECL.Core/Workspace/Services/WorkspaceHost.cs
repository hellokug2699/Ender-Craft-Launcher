// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>
/// 默认 <see cref="IWorkspaceHost"/> 实现。只保存工作区身份、生命周期与可见布局；
/// 页面、工具状态和后台任务由上层持有，因此该类型可以在无 UI 的测试中直接验证。
/// 非线程安全：只应由 UI 组合根在单一逻辑线程上驱动。
/// </summary>
public sealed class WorkspaceHost : IWorkspaceHost
{
    private readonly List<WorkspaceEntry> _entries = [];
    private readonly IWorkspaceCloseGuard _closeGuard;
    private WorkspaceLayout _layout = WorkspaceLayout.Empty;
    private WorkspaceEntry? _active;

    public WorkspaceHost(IWorkspaceCloseGuard? closeGuard = null) =>
        _closeGuard = closeGuard ?? new DefaultWorkspaceCloseGuard();

    public IReadOnlyList<WorkspaceEntry> Workspaces => _entries;

    public WorkspaceLayout Layout => _layout;

    public WorkspaceEntry? Active => _active;

    public event EventHandler<WorkspaceEventArgs>? WorkspaceOpened;

    public event EventHandler<WorkspaceEventArgs>? WorkspaceActivated;

    public event EventHandler<WorkspaceClosedEventArgs>? WorkspaceClosed;

    public event EventHandler? LayoutChanged;

    public WorkspaceEntry Open(WorkspaceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (Find(descriptor.Id) is not null)
        {
            throw new InvalidOperationException($"工作区 {descriptor.Id} 已经登记。");
        }

        var entry = new WorkspaceEntry(descriptor) { State = WorkspaceLifecycleState.Opening };
        _entries.Add(entry);
        WorkspaceOpened?.Invoke(this, new WorkspaceEventArgs(entry));
        return entry;
    }

    public WorkspaceEntry? Find(WorkspaceId id) => _entries.FirstOrDefault(entry => entry.Id == id);

    public WorkspaceEntry? FindByTarget(string toolId, string? targetResource) =>
        _entries.FirstOrDefault(entry =>
            string.Equals(entry.ToolId, toolId, StringComparison.Ordinal)
            && string.Equals(entry.TargetResource, targetResource, StringComparison.Ordinal));

    public bool Activate(WorkspaceId id)
    {
        var entry = Find(id);
        if (entry is null || !CanActivate(entry))
        {
            return false;
        }

        if (entry.State == WorkspaceLifecycleState.Suspended)
        {
            entry.State = WorkspaceLifecycleState.Ready;
        }

        var previous = _active;
        _active = entry;

        // 单工作区模式：被激活但不在可见布局中的工作区成为唯一可见工作区。
        // 分割模式由 TrySetLayout 建立，并保留其中已可见的工作区。
        if (!_layout.Contains(entry.Id))
        {
            _layout = WorkspaceLayout.Single(entry.Id);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        if (!ReferenceEquals(previous, entry))
        {
            WorkspaceActivated?.Invoke(this, new WorkspaceEventArgs(entry));
        }

        return true;
    }

    public bool MarkReady(WorkspaceId id) => Transition(id, WorkspaceLifecycleState.Ready);

    public bool MarkSuspended(WorkspaceId id) => Transition(id, WorkspaceLifecycleState.Suspended);

    public bool MarkFaulted(WorkspaceId id, WorkspaceLifecycleState fault)
    {
        if (fault is not (WorkspaceLifecycleState.Crashed or WorkspaceLifecycleState.Unavailable))
        {
            throw new ArgumentOutOfRangeException(nameof(fault), fault, "异常状态只能是 Crashed 或 Unavailable。");
        }

        return Transition(id, fault);
    }

    public bool MarkDirty(WorkspaceId id, bool isDirty)
    {
        var entry = Find(id);
        if (entry is null || entry.IsDisposed)
        {
            return false;
        }

        entry.IsDirty = isDirty;
        return true;
    }

    public WorkspaceCloseDecision RequestClose(WorkspaceId id) =>
        Find(id) is { } entry
            ? _closeGuard.Evaluate(entry)
            : WorkspaceCloseDecision.Block(WorkspaceCloseReason.AlreadyClosed);

    public WorkspaceCloseAttempt Close(WorkspaceId id, bool confirmed = false)
    {
        var entry = Find(id);
        if (entry is null)
        {
            return WorkspaceCloseAttempt.Refused(WorkspaceCloseDecision.Block(WorkspaceCloseReason.AlreadyClosed));
        }

        var decision = _closeGuard.Evaluate(entry);
        if (!decision.CanClose)
        {
            return WorkspaceCloseAttempt.Refused(decision);
        }

        if (decision.RequiresConfirmation && !confirmed)
        {
            return WorkspaceCloseAttempt.Refused(decision);
        }

        var index = _entries.IndexOf(entry);
        entry.State = WorkspaceLifecycleState.Closing;
        _entries.RemoveAt(index);

        var closedActive = ReferenceEquals(_active, entry);
        if (closedActive)
        {
            _active = _entries.Count == 0
                ? null
                : _entries[Math.Min(index, _entries.Count - 1)];
        }

        var layoutChanged = _layout.Contains(entry.Id);
        if (layoutChanged)
        {
            _layout = _layout.Remove(entry.Id);
        }

        if (_active is not null && !_layout.Contains(_active.Id))
        {
            _layout = WorkspaceLayout.Single(_active.Id);
            layoutChanged = true;
        }

        entry.State = WorkspaceLifecycleState.Disposed;

        WorkspaceClosed?.Invoke(this, new WorkspaceClosedEventArgs(entry.Descriptor, _active));
        if (layoutChanged)
        {
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        return new WorkspaceCloseAttempt(true, decision);
    }

    public bool TrySetLayout(WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!layout.Validate().IsValid)
        {
            return false;
        }

        foreach (var workspaceId in layout.Workspaces)
        {
            if (Find(workspaceId) is not { IsOpen: true })
            {
                return false;
            }
        }

        _layout = layout;
        if (layout.IsEmpty)
        {
            _active = null;
        }
        else if (_active is null || !layout.Contains(_active.Id))
        {
            _active = Find(layout.Workspaces[0]);
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool Transition(WorkspaceId id, WorkspaceLifecycleState target)
    {
        var entry = Find(id);
        if (entry is null || !WorkspaceEntry.CanTransition(entry.State, target))
        {
            return false;
        }

        entry.State = target;
        return true;
    }

    private static bool CanActivate(WorkspaceEntry entry) =>
        entry.State is WorkspaceLifecycleState.Ready
            or WorkspaceLifecycleState.Suspended
            or WorkspaceLifecycleState.Crashed
            or WorkspaceLifecycleState.Unavailable;
}
