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
///
/// 代次下限：队列自己维护 generation floor，并与 pending 队列、丢弃计数共用同一把锁。
/// 所有代次判定（拒绝入队、清理过期条目、统计丢弃）都在这个临界区内完成，因此
/// “生产者读取旧下限后暂停、清空推进代次后恢复”的交错不可能让旧代次条目污染队列容量或计数。
/// <see cref="Drain"/> 仍按传入下限过滤，作为刷新时的第二道防线。
/// </summary>
internal sealed class OutputBuffer(int capacity)
{
    private readonly object _gate = new();
    private readonly Queue<PendingOutputEntry> _pending = new();

    // 丢弃数按条目所属代次归属：推进下限时丢弃旧代次计数，保留新代次真实丢弃数。
    private readonly Dictionary<long, int> _droppedByGeneration = [];
    private long _generationFloor;

    public int Capacity { get; } = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));

    /// <summary>当前代次下限；低于它的条目既不能入队，也不计入丢弃数。</summary>
    public long GenerationFloor
    {
        get
        {
            lock (_gate)
            {
                return _generationFloor;
            }
        }
    }

    /// <summary>当前代次下限及之后的条目因容量限制被丢弃的总数。</summary>
    public int DroppedCount
    {
        get
        {
            lock (_gate)
            {
                var total = 0;
                foreach (var pair in _droppedByGeneration)
                {
                    if (pair.Key >= _generationFloor)
                    {
                        total += pair.Value;
                    }
                }

                return total;
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

    /// <summary>
    /// 入队。代次低于当前下限的条目在锁内直接拒绝：不占用容量、不增加丢弃计数。
    /// ViewModel 的 Volatile 下限检查只是快速过滤，这里才是最终防线。
    /// </summary>
    public void Enqueue(AppLogEntry entry, long generation)
    {
        lock (_gate)
        {
            if (generation < _generationFloor)
            {
                return;
            }

            if (_pending.Count == Capacity)
            {
                var dropped = _pending.Dequeue();

                // 下限推进已经清掉旧代次条目；保留防御性判断，避免任何路径把过期条目计成丢弃。
                if (dropped.Generation >= _generationFloor)
                {
                    _droppedByGeneration[dropped.Generation] =
                        _droppedByGeneration.GetValueOrDefault(dropped.Generation) + 1;
                }
            }

            _pending.Enqueue(new PendingOutputEntry(entry, generation));
        }
    }

    /// <summary>
    /// 单调推进代次下限：只前进、不回退，重复推进同一代次是幂等的。
    ///
    /// 在锁内清理所有低于新下限的 pending 条目与对应丢弃计数；被下限淘汰的旧条目属于
    /// 用户已清空的内容，不计入丢弃数。代次不低于新下限的条目（包括清空之后才入队的
    /// 新代次条目）保持原有 FIFO 顺序与真实丢弃计数。
    /// </summary>
    public void AdvanceGenerationFloor(long generation)
    {
        lock (_gate)
        {
            if (generation <= _generationFloor)
            {
                return;
            }

            _generationFloor = generation;

            if (_pending.Count > 0)
            {
                var kept = new Queue<PendingOutputEntry>(_pending.Count);
                while (_pending.Count > 0)
                {
                    var item = _pending.Dequeue();
                    if (item.Generation >= generation)
                    {
                        kept.Enqueue(item);
                    }
                }

                while (kept.Count > 0)
                {
                    _pending.Enqueue(kept.Dequeue());
                }
            }

            if (_droppedByGeneration.Count > 0)
            {
                List<long>? expired = null;
                foreach (var droppedGeneration in _droppedByGeneration.Keys)
                {
                    if (droppedGeneration < generation)
                    {
                        (expired ??= []).Add(droppedGeneration);
                    }
                }

                if (expired is not null)
                {
                    foreach (var droppedGeneration in expired)
                    {
                        _droppedByGeneration.Remove(droppedGeneration);
                    }
                }
            }
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

    /// <summary>清空待处理条目并重置全部丢弃计数；返回清空前当前下限及以上代次的丢弃数。</summary>
    public int Clear()
    {
        lock (_gate)
        {
            _pending.Clear();

            var dropped = 0;
            foreach (var pair in _droppedByGeneration)
            {
                if (pair.Key >= _generationFloor)
                {
                    dropped += pair.Value;
                }
            }

            _droppedByGeneration.Clear();
            return dropped;
        }
    }
}
