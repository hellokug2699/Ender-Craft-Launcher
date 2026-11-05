// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace Ecl.Core.Workspace;

/// <summary>
/// 默认 <see cref="IWorkspaceHost"/> 实现。只保存工作区身份、生命周期与可见布局；
/// 页面、工具状态和后台任务由上层持有，因此该类型可以在无 UI 的测试中直接验证。
///
/// 线程模型：非线程安全，只应由一个所有者线程驱动（WPF 下即 UI 线程）。
/// 状态变化通知在引起变化的同一线程上同步发出；订阅者若需要切换线程必须自行封送。
/// 订阅者异常在 <see cref="Notify"/> 中隔离并记录日志，不会回滚已提交的状态。
/// </summary>
public sealed class WorkspaceHost : IWorkspaceHost
{
    private readonly List<WorkspaceEntry> _entries = [];
    private readonly List<Subscription> _subscribers = [];
    private readonly IWorkspaceCloseGuard _closeGuard;
    private readonly ILogger<WorkspaceHost>? _logger;
    private WorkspaceLayout _layout = WorkspaceLayout.Empty;
    private WorkspaceEntry? _active;

    public WorkspaceHost(IWorkspaceCloseGuard? closeGuard = null, ILogger<WorkspaceHost>? logger = null)
    {
        _closeGuard = closeGuard ?? new DefaultWorkspaceCloseGuard();
        _logger = logger;
    }

    public IReadOnlyList<WorkspaceEntry> Workspaces => _entries;

    public WorkspaceLayout Layout => _layout;

    public WorkspaceEntry? Active => _active;

    public event EventHandler<WorkspaceEventArgs>? WorkspaceOpened;

    public event EventHandler<WorkspaceEventArgs>? WorkspaceActivated;

    public event EventHandler<WorkspaceClosedEventArgs>? WorkspaceClosed;

    public event EventHandler? LayoutChanged;

    public IDisposable Subscribe(Action<WorkspaceChangeNotification> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var subscription = new Subscription(this, observer);
        _subscribers.Add(subscription);
        return subscription;
    }

    public WorkspaceEntry Open(WorkspaceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (Find(descriptor.Id) is not null)
        {
            throw new InvalidOperationException($"工作区 {descriptor.Id} 已经登记。");
        }

        var entry = new WorkspaceEntry(descriptor);
        _entries.Add(entry);
        Transition(entry, WorkspaceLifecycleState.Opening);
        WorkspaceOpened?.Invoke(this, new WorkspaceEventArgs(entry));
        return entry;
    }

    public WorkspaceEntry? Find(WorkspaceId id) => _entries.FirstOrDefault(entry => entry.Id == id);

    /// <summary>
    /// 按去重键（规范化 ToolId + 规范化目标资源）查找；比较语义只在
    /// <see cref="WorkspaceDedupKey"/> 中实现一次。非法身份不会被匹配。
    /// </summary>
    public WorkspaceEntry? FindByTarget(string toolId, string? targetResource) =>
        _entries.FirstOrDefault(entry =>
            WorkspaceDedupKey.AreEqual(entry.ToolId, entry.TargetResource, toolId, targetResource));

    public bool Activate(WorkspaceId id)
    {
        var entry = Find(id);
        if (entry is null || !CanActivate(entry))
        {
            return false;
        }

        if (entry.State == WorkspaceLifecycleState.Suspended)
        {
            Transition(entry, WorkspaceLifecycleState.Ready);
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

    public bool MarkReady(WorkspaceId id) => TryTransition(id, WorkspaceLifecycleState.Ready);

    public bool MarkSuspended(WorkspaceId id) => TryTransition(id, WorkspaceLifecycleState.Suspended);

    public bool MarkFaulted(WorkspaceId id, WorkspaceLifecycleState fault)
    {
        if (fault is not (WorkspaceLifecycleState.Crashed or WorkspaceLifecycleState.Unavailable))
        {
            throw new ArgumentOutOfRangeException(nameof(fault), fault, "异常状态只能是 Crashed 或 Unavailable。");
        }

        return TryTransition(id, fault);
    }

    public bool MarkDirty(WorkspaceId id, bool isDirty)
    {
        var entry = Find(id);
        if (entry is null || entry.IsDisposed)
        {
            return false;
        }

        if (entry.IsDirty == isDirty)
        {
            return true;
        }

        var previousDirty = entry.IsDirty;
        entry.IsDirty = isDirty;
        Notify(new WorkspaceChangeNotification(
            entry.Id,
            WorkspaceChangeKind.DirtyStateChanged,
            entry.State,
            entry.State,
            previousDirty,
            entry.IsDirty));
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

        Transition(entry, WorkspaceLifecycleState.Closing);
        var index = _entries.IndexOf(entry);
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

        Transition(entry, WorkspaceLifecycleState.Disposed);

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

    private bool TryTransition(WorkspaceId id, WorkspaceLifecycleState target)
    {
        var entry = Find(id);
        return entry is not null && Transition(entry, target);
    }

    private bool Transition(WorkspaceEntry entry, WorkspaceLifecycleState target)
    {
        if (!WorkspaceEntry.CanTransition(entry.State, target))
        {
            return false;
        }

        var previous = entry.State;
        entry.State = target;
        Notify(new WorkspaceChangeNotification(
            entry.Id,
            WorkspaceChangeKind.LifecycleStateChanged,
            previous,
            target,
            entry.IsDirty,
            entry.IsDirty));
        return true;
    }

    private void Notify(WorkspaceChangeNotification notification)
    {
        if (_subscribers.Count == 0)
        {
            return;
        }

        // 快照订阅者列表：订阅/退订可以在回调中发生，不能影响本次通知的稳定顺序。
        foreach (var subscription in _subscribers.ToArray())
        {
            if (subscription.IsDisposed)
            {
                continue;
            }

            try
            {
                subscription.Invoke(notification);
            }
            catch (Exception exception)
            {
                // 单个订阅者失败不得阻止其他订阅者，也不得回滚已提交的状态迁移。
                _logger?.LogError(
                    exception,
                    "工作区状态订阅者处理 {Kind} 通知失败：{WorkspaceId}",
                    notification.Kind,
                    notification.WorkspaceId);
            }
        }
    }

    private static bool CanActivate(WorkspaceEntry entry) =>
        entry.State is WorkspaceLifecycleState.Ready
            or WorkspaceLifecycleState.Suspended
            or WorkspaceLifecycleState.Crashed
            or WorkspaceLifecycleState.Unavailable;

    private sealed class Subscription(WorkspaceHost owner, Action<WorkspaceChangeNotification> observer) : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Invoke(WorkspaceChangeNotification notification) => observer(notification);

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            owner._subscribers.Remove(this);
        }
    }
}
