// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Download;

/// <summary>
/// 下载限速（settings-downloads.md §4/§6）。按字节令牌桶计算需要等待的时间，
/// 由下载循环在两次读取之间等待。一个批次共用一个实例，因此并发下载共享同一上限
/// （不会出现“每文件各占一份限速”）。
///
/// 令牌按真实流逝时间补充，但调用方只有在 <see cref="Reserve"/> 返回延迟并等待后
/// 才真正放慢；因此该类型可以在测试中注入单调时间源做确定性验证。
/// </summary>
public sealed class DownloadRateLimiter
{
    /// <summary>允许的突发字节数，避免每个 80 KB 分块都被强行拖慢。</summary>
    public const long DefaultBurstBytes = 256 * 1024;

    private const int BytesPerMegabit = 125_000;

    private readonly long _bytesPerSecond;
    private readonly long _burstBytes;
    private readonly Func<long> _timestamp;
    private readonly object _gate = new();
    private long _tokens;
    private long _lastTimestamp;

    public DownloadRateLimiter(
        long bytesPerSecond,
        long burstBytes = DefaultBurstBytes,
        Func<long>? monotonicMilliseconds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(burstBytes);

        _bytesPerSecond = bytesPerSecond;
        _burstBytes = burstBytes;
        _tokens = burstBytes;
        _timestamp = monotonicMilliseconds ?? (() => Environment.TickCount64);
        _lastTimestamp = _timestamp();
    }

    /// <summary>0 或负数表示不限制；页面与引擎都把它当作“未启用”。</summary>
    public static DownloadRateLimiter? Create(int megabitsPerSecond) =>
        megabitsPerSecond <= 0
            ? null
            : new DownloadRateLimiter(megabitsPerSecond * (long)BytesPerMegabit);

    public long BytesPerSecond => _bytesPerSecond;

    /// <summary>为即将读取的字节预留带宽；返回调用方需要等待的时间。</summary>
    public TimeSpan Reserve(int bytes)
    {
        if (bytes <= 0)
        {
            return TimeSpan.Zero;
        }

        lock (_gate)
        {
            var now = _timestamp();
            var elapsed = now - _lastTimestamp;
            _lastTimestamp = now;

            if (elapsed > 0)
            {
                var refill = _bytesPerSecond * elapsed / 1000;
                _tokens = Math.Min(_burstBytes, _tokens + refill);
            }

            if (_tokens >= bytes)
            {
                _tokens -= bytes;
                return TimeSpan.Zero;
            }

            var deficit = bytes - _tokens;
            _tokens = 0;
            return TimeSpan.FromSeconds((double)deficit / _bytesPerSecond);
        }
    }
}
