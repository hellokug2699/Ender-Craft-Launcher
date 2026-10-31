// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Operations;

public sealed record OperationServiceOptions
{
    /// <summary>多久没有进度就标记为“停滞”。停滞不是失败。</summary>
    public TimeSpan StallAfter { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>停滞检查周期；设置为 <see cref="TimeSpan.Zero"/> 可关闭检查。</summary>
    public TimeSpan StallCheckInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>内存中最多保留的任务数量，超出时优先移除最早结束的任务。</summary>
    public int MaxRetainedOperations { get; init; } = 100;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// 进程内任务中心。第一阶段只保留内存快照：接口允许以后替换为持久化实现。
/// 所有读写都在同一个锁内完成，事件在锁外触发，避免 UI 回调造成死锁。
/// </summary>
public sealed class OperationService : IOperationService, IOperationStore, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OperationEntry> _entries = [];
    private readonly OperationServiceOptions _options;
    private readonly ITimer? _stallTimer;

    public OperationService()
        : this(new OperationServiceOptions())
    {
    }

    public OperationService(OperationServiceOptions options)
    {
        _options = options;
        if (options.StallCheckInterval > TimeSpan.Zero && options.StallAfter > TimeSpan.Zero)
        {
            _stallTimer = options.TimeProvider.CreateTimer(
                _ => CheckStalls(),
                null,
                options.StallCheckInterval,
                options.StallCheckInterval);
        }
    }

    public IOperationStore Store => this;

    public event EventHandler<OperationSnapshot>? Changed;

    public IReadOnlyList<OperationSnapshot> Snapshots
    {
        get
        {
            var now = _options.TimeProvider.GetUtcNow();
            lock (_gate)
            {
                return _entries.Values
                    .OrderByDescending(entry => entry.StartedAt)
                    .Select(entry => entry.ToSnapshot(now, _options.StallAfter))
                    .ToArray();
            }
        }
    }

    public OperationSnapshot? TryGet(Guid operationId)
    {
        var now = _options.TimeProvider.GetUtcNow();
        lock (_gate)
        {
            return _entries.TryGetValue(operationId, out var entry)
                ? entry.ToSnapshot(now, _options.StallAfter)
                : null;
        }
    }

    public IOperationHandle Begin(OperationStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (string.IsNullOrWhiteSpace(start.Title))
        {
            throw new ArgumentException("任务标题不能为空。", nameof(start));
        }

        var id = Guid.NewGuid();
        OperationSnapshot snapshot;
        lock (_gate)
        {
            TrimLocked();
            var now = _options.TimeProvider.GetUtcNow();
            var entry = new OperationEntry(id, start, now);
            _entries[id] = entry;
            snapshot = entry.ToSnapshot(now, _options.StallAfter);
        }

        RaiseChanged(snapshot);
        return new OperationHandle(this, id);
    }

    public bool Remove(Guid operationId)
    {
        lock (_gate)
        {
            return _entries.Remove(operationId);
        }
    }

    public int PruneCompleted(DateTimeOffset completedBefore)
    {
        lock (_gate)
        {
            var expired = _entries.Values
                .Where(entry => entry.IsTerminal && entry.UpdatedAt < completedBefore)
                .Select(entry => entry.Id)
                .ToArray();
            foreach (var id in expired)
            {
                _entries.Remove(id);
            }

            return expired.Length;
        }
    }

    internal OperationSnapshot Require(Guid id)
    {
        var now = _options.TimeProvider.GetUtcNow();
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry)
                ? entry.ToSnapshot(now, _options.StallAfter)
                : throw new InvalidOperationException($"任务不存在：{id}");
        }
    }

    internal void ApplyProgress(Guid id, OperationProgressUpdate update)
    {
        OperationSnapshot? snapshot = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.IsTerminal)
            {
                return;
            }

            entry.Apply(update, _options.TimeProvider.GetUtcNow());
            snapshot = entry.ToSnapshot(_options.TimeProvider.GetUtcNow(), _options.StallAfter);
        }

        RaiseChanged(snapshot);
    }

    internal void ApplyRetry(Guid id, int attempt, int maxAttempts, string? reason)
    {
        OperationSnapshot? snapshot = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.IsTerminal)
            {
                return;
            }

            entry.Retry(attempt, maxAttempts, reason, _options.TimeProvider.GetUtcNow());
            snapshot = entry.ToSnapshot(_options.TimeProvider.GetUtcNow(), _options.StallAfter);
        }

        RaiseChanged(snapshot);
    }

    internal void ApplyStage(Guid id, string stageId, string? stageText)
    {
        OperationSnapshot? snapshot = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.IsTerminal)
            {
                return;
            }

            entry.StageId = stageId;
            entry.StageText = stageText;
            entry.UpdatedAt = _options.TimeProvider.GetUtcNow();
            snapshot = entry.ToSnapshot(entry.UpdatedAt, _options.StallAfter);
        }

        RaiseChanged(snapshot);
    }

    internal void ApplyPause(Guid id, bool paused)
    {
        OperationSnapshot? snapshot = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.IsTerminal)
            {
                return;
            }

            entry.IsPaused = paused;
            entry.IsStalled = false;
            entry.UpdatedAt = _options.TimeProvider.GetUtcNow();
            if (!paused)
            {
                entry.LastActivityAt = entry.UpdatedAt;
            }

            snapshot = entry.ToSnapshot(entry.UpdatedAt, _options.StallAfter);
        }

        RaiseChanged(snapshot);
    }

    internal void ApplyComplete(Guid id, OperationProgressUpdate? finalUpdate)
    {
        OperationSnapshot? snapshot = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.IsTerminal)
            {
                return;
            }

            var now = _options.TimeProvider.GetUtcNow();
            if (finalUpdate is not null)
            {
                entry.Apply(finalUpdate, now);
            }

            entry.State = OperationState.Completed;
            entry.IsPaused = false;
            entry.IsStalled = false;
            entry.UpdatedAt = now;
            entry.LastActivityAt = now;
            snapshot = entry.ToSnapshot(now, _options.StallAfter);
        }

        RaiseChanged(snapshot);
    }

    internal void ApplyFailure(Guid id, OperationError error)
    {
        OperationSnapshot? snapshot = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.IsTerminal)
            {
                return;
            }

            var now = _options.TimeProvider.GetUtcNow();
            entry.State = error.Kind == OperationErrorKind.Cancelled
                ? OperationState.Cancelled
                : OperationState.Failed;
            entry.Error = error;
            entry.IsPaused = false;
            entry.IsStalled = false;
            entry.UpdatedAt = now;
            snapshot = entry.ToSnapshot(now, _options.StallAfter);
        }

        RaiseChanged(snapshot);
    }

    internal void Abandon(Guid id)
    {
        OperationSnapshot? snapshot = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.IsTerminal)
            {
                return;
            }

            var now = _options.TimeProvider.GetUtcNow();
            entry.State = OperationState.Cancelled;
            entry.Error = new OperationError(
                OperationErrorKind.Cancelled,
                "operation.abandoned",
                "任务在结束前被释放。");
            entry.IsPaused = false;
            entry.IsStalled = false;
            entry.UpdatedAt = now;
            snapshot = entry.ToSnapshot(now, _options.StallAfter);
        }

        RaiseChanged(snapshot);
    }

    private void CheckStalls()
    {
        var changed = new List<OperationSnapshot>();
        var now = _options.TimeProvider.GetUtcNow();
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.IsTerminal || entry.IsPaused || entry.IsStalled)
                {
                    continue;
                }

                if (now - entry.LastActivityAt >= _options.StallAfter)
                {
                    entry.IsStalled = true;
                    entry.UpdatedAt = now;
                    changed.Add(entry.ToSnapshot(now, _options.StallAfter));
                }
            }
        }

        foreach (var snapshot in changed)
        {
            RaiseChanged(snapshot);
        }
    }

    private void TrimLocked()
    {
        var limit = Math.Max(1, _options.MaxRetainedOperations);
        while (_entries.Count >= limit)
        {
            var victim = _entries.Values
                             .Where(entry => entry.IsTerminal)
                             .OrderBy(entry => entry.UpdatedAt)
                             .FirstOrDefault()
                         ?? _entries.Values.OrderBy(entry => entry.StartedAt).FirstOrDefault();
            if (victim is null)
            {
                return;
            }

            _entries.Remove(victim.Id);
        }
    }

    private void RaiseChanged(OperationSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        var handler = Changed;
        if (handler is null)
        {
            return;
        }

        // 观察者（尤其是 UI）出错不能影响正在执行的下载/安装任务。
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<OperationSnapshot>)subscriber)(this, snapshot);
            }
            catch
            {
                // 忽略观察者异常。
            }
        }
    }

    public void Dispose() => _stallTimer?.Dispose();

    private sealed class OperationEntry
    {
        public OperationEntry(Guid id, OperationStart start, DateTimeOffset now)
        {
            Id = id;
            Kind = start.Kind;
            Title = start.Title.Trim();
            State = OperationState.Running;
            StageId = start.StageId;
            StageText = start.StageText;
            Source = start.Source;
            Total = start.Total;
            TotalBytes = start.TotalBytes;
            CanCancel = start.CanCancel;
            CanRetry = start.CanRetry;
            StartedAt = now;
            UpdatedAt = now;
            LastActivityAt = now;
        }

        public Guid Id { get; }

        public OperationKind Kind { get; }

        public string Title { get; }

        public OperationState State { get; set; }

        public string? StageId { get; set; }

        public string? StageText { get; set; }

        public string? CurrentItem { get; set; }

        public long Completed { get; set; }

        public long? Total { get; set; }

        public long BytesDownloaded { get; set; }

        public long? TotalBytes { get; set; }

        public double? BytesPerSecond { get; set; }

        public string? Source { get; }

        public OperationError? Error { get; set; }

        public int RetryAttempt { get; set; }

        public int MaxRetryAttempts { get; set; }

        public string? RetryReason { get; set; }

        public bool CanCancel { get; }

        public bool CanRetry { get; }

        public bool IsPaused { get; set; }

        public bool IsStalled { get; set; }

        public DateTimeOffset StartedAt { get; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset LastActivityAt { get; set; }

        public bool IsTerminal => State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled;

        public void Apply(OperationProgressUpdate update, DateTimeOffset now)
        {
            if (update.StageId is not null)
            {
                StageId = update.StageId;
            }

            if (update.StageText is not null)
            {
                StageText = update.StageText;
            }

            if (update.CurrentItem is not null)
            {
                CurrentItem = update.CurrentItem;
            }

            if (update.Completed is { } completed)
            {
                Completed = completed;
            }

            if (update.Total is { } total)
            {
                Total = total;
            }

            if (update.BytesDownloaded is { } bytes)
            {
                BytesDownloaded = bytes;
            }

            if (update.TotalBytes is { } totalBytes)
            {
                TotalBytes = totalBytes;
            }

            if (update.BytesPerSecond is { } speed)
            {
                BytesPerSecond = speed;
            }

            if (update.IsPaused is { } paused)
            {
                IsPaused = paused;
            }

            State = OperationState.Running;
            RetryAttempt = 0;
            MaxRetryAttempts = 0;
            RetryReason = null;
            IsStalled = false;
            UpdatedAt = now;
            LastActivityAt = now;
        }

        public void Retry(int attempt, int maxAttempts, string? reason, DateTimeOffset now)
        {
            State = OperationState.Retrying;
            RetryAttempt = attempt;
            MaxRetryAttempts = maxAttempts;
            RetryReason = reason;
            UpdatedAt = now;
            LastActivityAt = now;
        }

        public OperationSnapshot ToSnapshot(DateTimeOffset now, TimeSpan stallAfter) =>
            new()
            {
                Id = Id,
                Kind = Kind,
                Title = Title,
                State = State,
                StageId = StageId,
                StageText = StageText,
                CurrentItem = CurrentItem,
                Completed = Completed,
                Total = Total,
                BytesDownloaded = BytesDownloaded,
                TotalBytes = TotalBytes,
                BytesPerSecond = BytesPerSecond,
                Source = Source,
                Error = Error,
                RetryAttempt = RetryAttempt,
                MaxRetryAttempts = MaxRetryAttempts,
                RetryReason = RetryReason,
                CanCancel = CanCancel,
                CanRetry = CanRetry,
                IsPaused = IsPaused,
                IsStalled = IsStalled ||
                            (!IsTerminal && !IsPaused && stallAfter > TimeSpan.Zero &&
                             now - LastActivityAt >= stallAfter),
                StartedAt = StartedAt,
                UpdatedAt = UpdatedAt,
            };
    }

    private sealed class OperationHandle(OperationService service, Guid id) : IOperationHandle
    {
        public Guid Id => id;

        public OperationSnapshot Snapshot => service.Require(id);

        public void Report(OperationProgressUpdate update) => service.ApplyProgress(id, update);

        public void SetStage(string stageId, string? stageText = null) => service.ApplyStage(id, stageId, stageText);

        public void Retrying(int attempt, int maxAttempts, string? reason = null) =>
            service.ApplyRetry(id, attempt, maxAttempts, reason);

        public void Complete(OperationProgressUpdate? finalUpdate = null) => service.ApplyComplete(id, finalUpdate);

        public void Fail(OperationError error) => service.ApplyFailure(id, error);

        public void Cancel() => service.ApplyFailure(
            id,
            new OperationError(OperationErrorKind.Cancelled, "operation.cancelled", "任务已取消。"));

        public void Pause() => service.ApplyPause(id, paused: true);

        public void Resume() => service.ApplyPause(id, paused: false);

        public void Dispose() => service.Abandon(id);
    }
}
