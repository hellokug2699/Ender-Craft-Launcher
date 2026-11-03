// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Engine.Inventory;

public enum InventoryResourceKind
{
    Mod,
    ResourcePack,
    ShaderPack,
    World,
    Schematic,
    Config,
    CrashReport,
    Log,
}

public enum ResourceEnablement
{
    Enabled,
    Disabled,
    Unknown,
}

public enum DependencyState
{
    Satisfied,
    Missing,
    Unknown,
}

public enum CompatibilityState
{
    Compatible,
    Incompatible,
    Unknown,
}

/// <summary>
/// 模组依赖的静态判断。可选依赖缺失不算缺失；版本范围无法解析时标记 Unknown，不猜测。
/// </summary>
public sealed record ModDependencyInfo(
    string ModId,
    bool Mandatory,
    string? VersionRange,
    DependencyState State,
    string? Detail = null);

/// <summary>
/// Inventory 中的一个资源实体。Enablement、依赖、Minecraft/Loader 兼容性与运行证据各自独立，
/// 不允许用一个“可加载”布尔值覆盖未知状态（ADR 0042 §15）。
/// </summary>
public sealed record InventoryResource(
    string Id,
    InventoryResourceKind Kind,
    string RelativePath,
    long Size,
    DateTimeOffset ModifiedAt,
    string FileTypeId,
    ResourceEnablement Enablement,
    string? Name,
    string? Version,
    IReadOnlyList<ModDependencyInfo> Dependencies,
    CompatibilityState MinecraftCompatibility,
    CompatibilityState LoaderCompatibility,
    IReadOnlyList<EvidenceItem> Evidence,
    string? IconCachePath = null,
    string? IconSource = null,
    string? IconError = null,
    string? WorldName = null);

public sealed record InventoryTotals(
    int Files,
    int Directories,
    long TotalBytes,
    int Inaccessible,
    int Mods,
    int EnabledMods,
    int ResourcePacks,
    int ShaderPacks,
    int Worlds,
    int Schematics,
    int CrashReports,
    int ConfigFiles);

/// <summary>
/// 本地派生的实例资源清单。它是可重建缓存，不是实例内容的权威来源；扫描时间、规则版本和
/// 完成程度必须随结果一起暴露。
/// </summary>
public sealed record InstanceInventory(
    string SchemaVersion,
    string InstanceId,
    string GameDirectory,
    string MinecraftVersion,
    string Loader,
    string? LoaderVersion,
    DateTimeOffset ScannedAt,
    string ScannerVersion,
    string RulesVersion,
    InventoryTotals Totals,
    IReadOnlyList<InventoryResource> Resources,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<string> Warnings,
    bool IsComplete);

public sealed record InventoryScanOptions(
    bool IncludeIcons = true,
    bool IncludeDeepAnalysis = true,
    int MaxFiles = 200_000,
    int MaxResources = 20_000);

public sealed record InventoryScanProgress(
    string Stage,
    int Processed,
    int Total,
    string? CurrentItem = null);

public interface IInstanceInventoryService
{
    Task<Result<InstanceInventory>> ScanAsync(
        string instanceId,
        InventoryScanOptions? options = null,
        IProgress<InventoryScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>读取缓存；实例配置或扫描器版本变化时返回 null，而不是返回过期结论。</summary>
    Task<Result<InstanceInventory?>> GetCachedAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    Task<Result> InvalidateAsync(string instanceId, CancellationToken cancellationToken = default);
}
