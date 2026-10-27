// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ecl.Core.Json;

namespace Ecl.Minecraft.Loaders;

/// <summary>
/// 解析各加载器/组件公开接口的响应。拆成纯函数便于测试，不发起网络请求。
/// </summary>
public static class LoaderCatalogParsers
{
    public const string NeoForgeLegacyGameVersion = "1.20.1";

    private static readonly XNamespace AtomNamespace = "http://www.w3.org/2005/Atom";

    private static readonly Regex ModernGameVersionRegex =
        new(@"^1\.(\d+)", RegexOptions.CultureInvariant);

    public static IReadOnlyDictionary<string, DateTimeOffset> ParseAtomDates(string xml)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var document = XDocument.Parse(xml);
        if (document.Root is null)
        {
            return result;
        }

        foreach (var entry in document.Root.Elements(AtomNamespace + "entry"))
        {
            var id = entry.Element(AtomNamespace + "id")?.Value;
            var updated = entry.Element(AtomNamespace + "updated")?.Value;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(updated))
            {
                continue;
            }

            var slash = id.LastIndexOf('/');
            var tag = slash >= 0 ? id[(slash + 1)..] : id;
            if (tag.Length == 0 || !TryParseTimestamp(updated, out var releasedAt))
            {
                continue;
            }

            result[tag] = releasedAt;
        }

        return result;
    }

    public static IReadOnlyList<LoaderVersionInfo> ParseFabricLoaders(
        string json,
        IReadOnlyDictionary<string, DateTimeOffset>? releaseDates = null)
    {
        var entries = JsonSerializer.Deserialize<List<FabricLoaderEntry>>(json, EclJson.Default);
        if (entries is null)
        {
            return [];
        }

        var result = new List<LoaderVersionInfo>(entries.Count);
        foreach (var entry in entries)
        {
            var version = entry.Loader?.Version;
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            var releasedAt = releaseDates is not null && releaseDates.TryGetValue(version, out var date)
                ? date
                : (DateTimeOffset?)null;
            result.Add(new LoaderVersionInfo(version, entry.Loader!.Stable, releasedAt));
        }

        return result;
    }

    public static IReadOnlyList<LoaderVersionInfo> ParseForgeVersions(string xml, string gameVersion)
    {
        var prefix = gameVersion.Trim() + "-";
        return ReadMavenVersions(xml)
            .Where(version => version.StartsWith(prefix, StringComparison.Ordinal))
            .Select(version => version[prefix.Length..])
            .Where(version => version.Length > 0)
            .Reverse()
            .Select(version => new LoaderVersionInfo(version, true, null))
            .ToArray();
    }

    /// <summary>
    /// NeoForge 的 maven 版本号本身随游戏版本变化：1.20.1 使用 47.1.x，
    /// 1.20.2+ 使用「去掉 1. 前缀的游戏版本」，26.x 则补足到三段（26.3 → 26.3.0.x）。
    /// </summary>
    public static IReadOnlyList<LoaderVersionInfo> ParseNeoForgeVersions(string xml, string gameVersion)
    {
        var game = gameVersion.Trim();
        var prefix = GetNeoForgeVersionPrefix(game);
        if (prefix is null)
        {
            return [];
        }

        var legacyPrefix = game + "-";
        var result = new List<LoaderVersionInfo>();

        foreach (var version in ReadMavenVersions(xml).Reverse())
        {
            string normalized;
            if (string.Equals(game, NeoForgeLegacyGameVersion, StringComparison.Ordinal))
            {
                // 1.20.1 的 NeoForge 发布在 net.neoforged:forge，版本形如 1.20.1-47.1.106。
                if (!version.StartsWith(legacyPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                normalized = version[legacyPrefix.Length..];
            }
            else
            {
                if (!version.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                normalized = version;
            }

            if (normalized.Length == 0)
            {
                continue;
            }

            result.Add(new LoaderVersionInfo(
                normalized,
                !normalized.Contains("-beta", StringComparison.OrdinalIgnoreCase) &&
                !normalized.Contains("-alpha", StringComparison.OrdinalIgnoreCase),
                null));
        }

        return result;
    }

    public static string? GetNeoForgeVersionPrefix(string gameVersion)
    {
        var version = gameVersion.Trim();
        if (string.Equals(version, NeoForgeLegacyGameVersion, StringComparison.Ordinal))
        {
            return "47.1.";
        }

        if (version.StartsWith("1.", StringComparison.Ordinal))
        {
            var match = ModernGameVersionRegex.Match(version);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var minor) || minor < 20)
            {
                return null;
            }

            return version[2..] + ".";
        }

        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            2 => version + ".0.",
            >= 3 => string.Join('.', parts.Take(3)) + ".",
            _ => null,
        };
    }

    private static IReadOnlyList<string> ReadMavenVersions(string xml)
    {
        var document = XDocument.Parse(xml);
        var versions = document.Root?
            .Element("versioning")?
            .Element("versions")?
            .Elements("version")
            .Select(element => element.Value.Trim())
            .Where(version => version.Length > 0)
            .ToList();

        return versions ?? [];
    }

    public static IReadOnlyList<LoaderVersionInfo> ParseCleanroomVersions(
        string xml,
        IReadOnlyDictionary<string, DateTimeOffset>? releaseDates = null)
    {
        var document = XDocument.Parse(xml);
        var versions = document.Root?
            .Element("versioning")?
            .Element("versions")?
            .Elements("version")
            .Select(element => element.Value.Trim())
            .Where(version => version.Length > 0)
            .ToList();

        if (versions is null || versions.Count == 0)
        {
            return [];
        }

        versions.Reverse();
        return versions
            .Select(version =>
            {
                var releasedAt = releaseDates is not null && releaseDates.TryGetValue(version, out var date)
                    ? date
                    : (DateTimeOffset?)null;
                return new LoaderVersionInfo(
                    version,
                    !version.Contains("alpha", StringComparison.OrdinalIgnoreCase),
                    releasedAt);
            })
            .ToArray();
    }

    public static IReadOnlyList<FabricApiVersionInfo> ParseFabricApiVersions(string json)
    {
        var versions = JsonSerializer.Deserialize<List<ModrinthVersion>>(json, EclJson.Default);
        if (versions is null)
        {
            return [];
        }

        var result = new List<FabricApiVersionInfo>(versions.Count);
        foreach (var version in versions)
        {
            var file = version.Files?.FirstOrDefault(item => item.Primary)
                       ?? version.Files?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(version.VersionNumber) ||
                string.IsNullOrWhiteSpace(file?.Url) ||
                string.IsNullOrWhiteSpace(file.FileName))
            {
                continue;
            }

            string? sha1 = null;
            file.Hashes?.TryGetValue("sha1", out sha1);
            result.Add(new FabricApiVersionInfo(
                version.VersionNumber,
                file.Url,
                file.FileName,
                sha1,
                file.Size,
                version.DatePublished));
        }

        return result;
    }

    public static IReadOnlyList<OptiFineVersionInfo> ParseOptiFineVersions(string json)
    {
        var entries = JsonSerializer.Deserialize<List<OptiFineEntry>>(json, EclJson.Default);
        if (entries is null)
        {
            return [];
        }

        return entries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Type) &&
                !string.IsNullOrWhiteSpace(entry.Patch))
            .Select(entry => new OptiFineVersionInfo(
                entry.Type!,
                entry.Patch!,
                string.IsNullOrWhiteSpace(entry.FileName)
                    ? $"OptiFine_{entry.McVersion}_{entry.Type}_{entry.Patch}.jar"
                    : entry.FileName!,
                ParseObjectIdTimestamp(entry.Id)))
            .OrderByDescending(version => version.ReleasedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    public static DateTimeOffset? ParseObjectIdTimestamp(string? objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId) || objectId.Length < 8)
        {
            return null;
        }

        if (!long.TryParse(
                objectId[..8],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var seconds))
        {
            return null;
        }

        if (seconds is <= 0 or > 253402300799)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);

    private sealed class FabricLoaderEntry
    {
        [JsonPropertyName("loader")]
        public FabricLoaderDescriptor? Loader { get; set; }
    }

    private sealed class FabricLoaderDescriptor
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("stable")]
        public bool Stable { get; set; }
    }

    private sealed class ModrinthVersion
    {
        [JsonPropertyName("version_number")]
        public string? VersionNumber { get; set; }

        [JsonPropertyName("date_published")]
        public DateTimeOffset DatePublished { get; set; }

        [JsonPropertyName("files")]
        public List<ModrinthFile>? Files { get; set; }
    }

    private sealed class ModrinthFile
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("filename")]
        public string? FileName { get; set; }

        [JsonPropertyName("primary")]
        public bool Primary { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }

        [JsonPropertyName("hashes")]
        public Dictionary<string, string>? Hashes { get; set; }
    }

    private sealed class OptiFineEntry
    {
        [JsonPropertyName("_id")]
        public string? Id { get; set; }

        [JsonPropertyName("mcversion")]
        public string? McVersion { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("patch")]
        public string? Patch { get; set; }

        [JsonPropertyName("filename")]
        public string? FileName { get; set; }
    }
}
