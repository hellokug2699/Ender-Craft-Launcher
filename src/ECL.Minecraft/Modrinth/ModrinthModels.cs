// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace Ecl.Minecraft.Modrinth;

/// <summary>Modrinth 的项目类型。数值与 API 的 <c>project_type</c> 文本对应。</summary>
public enum ModrinthProjectType
{
    Mod,
    ModPack,
    ResourcePack,
    Shader,
    DataPack,
    Unknown,
}

public sealed record ModrinthSearchQuery
{
    public string? Query { get; init; }

    public ModrinthProjectType? ProjectType { get; init; }

    public string? GameVersion { get; init; }

    /// <summary>Modrinth 把加载器放在 categories 中，例如 fabric、forge、neoforge。</summary>
    public string? Loader { get; init; }

    public int Offset { get; init; }

    public int PageSize { get; init; } = 20;

    /// <summary>relevance、downloads、follows、newest、updated。</summary>
    public string Sort { get; init; } = "relevance";
}

public sealed record ModrinthSearchHit
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    public string? Slug { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string? Author { get; init; }

    [JsonPropertyName("project_type")]
    public string? ProjectType { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    public IReadOnlyList<string> Versions { get; init; } = [];

    public long Downloads { get; init; }

    public long Follows { get; init; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }

    [JsonPropertyName("date_modified")]
    public DateTimeOffset? DateModified { get; init; }

    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; init; }

    public ModrinthProjectType MappedProjectType => ModrinthApi.ParseProjectType(ProjectType);

    public string? WebsiteUrl => Slug is { Length: > 0 }
        ? $"https://modrinth.com/{ModrinthApi.ProjectTypePath(MappedProjectType)}/{Slug}"
        : null;
}

public sealed record ModrinthSearchResult(
    IReadOnlyList<ModrinthSearchHit> Hits,
    int Offset,
    int Limit,
    int TotalHits);

public sealed record ModrinthProject
{
    public string Id { get; init; } = string.Empty;

    public string? Slug { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("project_type")]
    public string? ProjectType { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    public IReadOnlyList<string> Loaders { get; init; } = [];

    [JsonPropertyName("game_versions")]
    public IReadOnlyList<string> GameVersions { get; init; } = [];

    public long Downloads { get; init; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }

    public string? IssuesUrl { get; init; }

    public string? SourceUrl { get; init; }

    public ModrinthProjectType MappedProjectType => ModrinthApi.ParseProjectType(ProjectType);
}

public sealed record ModrinthFileHashes
{
    public string? Sha1 { get; init; }

    public string? Sha512 { get; init; }
}

public sealed record ModrinthFile
{
    public ModrinthFileHashes Hashes { get; init; } = new();

    public string Url { get; init; } = string.Empty;

    public string Filename { get; init; } = string.Empty;

    public bool Primary { get; init; }

    public long Size { get; init; }

    /// <summary>required-resource-pack / optional-resource-pack / sources-jar / dev-jar …</summary>
    [JsonPropertyName("file_type")]
    public string? FileType { get; init; }
}

public sealed record ModrinthDependency
{
    [JsonPropertyName("version_id")]
    public string? VersionId { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; init; }

    [JsonPropertyName("dependency_type")]
    public string? DependencyType { get; init; }
}

public sealed record ModrinthVersion
{
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    public string? Name { get; init; }

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; init; } = string.Empty;

    [JsonPropertyName("version_type")]
    public string VersionType { get; init; } = "release";

    [JsonPropertyName("game_versions")]
    public IReadOnlyList<string> GameVersions { get; init; } = [];

    public IReadOnlyList<string> Loaders { get; init; } = [];

    public IReadOnlyList<ModrinthDependency> Dependencies { get; init; } = [];

    public IReadOnlyList<ModrinthFile> Files { get; init; } = [];

    [JsonPropertyName("date_published")]
    public DateTimeOffset? DatePublished { get; init; }

    public long Downloads { get; init; }

    public ModrinthFile? PrimaryFile =>
        Files.FirstOrDefault(file => file.Primary) ?? Files.FirstOrDefault();

    public bool SupportsGameVersion(string gameVersion) =>
        GameVersions.Count == 0 ||
        GameVersions.Any(version => string.Equals(version, gameVersion, StringComparison.OrdinalIgnoreCase));

    public bool SupportsLoader(string? loader)
    {
        if (string.IsNullOrWhiteSpace(loader) ||
            string.Equals(loader, "vanilla", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(loader, "datapack", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = loader.Trim();
        return Loaders.Count == 0 ||
               Loaders.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
    }
}

public static class ModrinthApi
{
    public const string BaseAddress = "https://api.modrinth.com/v2";

    public static ModrinthProjectType ParseProjectType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "mod" => ModrinthProjectType.Mod,
        "modpack" => ModrinthProjectType.ModPack,
        "resourcepack" => ModrinthProjectType.ResourcePack,
        "shader" => ModrinthProjectType.Shader,
        "datapack" => ModrinthProjectType.DataPack,
        _ => ModrinthProjectType.Unknown,
    };

    public static string ToApiValue(ModrinthProjectType type) => type switch
    {
        ModrinthProjectType.Mod => "mod",
        ModrinthProjectType.ModPack => "modpack",
        ModrinthProjectType.ResourcePack => "resourcepack",
        ModrinthProjectType.Shader => "shader",
        ModrinthProjectType.DataPack => "datapack",
        _ => string.Empty,
    };

    public static string ProjectTypePath(ModrinthProjectType type) => type switch
    {
        ModrinthProjectType.Mod => "mod",
        ModrinthProjectType.ModPack => "modpack",
        ModrinthProjectType.ResourcePack => "resourcepack",
        ModrinthProjectType.Shader => "shader",
        ModrinthProjectType.DataPack => "datapack",
        _ => "project",
    };

    /// <summary>把启动器的加载器名映射到 Modrinth 的 categories / loaders 值。</summary>
    public static string? LoaderForModrinth(string? loader) => loader?.Trim().ToLowerInvariant() switch
    {
        "fabric" => "fabric",
        "forge" => "forge",
        "cleanroom" => "forge",
        "neoforge" => "neoforge",
        "quilt" => "quilt",
        _ => null,
    };
}
