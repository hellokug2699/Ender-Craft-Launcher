// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Operations;
using Xunit;

namespace Ecl.Tests.Operations;

public sealed class SpeedMeterTests
{
    [Fact]
    public void FirstSampleOnlyEstablishesTheBaseline()
    {
        var meter = new SpeedMeter(minimumInterval: TimeSpan.FromMilliseconds(100));

        Assert.Null(meter.Sample(0, TimeSpan.Zero));
        Assert.Null(meter.BytesPerSecond);
    }

    [Fact]
    public void UsesSmoothedInstantaneousSpeed()
    {
        var meter = new SpeedMeter(smoothingFactor: 0.3, minimumInterval: TimeSpan.FromMilliseconds(100));

        meter.Sample(0, TimeSpan.Zero);
        Assert.Equal(1_000d, meter.Sample(1_000, TimeSpan.FromSeconds(1))!.Value, precision: 3);

        // 第二个窗口瞬时速度 3 MB/s，平滑后为 0.3 * 3 + 0.7 * 1 = 1.6。
        Assert.Equal(1_600d, meter.Sample(4_000, TimeSpan.FromSeconds(2))!.Value, precision: 3);
    }

    [Fact]
    public void ThrottlesSamplesInsideTheMinimumInterval()
    {
        var meter = new SpeedMeter(smoothingFactor: 1, minimumInterval: TimeSpan.FromMilliseconds(200));
        meter.Sample(0, TimeSpan.Zero);
        meter.Sample(1_000, TimeSpan.FromSeconds(1));

        var before = meter.BytesPerSecond;
        var during = meter.Sample(1_500, TimeSpan.FromMilliseconds(1_100));

        Assert.NotNull(before);
        Assert.Equal(before!.Value, during!.Value, precision: 6);
    }

    [Fact]
    public void DoesNotInventSpeedWhenBytesDoNotGrow()
    {
        var meter = new SpeedMeter(smoothingFactor: 1, minimumInterval: TimeSpan.FromMilliseconds(100));
        meter.Sample(0, TimeSpan.Zero);
        meter.Sample(1_000, TimeSpan.FromSeconds(1));

        Assert.Equal(1_000d, meter.Sample(1_000, TimeSpan.FromSeconds(2))!.Value, precision: 3);
    }

    [Fact]
    public void ResetDropsPreviousSpeed()
    {
        var meter = new SpeedMeter(smoothingFactor: 1, minimumInterval: TimeSpan.FromMilliseconds(100));
        meter.Sample(0, TimeSpan.Zero);
        meter.Sample(1_000, TimeSpan.FromSeconds(1));
        Assert.NotNull(meter.BytesPerSecond);

        meter.Reset(5_000, TimeSpan.FromSeconds(2));
        Assert.Null(meter.BytesPerSecond);
    }

    [Fact]
    public void RejectsInvalidSmoothingFactor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeedMeter(smoothingFactor: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeedMeter(smoothingFactor: 1.5));
    }
}
