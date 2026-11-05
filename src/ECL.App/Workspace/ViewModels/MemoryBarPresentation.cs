// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using Ecl.Core.Diagnostics;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 基本信息「分配内存」的三段式内存条（`instance-workspace.md` §4.1）：
/// 蓝 = 系统已用、绿/红 = 游戏预计占用、灰 = 分配后的剩余空间；
/// 游戏预计占用叠加在可用内存区间上，不是独立的第三块内存池。
/// </summary>
public sealed record MemoryBarPresentation(
    bool HasSystemMemory,
    long TotalMb,
    long UsedMb,
    long GameMb,
    bool ExceedsCapacity,
    GridLength UsedWidth,
    GridLength GameWidth,
    GridLength FreeWidth)
{
    public static MemoryBarPresentation Unavailable { get; } = new(
        HasSystemMemory: false,
        TotalMb: 0,
        UsedMb: 0,
        GameMb: 0,
        ExceedsCapacity: false,
        UsedWidth: new GridLength(1, GridUnitType.Star),
        GameWidth: new GridLength(0),
        FreeWidth: new GridLength(0));
}

/// <summary>
/// 由系统内存采样与游戏分配值计算内存条比例。未知系统内存不当作 0，
/// 超出容量时把游戏占用段截断到剩余空间并用 <see cref="MemoryBarPresentation.ExceedsCapacity"/> 标记。
/// </summary>
public static class MemoryBarPresentationFactory
{
    private const long BytesPerMb = 1024L * 1024;

    public static MemoryBarPresentation Create(MemoryPressureSample? sample, long gameMb)
    {
        if (sample is null || sample.TotalPhysicalBytes <= 0)
        {
            return MemoryBarPresentation.Unavailable;
        }

        var totalMb = Math.Max(1, sample.TotalPhysicalBytes / BytesPerMb);
        var availableMb = Math.Clamp(sample.AvailablePhysicalBytes / BytesPerMb, 0, totalMb);
        var usedMb = totalMb - availableMb;
        var requestedMb = Math.Max(0, gameMb);

        var displayedGameMb = Math.Min(requestedMb, Math.Max(0, totalMb - usedMb));
        var freeMb = Math.Max(0, totalMb - usedMb - requestedMb);
        var exceeds = usedMb + requestedMb > totalMb;

        return new MemoryBarPresentation(
            HasSystemMemory: true,
            TotalMb: totalMb,
            UsedMb: usedMb,
            GameMb: requestedMb,
            ExceedsCapacity: exceeds,
            UsedWidth: new GridLength(usedMb, GridUnitType.Star),
            GameWidth: new GridLength(displayedGameMb, GridUnitType.Star),
            FreeWidth: new GridLength(freeMb, GridUnitType.Star));
    }
}
