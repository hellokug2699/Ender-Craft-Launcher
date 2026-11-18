// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Tests.Support;

/// <summary>
/// 自写假时钟：只实现 <see cref="GetUtcNow"/> 与 <see cref="CreateTimer"/>，
/// 足够驱动 <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c> 与
/// <c>OperationService</c> 的停滞计时，不需要引入额外测试包。
/// 时间只在 <see cref="Advance"/> 时前进，因此到期判断完全确定。
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset? start = null) =>
        _utcNow = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>推进时间并触发已到期的计时器；回调在锁外执行。</summary>
    public void Advance(TimeSpan delta)
    {
        List<FakeTimer> due;
        lock (_gate)
        {
            _utcNow += delta;
            due = _timers.Where(timer => timer.IsDue(_utcNow)).ToList();
        }

        foreach (var timer in due)
        {
            timer.Fire(_utcNow);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._utcNow + dueTime;
                return true;
            }
        }

        public bool IsDue(DateTimeOffset now)
        {
            lock (_owner._gate)
            {
                return !_disposed && _dueAt is { } dueAt && dueAt <= now;
            }
        }

        public void Fire(DateTimeOffset now)
        {
            lock (_owner._gate)
            {
                if (_disposed || _dueAt is null || _dueAt > now)
                {
                    return;
                }

                _dueAt = _period == Timeout.InfiniteTimeSpan ? null : now + _period;
            }

            _callback(_state);
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                _disposed = true;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
