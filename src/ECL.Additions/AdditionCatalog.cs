// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;
using Ecl.Deployment;

namespace Ecl.Additions;

/// <summary>
/// 官方目录解析与校验。目录、条目与包清单必须相互一致；未知字段拒绝而不是静默忽略
/// （ADR 0032 §6）。签名验证复用既有 RSA-PSS 信任模型，另用 additions-trust.json 区分密钥域。
/// </summary>
public static class AdditionCatalogParser
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxAdditions = 256;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static Result<AdditionCatalog> Parse(string json)
    {
        CatalogDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<CatalogDto>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            return Result<AdditionCatalog>.Fail(
                "additions.catalog_invalid_json",
                "扩展目录不是有效的 JSON。",
                exception.Message);
        }

        if (dto is null || dto.SchemaVersion != CurrentSchemaVersion)
        {
            return Result<AdditionCatalog>.Fail(
                "additions.catalog_unsupported",
                "扩展目录 schema 版本不受支持。");
        }

        if (!ReleaseVersion.TryParse(dto.CoreApiVersion, out var coreApiVersion))
        {
            return Result<AdditionCatalog>.Fail(
                "additions.catalog_core_api_invalid",
                "扩展目录缺少有效的 Core API 版本。");
        }

        if (dto.Additions is null || dto.Additions.Count > MaxAdditions)
        {
            return Result<AdditionCatalog>.Fail(
                "additions.catalog_entries_invalid",
                "扩展目录条目缺失或数量超出上限。");
        }

        var entries = new List<AdditionEntry>(dto.Additions.Count);
        var seen = new HashSet<AdditionId>();
        foreach (var item in dto.Additions)
        {
            var parsedEntry = ParseEntry(item);
            if (!parsedEntry.IsSuccess)
            {
                return Result<AdditionCatalog>.Fail(parsedEntry.Error!);
            }

            if (!seen.Add(parsedEntry.Value!.Id))
            {
                return Result<AdditionCatalog>.Fail(
                    "additions.catalog_duplicate_id",
                    "扩展目录包含重复的 Addition ID。",
                    parsedEntry.Value.Id.Value);
            }

            entries.Add(parsedEntry.Value);
        }

        return Result<AdditionCatalog>.Ok(new AdditionCatalog(CurrentSchemaVersion, coreApiVersion, entries));
    }

    public static string Serialize(AdditionCatalog catalog)
    {
        var dto = new CatalogDto
        {
            SchemaVersion = CurrentSchemaVersion,
            CoreApiVersion = catalog.CoreApiVersion.ToString(),
            Additions = catalog.Additions.Select(ToDto).ToList(),
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static Result<AdditionEntry> ParseEntry(EntryDto item)
    {
        if (!AdditionId.TryParse(item.Id, out var id))
        {
            return Result<AdditionEntry>.Fail("additions.catalog_id_invalid", $"Addition ID 无效：{item.Id}");
        }

        if (!TryParseType(item.Type, out var type))
        {
            return Result<AdditionEntry>.Fail("additions.catalog_type_invalid", $"扩展类型无效：{id.Value}");
        }

        if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Description))
        {
            return Result<AdditionEntry>.Fail("additions.catalog_metadata_missing", $"扩展缺少名称或说明：{id.Value}");
        }

        if (!ReleaseVersion.TryParse(item.Version, out var version))
        {
            return Result<AdditionEntry>.Fail("additions.catalog_version_invalid", $"扩展版本无效：{id.Value}");
        }

        if (item.Package is null ||
            string.IsNullOrWhiteSpace(item.Package.Url) ||
            !Uri.TryCreate(item.Package.Url, UriKind.Absolute, out var packageUri) ||
            !string.Equals(packageUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            item.Package.Size <= 0 ||
            !IsSha256(item.Package.Sha256))
        {
            return Result<AdditionEntry>.Fail(
                "additions.catalog_package_invalid",
                $"扩展包地址、大小或 SHA-256 无效：{id.Value}");
        }

        if (item.CoreApi is null ||
            !ReleaseVersion.TryParse(item.CoreApi.Min, out var apiMin) ||
            !ReleaseVersion.TryParse(item.CoreApi.Max, out var apiMax) ||
            apiMin.CompareTo(apiMax) > 0)
        {
            return Result<AdditionEntry>.Fail(
                "additions.catalog_core_api_invalid",
                $"扩展 Core API 范围无效：{id.Value}");
        }

        var dependencies = new List<AdditionDependency>();
        var seenDependencies = new HashSet<AdditionId>();
        foreach (var dependency in item.Dependencies ?? [])
        {
            if (!AdditionId.TryParse(dependency.Id, out var dependencyId) ||
                dependencyId == id ||
                !seenDependencies.Add(dependencyId) ||
                !ReleaseVersion.TryParse(dependency.MinVersion, out var minVersion) ||
                (dependency.MaxVersion is not null && !ReleaseVersion.TryParse(dependency.MaxVersion, out _)))
            {
                return Result<AdditionEntry>.Fail(
                    "additions.catalog_dependency_invalid",
                    $"扩展依赖声明无效：{id.Value}");
            }

            ReleaseVersion? maxVersion = null;
            if (dependency.MaxVersion is not null)
            {
                maxVersion = ReleaseVersion.Parse(dependency.MaxVersion);
            }

            if (maxVersion is not null && minVersion.CompareTo(maxVersion) > 0)
            {
                return Result<AdditionEntry>.Fail(
                    "additions.catalog_dependency_invalid",
                    $"扩展依赖版本范围无效：{id.Value}");
            }

            dependencies.Add(new AdditionDependency(dependencyId, minVersion, maxVersion, dependency.Optional));
        }

        var conflicts = new List<AdditionId>();
        foreach (var conflict in item.ConflictsWith ?? [])
        {
            if (!AdditionId.TryParse(conflict, out var conflictId) || conflictId == id)
            {
                return Result<AdditionEntry>.Fail(
                    "additions.catalog_conflict_invalid",
                    $"扩展冲突声明无效：{id.Value}");
            }

            conflicts.Add(conflictId);
        }

        if (type == AdditionType.Data && item.DataSchemaVersion is null or <= 0)
        {
            return Result<AdditionEntry>.Fail(
                "additions.catalog_data_schema_missing",
                $"数据扩展必须声明 dataSchemaVersion：{id.Value}");
        }

        var platforms = (item.Platforms ?? [])
            .Where(platform => !string.IsNullOrWhiteSpace(platform))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var packageDto = item.Package!;
        var packageUrl = packageDto.Url!;
        var packageSha256 = packageDto.Sha256!;
        return Result<AdditionEntry>.Ok(new AdditionEntry(
            id,
            type,
            item.Name!,
            item.Description!,
            version,
            new AdditionPackage(packageUrl, packageDto.Size, packageSha256.ToLowerInvariant(), packageDto.ReleaseTag),
            new AdditionCoreApiRange(apiMin, apiMax),
            dependencies,
            conflicts,
            item.DataSchemaVersion,
            platforms,
            item.Withdrawn));
    }

    private static EntryDto ToDto(AdditionEntry entry) => new()
    {
        Id = entry.Id.Value,
        Type = entry.Type == AdditionType.Feature ? "feature" : "data",
        Name = entry.Name,
        Description = entry.Description,
        Version = entry.Version.ToString(),
        Package = new PackageDto
        {
            Url = entry.Package.Url,
            Size = entry.Package.Size,
            Sha256 = entry.Package.Sha256,
            ReleaseTag = entry.Package.ReleaseTag,
        },
        CoreApi = new CoreApiDto { Min = entry.CoreApi.Min.ToString(), Max = entry.CoreApi.Max.ToString() },
        Dependencies = entry.Dependencies.Select(dependency => new DependencyDto
        {
            Id = dependency.Id.Value,
            MinVersion = dependency.MinVersion.ToString(),
            MaxVersion = dependency.MaxVersion?.ToString(),
            Optional = dependency.Optional,
        }).ToList(),
        ConflictsWith = entry.ConflictsWith.Select(conflict => conflict.Value).ToList(),
        DataSchemaVersion = entry.DataSchemaVersion,
        Platforms = entry.Platforms.ToList(),
        Withdrawn = entry.Withdrawn,
    };

    private static bool TryParseType(string? value, out AdditionType type)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "feature":
                type = AdditionType.Feature;
                return true;
            case "data":
                type = AdditionType.Data;
                return true;
            default:
                type = AdditionType.Feature;
                return false;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => Uri.IsHexDigit(character));

    private sealed class CatalogDto
    {
        public int SchemaVersion { get; init; }

        public string? CoreApiVersion { get; init; }

        public List<EntryDto>? Additions { get; init; }
    }

    private sealed class EntryDto
    {
        public string? Id { get; init; }

        public string? Type { get; init; }

        public string? Name { get; init; }

        public string? Description { get; init; }

        public string? Version { get; init; }

        public PackageDto? Package { get; init; }

        public CoreApiDto? CoreApi { get; init; }

        public List<DependencyDto>? Dependencies { get; init; }

        public List<string>? ConflictsWith { get; init; }

        public int? DataSchemaVersion { get; init; }

        public List<string>? Platforms { get; init; }

        public bool Withdrawn { get; init; }
    }

    private sealed class PackageDto
    {
        public string? Url { get; init; }

        public long Size { get; init; }

        public string? Sha256 { get; init; }

        public string? ReleaseTag { get; init; }
    }

    private sealed class CoreApiDto
    {
        public string? Min { get; init; }

        public string? Max { get; init; }
    }

    private sealed class DependencyDto
    {
        public string? Id { get; init; }

        public string? MinVersion { get; init; }

        public string? MaxVersion { get; init; }

        public bool Optional { get; init; }
    }
}

/// <summary>安装包内的 <c>addition.json</c> 解析；入口程序集与文件清单必须自洽。</summary>
public static class AdditionPackageManifestParser
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public const string FileName = "addition.json";

    public static Result<AdditionPackageManifest> Parse(string json)
    {
        PackageManifestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PackageManifestDto>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            return Result<AdditionPackageManifest>.Fail(
                "additions.package_manifest_invalid",
                "扩展包清单不是有效的 JSON。",
                exception.Message);
        }

        if (dto is null ||
            dto.SchemaVersion != AdditionCatalogParser.CurrentSchemaVersion ||
            !AdditionId.TryParse(dto.Id, out var id) ||
            !ReleaseVersion.TryParse(dto.Version, out var version) ||
            string.IsNullOrWhiteSpace(dto.EntryAssembly) ||
            dto.Files is null ||
            dto.Files.Count == 0)
        {
            return Result<AdditionPackageManifest>.Fail(
                "additions.package_manifest_invalid",
                "扩展包清单缺少必要字段或版本不受支持。");
        }

        var files = new List<AdditionPackageFile>();
        var fileList = dto.Files!;
        foreach (var file in fileList)
        {
            if (string.IsNullOrWhiteSpace(file.Path) ||
                file.Size < 0 ||
                !IsSha256(file.Sha256) ||
                !UpdatePathSafety.TryNormalizeRelativePath(file.Path, out _))
            {
                return Result<AdditionPackageManifest>.Fail(
                    "additions.package_file_invalid",
                    $"扩展包文件条目无效：{file.Path}");
            }

            files.Add(new AdditionPackageFile(file.Path!, file.Size, file.Sha256!.ToLowerInvariant()));
        }

        if (!files.Any(file => string.Equals(
                file.Path.Replace('\\', '/'),
                dto.EntryAssembly.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase)))
        {
            return Result<AdditionPackageManifest>.Fail(
                "additions.package_entry_missing",
                "入口程序集未列在包文件清单中。");
        }

        return Result<AdditionPackageManifest>.Ok(new AdditionPackageManifest(
            dto.SchemaVersion,
            id,
            version,
            dto.EntryAssembly,
            files,
            dto.DataSchemaVersion));

        static bool IsSha256(string? value) =>
            value is { Length: 64 } && value.All(character => Uri.IsHexDigit(character));
    }

    private sealed class PackageManifestDto
    {
        public int SchemaVersion { get; init; }

        public string? Id { get; init; }

        public string? Version { get; init; }

        public string? EntryAssembly { get; init; }

        public List<PackageFileDto>? Files { get; init; }

        public int? DataSchemaVersion { get; init; }
    }

    private sealed class PackageFileDto
    {
        public string? Path { get; init; }

        public long Size { get; init; }

        public string? Sha256 { get; init; }
    }
}

/// <summary>官方目录的快照；缓存命中时明确标记可能不是最新状态（ADR 0032 §8）。</summary>
public sealed record AdditionCatalogSnapshot(
    AdditionCatalog Catalog,
    bool FromCache,
    DateTimeOffset? FetchedAtUtc,
    IReadOnlyList<string> Warnings);

public sealed record AdditionCatalogPayload(string CatalogJson, string SignatureJson);

/// <summary>目录来源：实现可以是 GitHub Release 静态资产；普通启动路径不得依赖它。</summary>
public interface IAdditionCatalogSource
{
    Task<Result<AdditionCatalogPayload>> FetchAsync(CancellationToken cancellationToken = default);
}

public sealed class HttpAdditionCatalogSource : IAdditionCatalogSource
{
    private readonly HttpClient _httpClient;
    private readonly Uri _catalogUri;
    private readonly Uri _signatureUri;

    public HttpAdditionCatalogSource(HttpClient httpClient, Uri catalogUri, Uri signatureUri)
    {
        _httpClient = httpClient;
        _catalogUri = catalogUri;
        _signatureUri = signatureUri;
    }

    public async Task<Result<AdditionCatalogPayload>> FetchAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(_catalogUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_signatureUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Result<AdditionCatalogPayload>.Fail(
                "additions.catalog_source_insecure",
                "扩展目录必须通过 HTTPS 获取。");
        }

        try
        {
            var catalog = await _httpClient.GetStringAsync(_catalogUri, cancellationToken).ConfigureAwait(false);
            var signature = await _httpClient.GetStringAsync(_signatureUri, cancellationToken).ConfigureAwait(false);
            return Result<AdditionCatalogPayload>.Ok(new AdditionCatalogPayload(catalog, signature));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return Result<AdditionCatalogPayload>.Fail(
                "additions.catalog_source_unreachable",
                "无法获取扩展目录；普通启动与实例管理不受影响。",
                exception.Message);
        }
    }
}
