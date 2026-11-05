// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Shell.Output;

/// <summary>
/// Output 面板的待刷新队列条目：日志条目与它所属的输出代次。
/// 代次在这里保留下来，使“已进入队列但属于旧代次”的条目可以在刷新时被丢弃。
/// </summary>
internal readonly record struct PendingOutputEntry(AppLogEntry Entry, long Generation);

/// <summary>
/// Output 面板的待刷新队列。日志生产者（可能是任意线程）与 UI 刷新线程通过同一把锁同步。
///
/// 有界策略：容量固定，溢出时丢弃最旧条目并累计 <see cref="DroppedCount"/>。
/// 因此“生产速度长期高于 UI 刷新速度”时内存仍然有界，丢失量可观察。
/// <see cref="Clear"/> 在锁内清空待处理条目，清空后不会再有旧条目前移到 UI。
/// <see cref="Drain"/> 按代次下限过滤：即使某个旧代次条目在清空之后才进入队列，
/// 也不会被交给 UI。
/// </summary>
internal sealed class OutputBuffer(int capacity)
{
    private readonly object _gate = new();
    private readonly Queue<PendingOutputEntry> _pending = new();
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

    public void Enqueue(AppLogEntry entry, long generation)
    {
        lock (_gate)
        {
            if (_pending.Count == Capacity)
            {
                _pending.Dequeue();
                _dropped++;
            }

            _pending.Enqueue(new PendingOutputEntry(entry, generation));
        }
    }

    /// <summary>
    /// 取走当前全部待处理条目（FIFO 顺序），丢弃代次小于 <paramref name="generationFloor"/> 的过期条目；
    /// 没有条目时返回空数组。过期条目属于用户已经清空的内容，不计入截断数。
    /// </summary>
    public AppLogEntry[] Drain(long generationFloor)
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return [];
            }

            List<AppLogEntry>? current = null;
            while (_pending.Count > 0)
            {
                var item = _pending.Dequeue();
                if (item.Generation < generationFloor)
                {
                    continue;
                }

                (current ??= new List<AppLogEntry>(_pending.Count + 1)).Add(item.Entry);
            }

            return current is null ? [] : [.. current];
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
