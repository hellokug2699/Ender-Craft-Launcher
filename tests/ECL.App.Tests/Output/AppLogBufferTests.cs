// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Shell.Output;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecl.App.Tests.Output;

/// <summary>
/// 日志缓冲的输出代次契约：代次与条目提交原子捕获，只在“清空输出”时推进，
/// 并且推进时同时重置截断计数。
/// </summary>
public sealed class AppLogBufferTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task NotificationCarriesTheGenerationCapturedWithTheAppend()
    {
        using var notificationInFlight = new ManualResetEventSlim(false);
        using var resumeDelivery = new ManualResetEventSlim(false);

        var log = new AppLogBuffer();
        long? publishedGeneration = null;

        log.EntryWritten += (_, notification) =>
        {
            publishedGeneration = notification.Generation;
            notificationInFlight.Set();
            resumeDelivery.Wait(Timeout);
        };

        var producer = Task.Run(() => log.CreateLogger("Test").LogInformation("committed"));

        Assert.True(notificationInFlight.Wait(Timeout), "写入未进入通知阶段。");

        // 条目已经提交：此时推进代次必须让已提交的条目落在旧代次。
        var advanced = log.BeginOutputGeneration();

        resumeDelivery.Set();
        await producer.WaitAsync(Timeout);

        Assert.Equal(0, publishedGeneration);
        Assert.Equal(1, advanced);
        Assert.Equal(1, log.Generation);
    }

    [Fact]
    public void OnlyClearingAdvancesTheGeneration()
    {
        var log = new AppLogBuffer();
        var logger = log.CreateLogger("Test");

        Assert.Equal(0, log.Generation);

        for (var index = 0; index < AppLogBuffer.Capacity + 10; index++)
        {
            logger.LogInformation("buffer-{Index}", index);
        }

        Assert.Equal(0, log.Generation);
        Assert.Equal(10, log.DroppedCount);

        Assert.Equal(1, log.BeginOutputGeneration());
        Assert.Equal(1, log.Generation);
        Assert.Equal(0, log.DroppedCount);

        Assert.Equal(2, log.BeginOutputGeneration());
        Assert.Equal(2, log.Generation);
    }

    [Fact]
    public void DisposedBufferStopsPublishingAndRetainingEntries()
    {
        var log = new AppLogBuffer();
        var published = 0;
        log.EntryWritten += (_, _) => published++;

        log.CreateLogger("Test").LogInformation("before-dispose");
        Assert.Equal(1, published);

        log.Dispose();
        log.CreateLogger("Test").LogInformation("after-dispose");

        Assert.Equal(1, published);
        Assert.Single(log.Snapshot());
    }
}
