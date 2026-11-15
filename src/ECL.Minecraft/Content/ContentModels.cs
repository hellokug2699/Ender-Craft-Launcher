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

    /// <summary>
    /// 搜索结果声明的 Minecraft 版本（CurseForge 取 LatestFilesIndexes，Modrinth 取搜索命中的 versions）。
    /// 只用于列表展示“版本范围”；来源没有提供时保持空集合，不得猜测。
    /// </summary>
    public IReadOnlyList<string> GameVersions { get; init; } = [];

    /// <summary>搜索结果声明的加载器（CurseForge 的 modLoader 数值 / Modrinth 的 categories）。</summary>
    public IReadOnlyList<string> Loaders { get; init; } = [];

    /// <summary>Modrinth 分类标签（例如 shader 的 iris / optifine）；CurseForge 搜索不提供，保持空集合。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// 列表展示用的“主要下载文件大小”。搜索响应本身不带大小：ECL 在搜索后额外做一次
    /// 批量文件/版本查询得到真实字节数；查询失败时保持 null（页面显示「大小未知」），
    /// 不按经验估算、也不显示 0。
    /// </summary>
    public long? FileSize { get; init; }
}

/// <summary>
/// 某来源上一个可下载的具体版本。版本列表一律来自来源 API 的真实字段；
/// 缺失的发布说明 / 大小保持 null，由页面显示「大小未知」，不填 0 或估值。
/// </summary>
public sealed record ContentVersion(
    ContentSource Source,
    string ProjectId,
    string VersionId,
    string VersionName,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    string FileName,
    long? FileSize,
    DateTimeOffset? PublishedAt)
{
    public string SourceLabel => Source == ContentSource.CurseForge ? "CurseForge" : "Modrinth";

    /// <summary>
    /// 是否声明支持指定 Minecraft 版本。没有版本声明时返回 false（未知不等于兼容），
    /// 由调用方决定如何展示“未知”。
    /// </summary>
    public bool MatchesGameVersion(string? gameVersion) =>
        !string.IsNullOrWhiteSpace(gameVersion) &&
        GameVersions.Any(version => string.Equals(version, gameVersion, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 是否声明支持指定加载器。资源包 / 光影包 / 数据包不按加载器筛选，
    /// 由调用方在进入这里之前跳过；Cleanroom 在来源侧对应 Forge。
    /// </summary>
    public bool MatchesLoader(string? loader)
    {
        if (string.IsNullOrWhiteSpace(loader))
        {
            return true;
        }

        var expected = loader.Trim().ToLowerInvariant() switch
        {
            "cleanroom" => "forge",
            var value => value,
        };
        return Loaders.Any(item => string.Equals(item, expected, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasFileSize => FileSize is > 0;
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

/// <summary>
/// 整合包部署前的准备结果：归档已落盘、清单已解析（`download-instance-versions.md` §5.2/§5.4）。
/// 页面只用这里的真实信息装填锁定区域，不按来源搜索元数据或经验值补默认配置。
/// </summary>
public sealed record ModpackPreparation(
    string ArchivePath,
    ModpackArchiveInspector.ModpackMetadata Metadata,
    long ArchiveSize);

/// <summary>
/// 新建实例并部署整合包的请求。配置全部来自整合包清单；页面只提供实例名称，
/// 安装位置仍由新建实例的既有固定规则决定（主工作区）。
/// </summary>
public sealed record ModpackDeploymentRequest(
    string InstanceName,
    string ArchivePath,
    string GameVersion,
    string Loader,
    string? LoaderVersion);

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
