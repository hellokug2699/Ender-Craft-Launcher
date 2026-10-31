// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace Ecl.Operations;

/// <summary>
/// 平滑速度估算。使用指数平滑（EMA）而不是“总字节 / 任务总时长”，
/// 避免单个大文件瞬间完成时速度跳到离谱的数字。
/// <para>
/// 采样间隔小于 <c>minimumInterval</c> 时复用上一次结果，调用方可以按任意频率上报字节数，
/// 由这里负责节流计算。
/// </para>
/// </summary>
public sealed class SpeedMeter
{
    private readonly double _smoothingFactor;
    private readonly TimeSpan _minimumInterval;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastSampleTime;
    private long _lastBytes;
    private double? _bytesPerSecond;
    private bool _hasBaseline;

    public SpeedMeter(double smoothingFactor = 0.3, TimeSpan? minimumInterval = null)
    {
        if (smoothingFactor is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(smoothingFactor), "平滑系数必须在 (0, 1] 之间。");
        }

        _smoothingFactor = smoothingFactor;
        _minimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(200);
    }

    /// <summary>最近一次平滑后的速度（字节/秒）；样本不足时为 null。</summary>
    public double? BytesPerSecond => _bytesPerSecond;

    /// <summary>以内部时钟采样累计字节数。</summary>
    public double? Sample(long totalBytes) => Sample(totalBytes, _clock.Elapsed);

    /// <summary>以调用方提供的时间轴采样累计字节数，便于测试与多来源复用。</summary>
    public double? Sample(long totalBytes, TimeSpan timestamp)
    {
        if (totalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        }

        if (!_hasBaseline)
        {
            // 第一个样本只建立基线：没有经过的时间就无法计算速度。
            _lastBytes = totalBytes;
            _lastSampleTime = timestamp;
            _hasBaseline = true;
            return null;
        }

        var elapsed = timestamp - _lastSampleTime;
        if (elapsed <= TimeSpan.Zero)
        {
            return _bytesPerSecond;
        }

        if (elapsed < _minimumInterval)
        {
            return _bytesPerSecond;
        }

        var delta = totalBytes - _lastBytes;
        _lastBytes = totalBytes;
        _lastSampleTime = timestamp;

        if (delta <= 0)
        {
            // 暂停或校验阶段没有新增字节：不制造虚假速度，保留上一次结果直到下一次真实增量。
            return _bytesPerSecond;
        }

        var instant = delta / elapsed.TotalSeconds;
        _bytesPerSecond = _bytesPerSecond is null
            ? instant
            : (_smoothingFactor * instant) + ((1 - _smoothingFactor) * _bytesPerSecond.Value);
        return _bytesPerSecond;
    }

    public void Reset(long totalBytes = 0, TimeSpan? timestamp = null)
    {
        _lastBytes = totalBytes;
        _lastSampleTime = timestamp ?? _clock.Elapsed;
        _bytesPerSecond = null;
        _hasBaseline = true;
    }
}
