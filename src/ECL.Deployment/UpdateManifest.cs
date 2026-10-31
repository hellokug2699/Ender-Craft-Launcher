// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;

namespace Ecl.Deployment;

public enum UpdateChannel
{
    Stable,
    Beta,
    Dev,
}

public enum UpdateArchitecture
{
    X64,
    Arm64,
}

public static class UpdateChannels
{
    public const string WindowsPlatform = "windows";

    public static string ToApiValue(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "stable",
        UpdateChannel.Beta => "beta",
        UpdateChannel.Dev => "dev",
        _ => "stable",
    };

    public static bool TryParse(string? value, out UpdateChannel channel)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "stable":
                channel = UpdateChannel.Stable;
                return true;
            case "beta":
                channel = UpdateChannel.Beta;
                return true;
            case "dev":
                channel = UpdateChannel.Dev;
                return true;
            default:
                channel = UpdateChannel.Stable;
                return false;
        }
    }

    public static string ToApiValue(this UpdateArchitecture architecture) =>
        architecture == UpdateArchitecture.Arm64 ? "arm64" : "x64";

    public static bool TryParseArchitecture(string? value, out UpdateArchitecture architecture)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "x64":
            case "amd64":
            case "x86_64":
                architecture = UpdateArchitecture.X64;
                return true;
            case "arm64":
            case "aarch64":
                architecture = UpdateArchitecture.Arm64;
                return true;
            default:
                architecture = UpdateArchitecture.X64;
                return false;
        }
    }
}

public sealed record UpdatePackage(string FileName, long Size, string Sha256);

public sealed record UpdateFile(string Path, long Size, string Sha256)
{
    public override string ToString() => Path;
}

/// <summary>
/// 完整多文件更新 Manifest。版本化 schema；签名是独立的 detached signature，
/// 因此这里不包含签名本身，避免“签名覆盖签名”的歧义。
/// </summary>
public sealed record UpdateManifest(
    int SchemaVersion,
    string Product,
    UpdateChannel Channel,
    ReleaseVersion Version,
    string Platform,
    UpdateArchitecture Architecture,
    ReleaseVersion MinimumUpdaterVersion,
    UpdatePackage Package,
    IReadOnlyList<UpdateFile> Files,
    IReadOnlyList<string> RemovedFiles,
    string? ReleaseNotes,
    DateTimeOffset? PublishedAt);

public static class UpdateManifestParser
{
    public const int CurrentSchemaVersion = 1;
    public const string ProductName = "ECL";
    public const int MaxReleaseNotesLength = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static Result<UpdateManifest> Parse(ReadOnlySpan<byte> json)
    {
        ManifestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            return Result<UpdateManifest>.Fail(
                "update.manifest_invalid_json",
                "更新清单不是有效的 JSON。",
                exception.Message);
        }

        if (dto is null)
        {
            return Result<UpdateManifest>.Fail("update.manifest_invalid_json", "更新清单为空。");
        }

        if (dto.SchemaVersion != CurrentSchemaVersion)
        {
            return Result<UpdateManifest>.Fail(
                "update.schema_unsupported",
                $"不支持的更新清单 schema：{dto.SchemaVersion}。",
                $"当前客户端支持 {CurrentSchemaVersion}。");
        }

        if (!string.Equals(dto.Product, ProductName, StringComparison.Ordinal))
        {
            return Result<UpdateManifest>.Fail(
                "update.product_mismatch",
                "更新清单的产品标识不匹配。",
                dto.Product);
        }

        if (!UpdateChannels.TryParse(dto.Channel, out var channel))
        {
            return Result<UpdateManifest>.Fail(
                "update.channel_invalid",
                "更新清单的发布通道无效。",
                dto.Channel);
        }

        if (!ReleaseVersion.TryParse(dto.Version, out var version))
        {
            return Result<UpdateManifest>.Fail(
                "update.version_invalid",
                "更新清单的版本号无效。",
                dto.Version);
        }

        if (!string.Equals(dto.Platform, UpdateChannels.WindowsPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return Result<UpdateManifest>.Fail(
                "update.platform_unsupported",
                "更新清单的目标平台不受支持。",
                dto.Platform);
        }

        if (!UpdateChannels.TryParseArchitecture(dto.Architecture, out var architecture))
        {
            return Result<UpdateManifest>.Fail(
                "update.architecture_unsupported",
                "更新清单的目标架构不受支持。",
                dto.Architecture);
        }

        if (!ReleaseVersion.TryParse(dto.MinimumUpdaterVersion, out var minimumUpdaterVersion))
        {
            return Result<UpdateManifest>.Fail(
                "update.min_updater_invalid",
                "更新清单的最低更新器版本无效。",
                dto.MinimumUpdaterVersion);
        }

        var package = dto.Package;
        if (package is null ||
            !UpdatePathSafety.TryNormalizeRelativePath(package.File, out var packageFile) ||
            package.Size < 0 ||
            !UpdateHash.IsSha256(package.Sha256))
        {
            return Result<UpdateManifest>.Fail(
                "update.package_invalid",
                "更新清单的发布包信息无效。");
        }

        var files = new List<UpdateFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in dto.Files ?? [])
        {
            if (!UpdatePathSafety.TryNormalizeRelativePath(file.Path, out var path) ||
                file.Size < 0 ||
                !UpdateHash.IsSha256(file.Sha256) ||
                !seenPaths.Add(path))
            {
                return Result<UpdateManifest>.Fail(
                    "update.files_invalid",
                    "更新清单包含不安全的文件路径、重复路径或无效哈希。",
                    file.Path);
            }

            files.Add(new UpdateFile(path, file.Size, file.Sha256!.ToLowerInvariant()));
        }

        var removedFiles = new List<string>();
        foreach (var removed in dto.RemovedFiles ?? [])
        {
            if (!UpdatePathSafety.TryNormalizeRelativePath(removed, out var path) ||
                !seenPaths.Add(path))
            {
                return Result<UpdateManifest>.Fail(
                    "update.removed_invalid",
                    "更新清单的删除列表包含不安全路径、重复路径或与安装文件冲突的条目。",
                    removed);
            }

            removedFiles.Add(path);
        }

        var releaseNotes = dto.ReleaseNotes;
        if (releaseNotes is not null && releaseNotes.Length > MaxReleaseNotesLength)
        {
            return Result<UpdateManifest>.Fail(
                "update.release_notes_too_long",
                "更新清单的发布说明过长。");
        }

        return Result<UpdateManifest>.Ok(new UpdateManifest(
            dto.SchemaVersion,
            dto.Product!,
            channel,
            version,
            UpdateChannels.WindowsPlatform,
            architecture,
            minimumUpdaterVersion,
            new UpdatePackage(packageFile, package.Size, package.Sha256!.ToLowerInvariant()),
            files,
            removedFiles,
            releaseNotes,
            dto.PublishedAt));
    }

    public static byte[] Serialize(UpdateManifest manifest)
    {
        var dto = new ManifestDto
        {
            SchemaVersion = manifest.SchemaVersion,
            Product = manifest.Product,
            Channel = manifest.Channel.ToApiValue(),
            Version = manifest.Version.ToString(),
            Platform = manifest.Platform,
            Architecture = manifest.Architecture.ToApiValue(),
            MinimumUpdaterVersion = manifest.MinimumUpdaterVersion.ToString(),
            Package = new PackageDto
            {
                File = manifest.Package.FileName,
                Size = manifest.Package.Size,
                Sha256 = manifest.Package.Sha256,
            },
            Files = manifest.Files
                .Select(file => new FileDto { Path = file.Path, Size = file.Size, Sha256 = file.Sha256 })
                .ToList(),
            RemovedFiles = manifest.RemovedFiles.ToList(),
            ReleaseNotes = manifest.ReleaseNotes,
            PublishedAt = manifest.PublishedAt,
        };

        return JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
    }

    private sealed class ManifestDto
    {
        public int SchemaVersion { get; init; }

        public string? Product { get; init; }

        public string? Channel { get; init; }

        public string? Version { get; init; }

        public string? Platform { get; init; }

        public string? Architecture { get; init; }

        public string? MinimumUpdaterVersion { get; init; }

        public PackageDto? Package { get; init; }

        public List<FileDto>? Files { get; init; }

        public List<string>? RemovedFiles { get; init; }

        public string? ReleaseNotes { get; init; }

        public DateTimeOffset? PublishedAt { get; init; }
    }

    private sealed class PackageDto
    {
        public string? File { get; init; }

        public long Size { get; init; }

        public string? Sha256 { get; init; }
    }

    private sealed class FileDto
    {
        public string? Path { get; init; }

        public long Size { get; init; }

        public string? Sha256 { get; init; }
    }
}
