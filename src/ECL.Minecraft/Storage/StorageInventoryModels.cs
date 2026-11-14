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

/// <summary>固定预设分类（storage-management.md §6）；顺序即页面顺序。</summary>
public sealed record StorageCategorySpec(string Id, StorageGroup Group)
{
    public static readonly StorageCategorySpec[] All =
    [
        new("instance.logs", StorageGroup.MinecraftInstance),
        new("instance.crashReports", StorageGroup.MinecraftInstance),
        new("instance.saves", StorageGroup.MinecraftInstance),
        new("instance.mods", StorageGroup.MinecraftInstance),
        new("instance.modConfig", StorageGroup.MinecraftInstance),
        new("instance.resourcePacks", StorageGroup.MinecraftInstance),
        new("instance.shaderPacks", StorageGroup.MinecraftInstance),
        new("instance.dataPacks", StorageGroup.MinecraftInstance),
        new("instance.schematics", StorageGroup.MinecraftInstance),
        new("instance.screenshots", StorageGroup.MinecraftInstance),
        new("instance.serverResourcePacks", StorageGroup.MinecraftInstance),
        new("instance.versionFiles", StorageGroup.MinecraftInstance),
        new("instance.runtimeCache", StorageGroup.MinecraftInstance),
        new("instance.other", StorageGroup.MinecraftInstance),
        new("ecl.downloads", StorageGroup.EclOwn),
        new("ecl.installTemp", StorageGroup.EclOwn),
        new("ecl.updateLeftovers", StorageGroup.EclOwn),
        new("ecl.logs", StorageGroup.EclOwn),
        new("ecl.crashReports", StorageGroup.EclOwn),
        new("ecl.diagnostics", StorageGroup.EclOwn),
        new("ecl.imageCache", StorageGroup.EclOwn),
        new("ecl.metadataCache", StorageGroup.EclOwn),
        new("ecl.tempFiles", StorageGroup.EclOwn),
        new("ecl.other", StorageGroup.EclOwn),
        new("additions.cache", StorageGroup.Additions),
        new("additions.database", StorageGroup.Additions),
        new("additions.temp", StorageGroup.Additions),
        new("additions.other", StorageGroup.Additions),
        new("backups.instance", StorageGroup.Backups),
        new("backups.world", StorageGroup.Backups),
        new("backups.config", StorageGroup.Backups),
        new("backups.other", StorageGroup.Backups),
    ];
}

/// <summary>
/// 一个可呈现在分类表格中的文件。大小未知时不写 0；不在白名单中的项
/// 仍可被列出，但 <see cref="CanClean"/> 为 false，执行层会再次拒绝删除。
/// </summary>
public sealed record StorageFileEntry
{
    public required string Id { get; init; }

    public required string CategoryId { get; init; }

    public required StorageGroup Group { get; init; }

    public required string FullPath { get; init; }

    public required string AllowedRoot { get; init; }

    public string FileName => Path.GetFileName(FullPath);

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
