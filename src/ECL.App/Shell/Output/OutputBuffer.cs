// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Shell.Output;

/// <summary>
/// Output 面板的待刷新队列。日志生产者（可能是任意线程）与 UI 刷新线程通过同一把锁同步。
///
/// 有界策略：容量固定，溢出时丢弃最旧条目并累计 <see cref="DroppedCount"/>。
/// 因此“生产速度长期高于 UI 刷新速度”时内存仍然有界，丢失量可观察。
/// <see cref="Clear"/> 在锁内清空待处理条目，清空后不会再有旧条目前移到 UI。
/// </summary>
internal sealed class OutputBuffer(int capacity)
{
    private readonly object _gate = new();
    private readonly Queue<AppLogEntry> _pending = new();
    private int _dropped;

    public int Capacity { get; } = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));

    public int DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0;
            }
        }
    }

    public void Enqueue(AppLogEntry entry)
    {
        lock (_gate)
        {
            if (_pending.Count == Capacity)
            {
                _pending.Dequeue();
                _dropped++;
            }

            _pending.Enqueue(entry);
        }
    }

    /// <summary>取走当前全部待处理条目（FIFO 顺序）；没有条目时返回空数组。</summary>
    public AppLogEntry[] Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return [];
            }

            var drained = _pending.ToArray();
            _pending.Clear();
            return drained;
        }
    }

    /// <summary>清空待处理条目并重置截断计数；返回清空前累计的丢弃数。</summary>
    public int Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            var dropped = _dropped;
            _dropped = 0;
            return dropped;
        }
    }
}
