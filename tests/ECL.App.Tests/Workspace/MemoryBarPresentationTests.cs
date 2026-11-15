// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Workspace.ViewModels;
using Ecl.Core.Diagnostics;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 基本信息内存条（B2 步骤 3b）：系统已用、游戏预计占用与剩余空间按同一最大内存计算；
/// 超出容量时游戏段被截断并标记为红色，未知系统内存不画成 0。
/// </summary>
public sealed class MemoryBarPresentationTests
{
    private const long Mb = 1024L * 1024;

    private static MemoryPressureSample Sample(long totalMb, long availableMb) =>
        new(availableMb * Mb, totalMb * Mb, 0, 0);

    [Fact]
    public void UnknownSystemMemoryIsNotShownAsZero()
    {
        var bar = MemoryBarPresentationFactory.Create(sample: null, gameMb: 4096);

        Assert.False(bar.HasSystemMemory);
        Assert.Equal(MemoryBarPresentation.Unavailable, bar);
    }

    [Fact]
    public void SegmentsFollowUsedGameAndRemainingSpace()
    {
        var bar = MemoryBarPresentationFactory.Create(Sample(16384, 8192), gameMb: 4096);

        Assert.True(bar.HasSystemMemory);
        Assert.Equal(16384, bar.TotalMb);
        Assert.Equal(8192, bar.UsedMb);
        Assert.Equal(4096, bar.GameMb);
        Assert.False(bar.ExceedsCapacity);
        Assert.Equal(new System.Windows.GridLength(8192, System.Windows.GridUnitType.Star), bar.UsedWidth);
        Assert.Equal(new System.Windows.GridLength(4096, System.Windows.GridUnitType.Star), bar.GameWidth);
        Assert.Equal(new System.Windows.GridLength(4096, System.Windows.GridUnitType.Star), bar.FreeWidth);
    }

    [Fact]
    public void OverflowClampsTheGameSegmentAndMarksCapacityExceeded()
    {
        var bar = MemoryBarPresentationFactory.Create(Sample(16384, 8192), gameMb: 12288);

        Assert.True(bar.ExceedsCapacity);
        Assert.Equal(12288, bar.GameMb);
        // 已用 8192 后只剩 8192 可叠加，游戏段被截断；剩余段为 0。
        Assert.Equal(new System.Windows.GridLength(8192, System.Windows.GridUnitType.Star), bar.GameWidth);
        Assert.Equal(new System.Windows.GridLength(0, System.Windows.GridUnitType.Star), bar.FreeWidth);
    }

    [Fact]
    public void ExactFitLeavesNoRemainingSegment()
    {
        var bar = MemoryBarPresentationFactory.Create(Sample(16384, 8192), gameMb: 8192);

        Assert.False(bar.ExceedsCapacity);
        Assert.Equal(new System.Windows.GridLength(0, System.Windows.GridUnitType.Star), bar.FreeWidth);
        Assert.Equal(new System.Windows.GridLength(8192, System.Windows.GridUnitType.Star), bar.GameWidth);
    }

    [Fact]
    public void NegativeGameAllocationIsClampedToZero()
    {
        var bar = MemoryBarPresentationFactory.Create(Sample(16384, 8192), gameMb: -512);

        Assert.Equal(0, bar.GameMb);
        Assert.Equal(new System.Windows.GridLength(0, System.Windows.GridUnitType.Star), bar.GameWidth);
        Assert.Equal(new System.Windows.GridLength(8192, System.Windows.GridUnitType.Star), bar.FreeWidth);
    }

    [Fact]
    public void AvailableAboveTotalIsClamped()
    {
        var bar = MemoryBarPresentationFactory.Create(Sample(16384, 20000), gameMb: 1024);

        Assert.Equal(0, bar.UsedMb);
        Assert.False(bar.ExceedsCapacity);
        Assert.Equal(new System.Windows.GridLength(0, System.Windows.GridUnitType.Star), bar.UsedWidth);
    }
}
