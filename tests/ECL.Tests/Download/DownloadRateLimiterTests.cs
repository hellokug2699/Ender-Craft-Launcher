// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Download;
using Xunit;

namespace Ecl.Tests.Download;

/// <summary>
/// 下载限速的令牌桶：突发额度内不等待；超出部分按真实速率折算等待时间；
/// 流逝时间会补充令牌。实例由批次共享，保证并发下载共享同一上限。
/// </summary>
public sealed class DownloadRateLimiterTests
{
    [Fact]
    public void BytesWithinTheBurstAreNotDelayed()
    {
        var now = 0L;
        var limiter = new DownloadRateLimiter(100_000, burstBytes: 10_000, () => now);

        Assert.Equal(TimeSpan.Zero, limiter.Reserve(4_000));
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(6_000));

        // 突发额度用尽后，下一块需要等待。
        Assert.True(limiter.Reserve(1_000) > TimeSpan.Zero);
    }

    [Fact]
    public void BytesBeyondTheBurstArePacedAtTheConfiguredRate()
    {
        var now = 0L;
        var limiter = new DownloadRateLimiter(100_000, burstBytes: 0, () => now);

        // 100 KB/s：每次 10 KB 需要 0.1 秒。
        Assert.Equal(TimeSpan.FromMilliseconds(100), limiter.Reserve(10_000));
        Assert.Equal(TimeSpan.FromMilliseconds(200), limiter.Reserve(20_000));
    }

    [Fact]
    public void ElapsedTimeRefillsTokensUpToTheBurst()
    {
        var now = 0L;
        var limiter = new DownloadRateLimiter(100_000, burstBytes: 10_000, () => now);

        Assert.Equal(TimeSpan.Zero, limiter.Reserve(10_000));

        // 流逝 50 ms → 补充 5 KB。
        now = 50;
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(5_000));
        Assert.True(limiter.Reserve(1) > TimeSpan.Zero);

        // 长时间空闲也只补到突发上限。
        now = 10_000;
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(10_000));
        Assert.True(limiter.Reserve(1) > TimeSpan.Zero);
    }

    [Fact]
    public void ZeroOrNegativeLimitMeansUnlimited()
    {
        Assert.Null(DownloadRateLimiter.Create(0));
        Assert.Null(DownloadRateLimiter.Create(-1));
        Assert.NotNull(DownloadRateLimiter.Create(8));
        Assert.Equal(8 * 125_000L, DownloadRateLimiter.Create(8)!.BytesPerSecond);
    }
}
