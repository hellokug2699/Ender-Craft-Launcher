// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Storage;

public enum StorageGroup
{
    MinecraftInstance,
    EclOwn,
    Additions,
    Backups,
}

/// <summary>清理风险提示的视觉等级；橙/红都使用页面统一的“谨慎清理”文案。</summary>
public enum StorageRiskLevel
{
    None,
    Caution,
    High,
}

/// <summary>
/// 页面上的清理分类。只有可安全重建的缓存、临时文件、日志、崩溃报告、截图、
/// 服务器资源包缓存与存档文件夹进入本列表；模组、配置、资源包、光影包、数据包、
/// 投影、版本文件与基础运行文件只参与磁盘占用统计，不出现在清理列表里。
/// 顺序即页面顺序。
/// </summary>
public sealed record StorageCategorySpec(string Id, StorageGroup Group)
{
    public static readonly StorageCategorySpec[] All =
    [
        new("instance.logs", StorageGroup.MinecraftInstance),
        new("instance.crashReports", StorageGroup.MinecraftInstance),
        new("instance.saves", StorageGroup.MinecraftInstance),
        new("instance.screenshots", StorageGroup.MinecraftInstance),
        new("instance.serverResourcePacks", StorageGroup.MinecraftInstance),
        new("instance.runtimeCache", StorageGroup.MinecraftInstance),
        new("ecl.downloads", StorageGroup.EclOwn),
        new("ecl.installTemp", StorageGroup.EclOwn),
        new("ecl.updateLeftovers", StorageGroup.EclOwn),
        new("ecl.logs", StorageGroup.EclOwn),
        new("ecl.crashReports", StorageGroup.EclOwn),
        new("ecl.diagnostics", StorageGroup.EclOwn),
        new("ecl.imageCache", StorageGroup.EclOwn),
        new("ecl.metadataCache", StorageGroup.EclOwn),
        new("ecl.tempFiles", StorageGroup.EclOwn),
        // F-07：生命周期恢复材料（隔离区/退休区/迁移 staging/事务与快照状态）只展示占用，
        // 只能由生命周期恢复入口或专用事务清理，永远不进入普通清理。
        new("ecl.lifecycleMaterials", StorageGroup.EclOwn),
        new("ecl.instanceEngine", StorageGroup.EclOwn),
        // F-03 §4.4：已移除记录但保留在原位的游戏数据（RemoveRecordOnly 残留）。
        new("ecl.detachedGameData", StorageGroup.MinecraftInstance),
        new("additions.cache", StorageGroup.Additions),
        new("additions.temp", StorageGroup.Additions),
    ];
}

/// <summary>
/// 一个可呈现在清理列表中的文件或文件夹。大小未知时不写 0；不在白名单中的项
/// 仍可被列出，但 <see cref="CanClean"/> 为 false，执行层会再次拒绝删除。
/// 存档以世界文件夹为单位列出（<see cref="IsDirectory"/>），不拆分到 .mca 文件。
/// </summary>
public sealed record StorageFileEntry
{
    public required string Id { get; init; }

    public required string CategoryId { get; init; }

    public required StorageGroup Group { get; init; }

    public required string FullPath { get; init; }

    public required string AllowedRoot { get; init; }

    public string FileName => Path.GetFileName(FullPath.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar));

    /// <summary>true 表示这是一整个文件夹（当前只有世界存档）；删除时递归移除。</summary>
    public bool IsDirectory { get; init; }

    public long? Size { get; init; }

    public DateTimeOffset? LastWriteTimeUtc { get; init; }

    public string? InstanceId { get; init; }

    public string? InstanceName { get; init; }

    /// <summary>扩展名称、备份来源或实例名；没有归属时为空。</summary>
    public string? Owner { get; init; }

    public bool CanClean { get; init; }

    public string? ProtectionReason { get; init; }

    public StorageRiskLevel Risk { get; init; }

    public string? CleanupRule { get; init; }
}

public sealed record StorageCategory(
    string Id,
    StorageGroup Group,
    IReadOnlyList<StorageFileEntry> Entries,
    long KnownBytes,
    int KnownFileCount,
    int UnknownSizeCount,
    bool ListingTruncated);

public enum StorageDiskState
{
    Ready,
    Unknown,
    Unavailable,
}

public sealed record StorageDisk(
    string Root,
    string DisplayName,
    StorageDiskState State,
    long? TotalBytes,
    long? AvailableBytes,
    long KnownEclBytes,
    IReadOnlyDictionary<StorageGroup, long> EclBytesByGroup)
{
    public bool IsLowSpace => State == StorageDiskState.Ready &&
                              TotalBytes is > 0 &&
                              AvailableBytes is { } available &&
                              available < TotalBytes.Value * 0.10;
}

public sealed record StorageInventory(
    IReadOnlyList<StorageCategory> Categories,
    IReadOnlyList<StorageDisk> Disks,
    long KnownEclBytes,
    int UnavailableCount,
    int InstanceCount,
    bool IsComplete,
    DateTimeOffset ScannedAtUtc,
    IReadOnlyList<string> Warnings);

public enum StorageDeleteItemState
{
    Running,
    Succeeded,
    Failed,
    Skipped,
}

public sealed record StorageDeleteProgress(
    string EntryId,
    StorageDeleteItemState State,
    long Bytes,
    string? Reason);

public sealed record StorageDeleteItemResult(
    StorageFileEntry Entry,
    StorageDeleteItemState State,
    long Bytes,
    string? Reason);

public sealed record StorageDeleteResult(IReadOnlyList<StorageDeleteItemResult> Items)
{
    public int Succeeded => Items.Count(item => item.State == StorageDeleteItemState.Succeeded);

    public int Failed => Items.Count(item => item.State == StorageDeleteItemState.Failed);

    public int Skipped => Items.Count(item => item.State == StorageDeleteItemState.Skipped);

    public long BytesFreed => Items
        .Where(item => item.State == StorageDeleteItemState.Succeeded)
        .Sum(item => item.Bytes);
}
