// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Shell.Output;
using Xunit;

namespace Ecl.App.Tests.Output;

public sealed class OutputBufferTests
{
    private static AppLogEntry Entry(int index) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(index), Microsoft.Extensions.Logging.LogLevel.Information, "Test", $"line-{index}");

    [Fact]
    public void PendingBufferDropsOldestAtCapacity()
    {
        var buffer = new OutputBuffer(capacity: 3);

        for (var index = 0; index < 5; index++)
        {
            buffer.Enqueue(Entry(index));
        }

        var drained = buffer.Drain();

        Assert.Equal(["line-2", "line-3", "line-4"], drained.Select(entry => entry.Message).ToArray());
        Assert.Equal(2, buffer.DroppedCount);
        Assert.False(buffer.HasPending);
    }

    [Fact]
    public void ClearRemovesPendingEntriesAndResetsDroppedCount()
    {
        var buffer = new OutputBuffer(capacity: 2);
        buffer.Enqueue(Entry(0));
        buffer.Enqueue(Entry(1));
        buffer.Enqueue(Entry(2));

        var dropped = buffer.Clear();

        Assert.Equal(1, dropped);
        Assert.Empty(buffer.Drain());
        Assert.Equal(0, buffer.DroppedCount);
        Assert.False(buffer.HasPending);
    }

    [Fact]
    public void DrainReturnsEmptyWhenNothingPending()
    {
        var buffer = new OutputBuffer(capacity: 2);

        Assert.Empty(buffer.Drain());
        Assert.False(buffer.HasPending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CapacityMustBePositive(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutputBuffer(capacity));
    }
}
