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
            buffer.Enqueue(Entry(index), generation: 0);
        }

        var drained = buffer.Drain(generationFloor: 0);

        Assert.Equal(["line-2", "line-3", "line-4"], drained.Select(entry => entry.Message).ToArray());
        Assert.Equal(2, buffer.DroppedCount);
        Assert.False(buffer.HasPending);
    }

    [Fact]
    public void DrainDiscardsEntriesOlderThanTheGenerationFloor()
    {
        var buffer = new OutputBuffer(capacity: 4);
        buffer.Enqueue(Entry(0), generation: 0);
        buffer.Enqueue(Entry(1), generation: 1);
        buffer.Enqueue(Entry(2), generation: 0);
        buffer.Enqueue(Entry(3), generation: 2);

        var drained = buffer.Drain(generationFloor: 1);

        // 旧代次条目被丢弃，但保持剩余条目的原始顺序。
        Assert.Equal(["line-1", "line-3"], drained.Select(entry => entry.Message).ToArray());
        Assert.False(buffer.HasPending);

        // 过期条目属于用户已经清空的内容，不计入截断数。
        Assert.Equal(0, buffer.DroppedCount);
    }

    [Fact]
    public void GenerationFloorKeepsBoundedCapacityAndDroppedAccounting()
    {
        var buffer = new OutputBuffer(capacity: 3);

        for (var index = 0; index < 3; index++)
        {
            buffer.Enqueue(Entry(index), generation: 0);
        }

        // 旧代次条目全部被丢弃，且不计入截断数。
        Assert.Empty(buffer.Drain(generationFloor: 1));
        Assert.Equal(0, buffer.DroppedCount);
        Assert.False(buffer.HasPending);

        // 新代次继续写入时容量边界与丢弃计数照常生效。
        for (var index = 3; index < 7; index++)
        {
            buffer.Enqueue(Entry(index), generation: 1);
        }

        Assert.Equal(["line-4", "line-5", "line-6"], buffer.Drain(generationFloor: 1).Select(entry => entry.Message).ToArray());
        Assert.Equal(1, buffer.DroppedCount);
        Assert.False(buffer.HasPending);
    }

    [Fact]
    public void ClearRemovesPendingEntriesAndResetsDroppedCount()
    {
        var buffer = new OutputBuffer(capacity: 2);
        buffer.Enqueue(Entry(0), generation: 0);
        buffer.Enqueue(Entry(1), generation: 0);
        buffer.Enqueue(Entry(2), generation: 0);

        var dropped = buffer.Clear();

        Assert.Equal(1, dropped);
        Assert.Empty(buffer.Drain(generationFloor: 0));
        Assert.Equal(0, buffer.DroppedCount);
        Assert.False(buffer.HasPending);
    }

    [Fact]
    public void DrainReturnsEmptyWhenNothingPending()
    {
        var buffer = new OutputBuffer(capacity: 2);

        Assert.Empty(buffer.Drain(generationFloor: 0));
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
