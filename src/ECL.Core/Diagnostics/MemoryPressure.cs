// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Diagnostics;

/// <summary>内存压力等级（ADR 0019 §5）。</summary>
public enum MemoryPressureLevel
{
    Normal,
    Elevated,
    Critical,
    Recovery,
}

/// <summary>一次压力采样：可用物理内存与提交余量，单位为字节。</summary>
public sealed record MemoryPressureSample(
    long AvailablePhysicalBytes,
    long TotalPhysicalBytes,
    long CommitHeadroomBytes,
    long CommitLimitBytes)
{
    public double Availability => TotalPhysicalBytes <= 0 ? 1 : (double)AvailablePhysicalBytes / TotalPhysicalBytes;

    public double CommitHeadroomRatio => CommitLimitBytes <= 0 ? 1 : (double)CommitHeadroomBytes / CommitLimitBytes;
}

/// <summary>
/// 带迟滞的压力状态机。阈值与保持时间由实现/实测确定，不在 ADR 中虚构；
/// 升级立即生效，降级需要持续观察，避免在 Elevated/Critical 之间抖动。
/// </summary>
public sealed class MemoryPressureStateMachine
{
    private readonly double _elevatedAvailability;
    private readonly double _criticalAvailability;
    private readonly double _elevatedCommit;
    private readonly double _criticalCommit;
    private readonly TimeSpan _recoveryHold;
    private readonly TimeSpan _normalHold;

    private DateTimeOffset _since;

    public MemoryPressureStateMachine(
        double elevatedAvailability = 0.15,
        double criticalAvailability = 0.07,
        double elevatedCommit = 0.10,
        double criticalCommit = 0.04,
        TimeSpan? recoveryHold = null,
        TimeSpan? normalHold = null)
    {
        _elevatedAvailability = elevatedAvailability;
        _criticalAvailability = criticalAvailability;
        _elevatedCommit = elevatedCommit;
        _criticalCommit = criticalCommit;
        _recoveryHold = recoveryHold ?? TimeSpan.FromSeconds(20);
        _normalHold = normalHold ?? TimeSpan.FromSeconds(30);
    }

    public MemoryPressureLevel Level { get; private set; } = MemoryPressureLevel.Normal;

    public MemoryPressureLevel Observe(DateTimeOffset now, MemoryPressureSample sample)
    {
        var raw = Classify(sample);
        if (raw > Level)
        {
            Level = raw;
            _since = now;
            return Level;
        }

        switch (Level)
        {
            case MemoryPressureLevel.Critical when raw < MemoryPressureLevel.Critical:
                Level = MemoryPressureLevel.Recovery;
                _since = now;
                return Level;

            case MemoryPressureLevel.Recovery:
                if (raw == MemoryPressureLevel.Critical)
                {
                    Level = MemoryPressureLevel.Critical;
                    _since = now;
                }
                else if (now - _since >= _recoveryHold)
                {
                    Level = raw == MemoryPressureLevel.Elevated
                        ? MemoryPressureLevel.Elevated
                        : MemoryPressureLevel.Normal;
                    _since = now;
                }

                return Level;

            case MemoryPressureLevel.Elevated when raw == MemoryPressureLevel.Normal:
                if (now - _since >= _normalHold)
                {
                    Level = MemoryPressureLevel.Normal;
                    _since = now;
                }

                return Level;
        }

        _since = now;
        return Level;
    }

    private MemoryPressureLevel Classify(MemoryPressureSample sample)
    {
        if (sample.TotalPhysicalBytes <= 0 && sample.CommitLimitBytes <= 0)
        {
            return MemoryPressureLevel.Normal;
        }

        var availability = sample.Availability;
        var commit = sample.CommitHeadroomRatio;

        if (availability < _criticalAvailability || commit < _criticalCommit)
        {
            return MemoryPressureLevel.Critical;
        }

        if (availability < _elevatedAvailability || commit < _elevatedCommit)
        {
            return MemoryPressureLevel.Elevated;
        }

        return MemoryPressureLevel.Normal;
    }
}
