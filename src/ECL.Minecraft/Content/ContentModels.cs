// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Content;

/// <summary>内容来源。</summary>
public enum ContentSource
{
    CurseForge,
    Modrinth,
}

/// <summary>下载页支持的资源类型。</summary>
public enum ContentKind
{
    Mod,
    ModPack,
    ResourcePack,
    ShaderPack,
    DataPack,
}

/// <summary>搜索结果来源过滤：全部 / 仅 CurseForge / 仅 Modrinth。</summary>
public enum ContentSourceFilter
{
    All,
    CurseForge,
    Modrinth,
}

public sealed record ContentSearchQuery
{
    public string? Query { get; init; }

    public ContentKind Kind { get; init; } = ContentKind.Mod;

    public ContentSourceFilter Source { get; init; } = ContentSourceFilter.All;

    /// <summary>游戏版本；为空表示不按版本过滤（没有实例时也可以搜索）。</summary>
    public string? GameVersion { get; init; }

    /// <summary>加载器；为空表示不过滤。资源包/光影包/数据包忽略该字段。</summary>
    public string? Loader { get; init; }

    public int PageSize { get; init; } = 20;

    public int Offset { get; init; }
}

/// <summary>两个平台统一后的搜索结果项。展示时保留来源徽标，不做跨平台去重。</summary>
public sealed record ContentHit(
    ContentSource Source,
    string Id,
    string? Slug,
    string Name,
    string Summary,
    string? Author,
    long DownloadCount,
    DateTimeOffset? UpdatedAt,
    string? WebsiteUrl,
    string? IconUrl,
    ContentKind Kind)
{
    public string SourceLabel => Source == ContentSource.CurseForge ? "CurseForge" : "Modrinth";

    /// <summary>用于列表去重的稳定键（仅在同一来源内唯一）。</summary>
    public string Key => $"{Source}:{Id}";
}

public sealed record ContentSearchResult(
    IReadOnlyList<ContentHit> Hits,
    int TotalCount,
    IReadOnlyDictionary<ContentSource, int> SourceTotals,
    IReadOnlyList<string> Warnings);

public sealed record ContentProgress(
    string Stage,
    string Message,
    int CompletedFiles = 0,
    int TotalFiles = 0)
{
    public double BytesPerSecond { get; init; }

    public long CompletedBytes { get; init; }

    public long TotalBytes { get; init; }

    public string? CurrentFile { get; init; }
}

public sealed record ContentInstallReport(
    string Name,
    ContentSource Source,
    ContentKind Kind,
    string? InstanceId,
    string? PrimaryFile,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Warnings);

public sealed record ContentDownloadReport(
    string Name,
    ContentKind Kind,
    string Path,
    long Size);

public sealed record ModpackImportResult(
    string Name,
    string? Version,
    int DownloadedFiles,
    int SkippedFiles,
    int OverrideFiles,
    IReadOnlyList<string> Warnings,
    string ArchivePath,
    string? InstanceId);

/// <summary>把统一结果映射到平台具体的 classId / project_type。</summary>
public static class ContentKinds
{
    public static string FolderName(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mods",
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.ShaderPack => "shaderpacks",
        ContentKind.DataPack => "datapacks",
        ContentKind.ModPack => "modpacks",
        _ => "content",
    };

    public static string DisplayKey(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "downloads.kind.mod",
        ContentKind.ModPack => "downloads.kind.modpack",
        ContentKind.ResourcePack => "downloads.kind.resourcepack",
        ContentKind.ShaderPack => "downloads.kind.shaderpack",
        ContentKind.DataPack => "downloads.kind.datapack",
        _ => "downloads.kind.mod",
    };

    public static int CurseForgeClassId(ContentKind kind) => kind switch
    {
        ContentKind.Mod => 6,
        ContentKind.ModPack => 4471,
        ContentKind.ResourcePack => 12,
        ContentKind.ShaderPack => 6552,
        ContentKind.DataPack => 6945,
        _ => 6,
    };

    public static bool UsesLoader(ContentKind kind) =>
        kind is ContentKind.Mod or ContentKind.ModPack;
}
