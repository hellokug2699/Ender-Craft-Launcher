// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.CurseForge;

/// <summary>CurseForge Minecraft 游戏 ID。</summary>
public static class CurseForgeGame
{
    public const int Minecraft = 432;
}

/// <summary>CurseForge 项目分类 ID。</summary>
public static class CurseForgeClassIds
{
    public const int Mod = 6;
    public const int Modpack = 4471;
}

/// <summary>与 CurseForge API 的 modLoaderType 数值一致。</summary>
public enum CurseForgeModLoader
{
    Forge = 1,
    Fabric = 4,
    Quilt = 5,
    NeoForge = 6,
}

/// <summary>与 CurseForge API 的 ModsSearchSortField 数值一致。</summary>
public enum CurseForgeSortField
{
    Featured = 1,
    Popularity = 2,
    LastUpdated = 3,
    Name = 4,
    Author = 5,
    TotalDownloads = 6,
    Category = 7,
    GameVersion = 8,
}

public enum CurseForgeKeySource
{
    /// <summary>未知状态；只用于测试替身模拟“客户端不认为已就绪”。</summary>
    None,

    /// <summary>API key 由 ECL 代理服务端注入，客户端不持有 key。</summary>
    Proxy,
}

public sealed record CurseForgeStatus(CurseForgeKeySource Source)
{
    public bool IsConfigured => Source != CurseForgeKeySource.None;
}

public sealed record CurseForgePagination
{
    public int Index { get; init; }

    public int PageSize { get; init; }

    public int ResultCount { get; init; }

    public int TotalCount { get; init; }
}

public sealed record CurseForgeModLinks
{
    public string? WebsiteUrl { get; init; }

    public string? WikiUrl { get; init; }

    public string? IssuesUrl { get; init; }

    public string? SourceUrl { get; init; }
}

public sealed record CurseForgeModAsset
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? ThumbnailUrl { get; init; }

    public string? Url { get; init; }
}

public sealed record CurseForgeFileIndex
{
    public string? GameVersion { get; init; }

    public int FileId { get; init; }

    public string? Filename { get; init; }

    /// <summary>1=Release，2=Beta，3=Alpha。</summary>
    public int ReleaseType { get; init; }

    public int ModLoader { get; init; }
}

public sealed record CurseForgeMod
{
    public int Id { get; init; }

    public string Slug { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Summary { get; init; }

    public long DownloadCount { get; init; }

    public DateTimeOffset? DateModified { get; init; }

    public int ClassId { get; init; }

    public bool? AllowModDistribution { get; init; }

    public CurseForgeModLinks? Links { get; init; }

    public CurseForgeModAsset? Logo { get; init; }

    public IReadOnlyList<CurseForgeFileIndex> LatestFilesIndexes { get; init; } = [];

    public string? WebsiteUrl => Links?.WebsiteUrl;

    public string? IconUrl => Logo?.ThumbnailUrl ?? Logo?.Url;
}

public sealed record CurseForgeFileHash
{
    /// <summary>1=SHA1，2=MD5。</summary>
    public int Algo { get; init; }

    public string? Value { get; init; }
}

public sealed record CurseForgeFileDependency
{
    public int ModId { get; init; }

    /// <summary>1=Embedded，2=Optional，3=Required，4=Tool，5=Incompatible，6=Include。</summary>
    public int RelationType { get; init; }
}

public sealed record CurseForgeFile
{
    public int Id { get; init; }

    public int ModId { get; init; }

    public string? DisplayName { get; init; }

    public string FileName { get; init; } = string.Empty;

    /// <summary>1=Release，2=Beta，3=Alpha。</summary>
    public int ReleaseType { get; init; }

    public DateTimeOffset? FileDate { get; init; }

    public long FileLength { get; init; }

    public string? DownloadUrl { get; init; }

    public IReadOnlyList<string> GameVersions { get; init; } = [];

    public IReadOnlyList<CurseForgeFileHash> Hashes { get; init; } = [];

    public IReadOnlyList<CurseForgeFileDependency> Dependencies { get; init; } = [];

    public string? Sha1 => Hashes
        .FirstOrDefault(hash => hash.Algo == 1 && !string.IsNullOrWhiteSpace(hash.Value))
        ?.Value;

    public bool SupportsGameVersion(string gameVersion) =>
        GameVersions.Any(version => string.Equals(version, gameVersion, StringComparison.OrdinalIgnoreCase));

    public bool SupportsLoader(CurseForgeModLoader loader)
    {
        var expected = loader switch
        {
            CurseForgeModLoader.Forge => "Forge",
            CurseForgeModLoader.Fabric => "Fabric",
            CurseForgeModLoader.Quilt => "Quilt",
            CurseForgeModLoader.NeoForge => "NeoForge",
            _ => null,
        };

        if (expected is null)
        {
            return true;
        }

        string[] knownLoaders = ["Forge", "Fabric", "Quilt", "NeoForge"];
        var declared = GameVersions
            .Where(version => knownLoaders.Contains(version, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        // 少数文件不声明加载器；此时不阻止安装，由实例加载器自行判断。
        return declared.Length == 0 ||
               declared.Any(version => string.Equals(version, expected, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record CurseForgeSearchQuery
{
    public string? SearchFilter { get; init; }

    /// <summary>按 slug 精确搜索；与 SearchFilter 二选一。</summary>
    public string? Slug { get; init; }

    public string? GameVersion { get; init; }

    public CurseForgeModLoader? Loader { get; init; }

    public int ClassId { get; init; } = CurseForgeClassIds.Mod;

    public int Index { get; init; }

    public int PageSize { get; init; } = 20;

    public CurseForgeSortField SortField { get; init; } = CurseForgeSortField.Popularity;

    public bool Descending { get; init; } = true;
}

public sealed record CurseForgeSearchResult(
    IReadOnlyList<CurseForgeMod> Mods,
    int TotalCount,
    int Index,
    int PageSize);

public sealed record CurseForgeProgress(
    string Stage,
    string Message,
    int Completed = 0,
    int Total = 0)
{
    public double BytesPerSecond { get; init; }

    public long CompletedBytes { get; init; }

    public long TotalBytes { get; init; }

    public string? CurrentFile { get; init; }
}

public sealed record CurseForgeInstallReport(
    string ModName,
    int ProjectId,
    int FileId,
    string FileName,
    IReadOnlyList<string> InstalledFiles,
    IReadOnlyList<string> Warnings);

public static class CurseForgeLoaders
{
    public static CurseForgeModLoader? FromInstanceLoader(string? loader) =>
        loader?.Trim().ToLowerInvariant() switch
        {
            "fabric" => CurseForgeModLoader.Fabric,
            "forge" => CurseForgeModLoader.Forge,
            "cleanroom" => CurseForgeModLoader.Forge,
            "neoforge" => CurseForgeModLoader.NeoForge,
            _ => null,
        };

    public static bool TryParse(string? value, out CurseForgeModLoader loader)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "forge":
            case "cleanroom":
                loader = CurseForgeModLoader.Forge;
                return true;
            case "fabric":
                loader = CurseForgeModLoader.Fabric;
                return true;
            case "neoforge":
                loader = CurseForgeModLoader.NeoForge;
                return true;
            case "quilt":
                loader = CurseForgeModLoader.Quilt;
                return true;
            default:
                loader = default;
                return false;
        }
    }

    public static string DisplayName(CurseForgeModLoader loader) => loader switch
    {
        CurseForgeModLoader.Forge => "Forge",
        CurseForgeModLoader.Fabric => "Fabric",
        CurseForgeModLoader.Quilt => "Quilt",
        CurseForgeModLoader.NeoForge => "NeoForge",
        _ => loader.ToString(),
    };
}
