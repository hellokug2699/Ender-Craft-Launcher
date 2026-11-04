// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;

namespace Ecl.VanillaToolkit;

/// <summary>数据可信度/验证状态；工具结果必须能区分精确数据、推导规则与未验证信息。</summary>
public enum VanillaDataQuality
{
    Verified,
    Derived,
    Community,
    Unverified,
}

public sealed record VanillaDataSource(
    string Id,
    string Name,
    string? Url,
    string? License,
    string? RetrievedAtUtc);

public sealed record VanillaDataDomain(
    string Name,
    int SchemaVersion,
    string File,
    long Size,
    string Sha256);

/// <summary>
/// 数据包清单（ADR 0040 §5）。目录、文件、大小与哈希必须相互一致；版本适用范围显式声明，
/// 不允许用“最接近版本”静默回退。
/// </summary>
public sealed record VanillaDataManifest(
    int SchemaVersion,
    string PackId,
    string DataVersion,
    IReadOnlyList<string> MinecraftVersions,
    IReadOnlyList<VanillaDataDomain> Domains,
    IReadOnlyList<VanillaDataSource> Sources,
    string Maintainer,
    DateTimeOffset UpdatedAtUtc,
    VanillaDataQuality Quality,
    string? License,
    string? Description);

public sealed record RegistryEntry(
    string Id,
    string Kind,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyList<string> Aliases,
    string? Introduced,
    string? Removed,
    IReadOnlyList<string> Versions,
    VanillaDataQuality Quality,
    string? SourceId);

public sealed record LoadedVanillaDataPack(
    VanillaDataManifest Manifest,
    IReadOnlyList<RegistryEntry> Registry);

public enum RegistryMatchKind
{
    ExactId,
    ExactName,
    Alias,
    Partial,
}

public sealed record RegistryQueryResult(
    RegistryEntry Entry,
    RegistryMatchKind Match,
    VanillaDataQuality Quality,
    string? SourceId);

/// <summary>
/// 原版数据包加载与校验。所有文件在参与查询前完成路径、大小、SHA-256 与 schema 校验；
/// 单个数据域损坏只禁用该域，不影响其他工具（ADR 0040 §11）。
/// </summary>
public static class VanillaDataPackLoader
{
    public const int CurrentSchemaVersion = 1;
    public const long MaxManifestBytes = 256 * 1024;
    public const long MaxDomainBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<Result<LoadedVanillaDataPack>> LoadAsync(
        string packDirectory,
        CancellationToken cancellationToken = default)
    {
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packDirectory));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<LoadedVanillaDataPack>.Fail("toolkit.path_invalid", $"数据包目录无效：{exception.Message}");
        }

        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return Result<LoadedVanillaDataPack>.Fail("toolkit.manifest_missing", "数据包缺少 manifest.json。");
        }

        byte[] manifestBytes;
        try
        {
            var info = new FileInfo(manifestPath);
            if (info.Length > MaxManifestBytes)
            {
                return Result<LoadedVanillaDataPack>.Fail("toolkit.manifest_too_large", "数据包清单超出大小上限。");
            }

            manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<LoadedVanillaDataPack>.Fail("toolkit.manifest_unreadable", "无法读取数据包清单。", exception.Message);
        }

        ManifestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(new string(System.Text.Encoding.UTF8.GetString(manifestBytes)), JsonOptions);
        }
        catch (JsonException exception)
        {
            return Result<LoadedVanillaDataPack>.Fail("toolkit.manifest_invalid", "数据包清单不是有效的 JSON。", exception.Message);
        }

        if (dto is null ||
            dto.SchemaVersion != CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(dto.PackId) ||
            string.IsNullOrWhiteSpace(dto.DataVersion) ||
            string.IsNullOrWhiteSpace(dto.Maintainer) ||
            dto.MinecraftVersions is null ||
            dto.MinecraftVersions.Count == 0 ||
            dto.Domains is null ||
            dto.Domains.Count == 0)
        {
            return Result<LoadedVanillaDataPack>.Fail("toolkit.manifest_invalid", "数据包清单缺少必要字段或 schema 版本不受支持。");
        }

        var domains = new List<VanillaDataDomain>();
        foreach (var domain in dto.Domains)
        {
            if (string.IsNullOrWhiteSpace(domain.Name) ||
                string.IsNullOrWhiteSpace(domain.File) ||
                domain.Size <= 0 ||
                !IsSha256(domain.Sha256) ||
                !UpdatePathSafe(domain.File))
            {
                return Result<LoadedVanillaDataPack>.Fail("toolkit.domain_invalid", $"数据域声明无效：{domain.Name}");
            }

            domains.Add(new VanillaDataDomain(domain.Name, domain.SchemaVersion, domain.File, domain.Size, domain.Sha256!.ToLowerInvariant()));
        }

        var sources = (dto.Sources ?? [])
            .Where(source => !string.IsNullOrWhiteSpace(source.Id) && !string.IsNullOrWhiteSpace(source.Name))
            .Select(source => new VanillaDataSource(source.Id!, source.Name!, source.Url, source.License, source.RetrievedAtUtc))
            .ToArray();
        var manifest = new VanillaDataManifest(
            dto.SchemaVersion,
            dto.PackId!,
            dto.DataVersion!,
            dto.MinecraftVersions,
            domains,
            sources,
            dto.Maintainer!,
            dto.UpdatedAtUtc ?? DateTimeOffset.UnixEpoch,
            ParseQuality(dto.Quality),
            dto.License,
            dto.Description);

        IReadOnlyList<RegistryEntry> registry = [];
        var registryDomain = domains.FirstOrDefault(domain => string.Equals(domain.Name, "registry", StringComparison.OrdinalIgnoreCase));
        if (registryDomain is not null)
        {
            var loaded = await LoadRegistryAsync(root, registryDomain, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return Result<LoadedVanillaDataPack>.Fail(loaded.Error!);
            }

            registry = loaded.Value!;
        }

        return Result<LoadedVanillaDataPack>.Ok(new LoadedVanillaDataPack(manifest, registry));
    }

    private static async Task<Result<IReadOnlyList<RegistryEntry>>> LoadRegistryAsync(
        string packRoot,
        VanillaDataDomain domain,
        CancellationToken cancellationToken)
    {
        if (!UpdatePathSafe(domain.File))
        {
            return Result<IReadOnlyList<RegistryEntry>>.Fail("toolkit.domain_path_invalid", "数据域路径越界。");
        }

        var path = Path.GetFullPath(Path.Combine(packRoot, domain.File.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContainedIn(packRoot, path) || !File.Exists(path))
        {
            return Result<IReadOnlyList<RegistryEntry>>.Fail("toolkit.domain_missing", $"数据域文件不存在：{domain.Name}");
        }

        var info = new FileInfo(path);
        if (info.Length != domain.Size || info.Length > MaxDomainBytes)
        {
            return Result<IReadOnlyList<RegistryEntry>>.Fail("toolkit.domain_size_mismatch", $"数据域大小与清单不符：{domain.Name}");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, domain.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return Result<IReadOnlyList<RegistryEntry>>.Fail("toolkit.domain_hash_mismatch", $"数据域 SHA-256 与清单不符：{domain.Name}");
        }

        RegistryDomainDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RegistryDomainDto>(System.Text.Encoding.UTF8.GetString(bytes), JsonOptions);
        }
        catch (JsonException exception)
        {
            return Result<IReadOnlyList<RegistryEntry>>.Fail("toolkit.domain_invalid", $"数据域 JSON 无效：{domain.Name}", exception.Message);
        }

        if (dto is null ||
            dto.SchemaVersion != domain.SchemaVersion ||
            dto.SchemaVersion != 1 ||
            dto.Entries is null)
        {
            return Result<IReadOnlyList<RegistryEntry>>.Fail("toolkit.domain_schema_unsupported", $"数据域 schema 不受支持：{domain.Name}");
        }

        var entries = new List<RegistryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in dto.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) ||
                string.IsNullOrWhiteSpace(entry.Kind) ||
                entry.Names is null ||
                entry.Names.Count == 0 ||
                !seen.Add(entry.Id))
            {
                return Result<IReadOnlyList<RegistryEntry>>.Fail("toolkit.registry_entry_invalid", $"注册表条目无效或重复：{entry.Id}");
            }

            entries.Add(new RegistryEntry(
                entry.Id,
                entry.Kind,
                new Dictionary<string, string>(entry.Names, StringComparer.OrdinalIgnoreCase),
                entry.Aliases ?? [],
                entry.Introduced,
                entry.Removed,
                entry.Versions ?? [],
                ParseQuality(entry.Quality),
                entry.SourceId));
        }

        return Result<IReadOnlyList<RegistryEntry>>.Ok(entries);
    }

    private static VanillaDataQuality ParseQuality(string? value) =>
        Enum.TryParse<VanillaDataQuality>(value, ignoreCase: true, out var quality) ? quality : VanillaDataQuality.Unverified;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool UpdatePathSafe(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Split('/', '\\').Any(segment => segment is "." or "..") &&
        path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    private static bool IsContainedIn(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ManifestDto
    {
        public int SchemaVersion { get; init; }

        public string? PackId { get; init; }

        public string? DataVersion { get; init; }

        public List<string>? MinecraftVersions { get; init; }

        public List<DomainDto>? Domains { get; init; }

        public List<SourceDto>? Sources { get; init; }

        public string? Maintainer { get; init; }

        public DateTimeOffset? UpdatedAtUtc { get; init; }

        public string? Quality { get; init; }

        public string? License { get; init; }

        public string? Description { get; init; }
    }

    private sealed class DomainDto
    {
        public string? Name { get; init; }

        public int SchemaVersion { get; init; }

        public string? File { get; init; }

        public long Size { get; init; }

        public string? Sha256 { get; init; }
    }

    private sealed class SourceDto
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Url { get; init; }

        public string? License { get; init; }

        public string? RetrievedAtUtc { get; init; }
    }

    private sealed class RegistryDomainDto
    {
        public int SchemaVersion { get; init; }

        public List<RegistryEntryDto>? Entries { get; init; }
    }

    private sealed class RegistryEntryDto
    {
        public string? Id { get; init; }

        public string? Kind { get; init; }

        public Dictionary<string, string>? Names { get; init; }

        public List<string>? Aliases { get; init; }

        public string? Introduced { get; init; }

        public string? Removed { get; init; }

        public List<string>? Versions { get; init; }

        public string? Quality { get; init; }

        public string? SourceId { get; init; }
    }
}
