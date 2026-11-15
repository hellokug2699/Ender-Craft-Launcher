// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Storage;

namespace Ecl.App.ViewModels.Storage;

public sealed record StorageSegmentViewModel(
    string Label,
    string BrushKey,
    long Bytes,
    double Width,
    string SizeText);

/// <summary>
/// 顶部磁盘圆形组件与空间分布条。容量/剩余未知时不伪造 0 或正常颜色判定。
/// </summary>
public sealed partial class StorageDiskViewModel : ObservableObject
{
    private const double BarWidth = 260;

    private readonly ILocalizer _localizer;
    private readonly Action _selectionChanged;

    public StorageDiskViewModel(StorageDisk disk, ILocalizer localizer, Action selectionChanged)
    {
        Disk = disk;
        _localizer = localizer;
        _selectionChanged = selectionChanged;
        DisplayName = disk.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        TotalText = disk.TotalBytes is { } total
            ? StorageSizeFormatter.FormatBytes(total)
            : _localizer.Get("storage.disk.unknown");
        AvailableText = disk.AvailableBytes is { } available
            ? _localizer.Get("storage.disk.available", StorageSizeFormatter.FormatBytes(available))
            : _localizer.Get("storage.disk.state." + disk.State.ToString().ToLowerInvariant());
        RingState = disk.State != StorageDiskState.Ready || disk.TotalBytes is null || disk.AvailableBytes is null
            ? "unknown"
            : disk.IsLowSpace
                ? "low"
                : "normal";
        Segments = BuildSegments(disk);
    }

    public StorageDisk Disk { get; }

    public string Root => Disk.Root;

    public string DisplayName { get; }

    public string TotalText { get; }

    public string AvailableText { get; }

    public string RingState { get; }

    public IReadOnlyList<StorageSegmentViewModel> Segments { get; }

    [ObservableProperty]
    private bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();

    private IReadOnlyList<StorageSegmentViewModel> BuildSegments(StorageDisk disk)
    {
        if (disk.State != StorageDiskState.Ready ||
            disk.TotalBytes is not { } total ||
            disk.AvailableBytes is not { } available)
        {
            return [];
        }

        var used = Math.Max(0, total - available);
        if (used == 0)
        {
            return [];
        }

        var segments = new List<StorageSegmentViewModel>();
        AddGroup(segments, disk, StorageGroup.MinecraftInstance, "StorageMinecraftBrush", used);
        AddGroup(segments, disk, StorageGroup.EclOwn, "StorageEclBrush", used);
        AddGroup(segments, disk, StorageGroup.Additions, "StorageAdditionsBrush", used);
        AddGroup(segments, disk, StorageGroup.Backups, "StorageBackupsBrush", used);

        var known = disk.KnownEclBytes;
        var nonEcl = Math.Max(0, used - known);
        if (nonEcl > 0)
        {
            segments.Add(new StorageSegmentViewModel(
                _localizer.Get("storage.group.nonEcl"),
                "StorageNonEclBrush",
                nonEcl,
                Math.Max(1, nonEcl / (double)used * BarWidth),
                StorageSizeFormatter.FormatBytes(nonEcl)));
        }

        return segments;
    }

    private void AddGroup(
        List<StorageSegmentViewModel> segments,
        StorageDisk disk,
        StorageGroup group,
        string brushKey,
        long used)
    {
        var bytes = disk.EclBytesByGroup.GetValueOrDefault(group);
        if (bytes <= 0)
        {
            return;
        }

        segments.Add(new StorageSegmentViewModel(
            _localizer.Get("storage.group." + group),
            brushKey,
            bytes,
            Math.Max(1, bytes / (double)used * BarWidth),
            StorageSizeFormatter.FormatBytes(bytes)));
    }
}
