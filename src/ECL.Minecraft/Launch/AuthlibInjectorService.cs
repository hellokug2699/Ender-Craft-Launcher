// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Download;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch;

public sealed record AuthlibInjectorArtifact(string Path, string Version);

/// <summary>
/// 获取 Authlib-Injector 客户端组件：官方清单 → 校验 SHA-256 → 缓存到数据目录。
/// 只下载并执行官方发布且校验通过的 jar。
/// </summary>
public interface IAuthlibInjectorService
{
    Task<Result<AuthlibInjectorArtifact>> EnsureAsync(CancellationToken cancellationToken = default);

    /// <summary>预览用：返回已缓存的 jar 路径（可能为 null）。</summary>
    string? TryGetCachedJarPath();
}

public sealed class AuthlibInjectorService(
    IDownloadService downloads,
    IAppPaths paths,
    ILogger<AuthlibInjectorService> logger) : IAuthlibInjectorService
{
    private static readonly Uri[] ManifestUrls =
    [
        new("https://authlib-injector.yushi.moe/artifact/latest.json"),
        new("https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json"),
    ];

    private const string JarPrefix = "authlib-injector-";
    private const string JarSuffix = ".jar";

    public async Task<Result<AuthlibInjectorArtifact>> EnsureAsync(CancellationToken cancellationToken = default)
    {
        ArtifactManifest? manifest;
        try
        {
            var text = await downloads.DownloadTextAsync(ManifestUrls, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<ArtifactManifest>(text, EclJson.Default);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return Result<AuthlibInjectorArtifact>.Fail(
                "authlibinjector.manifest_failed",
                $"无法获取 Authlib-Injector 版本信息：{exception.Message}");
        }

        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.DownloadUrl) ||
            string.IsNullOrWhiteSpace(manifest.Checksums?.Sha256))
        {
            return Result<AuthlibInjectorArtifact>.Fail(
                "authlibinjector.manifest_failed",
                "Authlib-Injector 版本信息不完整，无法校验下载文件。");
        }

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps)
        {
            return Result<AuthlibInjectorArtifact>.Fail(
                "authlibinjector.manifest_failed",
                "Authlib-Injector 下载地址不是 HTTPS 地址，已拒绝。");
        }

        var version = manifest.Version.Trim();
        if (!IsSafeVersion(version))
        {
            return Result<AuthlibInjectorArtifact>.Fail(
                "authlibinjector.manifest_failed",
                "Authlib-Injector 版本号格式不合法，已拒绝。");
        }

        var expectedHash = manifest.Checksums.Sha256.Trim().ToLowerInvariant();
        if (!IsValidSha256(expectedHash))
        {
            return Result<AuthlibInjectorArtifact>.Fail(
                "authlibinjector.manifest_failed",
                "Authlib-Injector 的 SHA-256 校验值格式不正确，已拒绝。");
        }

        var target = GetCachePath(paths.InjectorDirectory, version);
        if (target is null)
        {
            return Result<AuthlibInjectorArtifact>.Fail(
                "authlibinjector.manifest_failed",
                "Authlib-Injector 缓存路径不合法，已拒绝。");
        }

        try
        {
            Directory.CreateDirectory(paths.InjectorDirectory);

            if (File.Exists(target) && !HasExpectedHash(target, expectedHash))
            {
                logger.LogWarning("缓存的 Authlib-Injector 校验失败，将重新下载：{Path}", target);
                File.Delete(target);
                DeleteCachedHash(target);
            }

            if (!File.Exists(target))
            {
                await downloads.DownloadAsync(
                    [
                        new DownloadRequest(
                            target,
                            [downloadUri],
                            expectedHash,
                            HashingAlgorithm.Sha256,
                            null,
                            $"Authlib-Injector {version}"),
                    ],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (!HasExpectedHash(target, expectedHash))
            {
                File.Delete(target);
                DeleteCachedHash(target);
                return Result<AuthlibInjectorArtifact>.Fail(
                    "authlibinjector.checksum_failed",
                    "Authlib-Injector 下载文件校验失败，已拒绝加载。");
            }

            // 记录已验证的哈希，预览命令只使用带有效校验记录的缓存。
            try
            {
                File.WriteAllText(GetHashPath(target), expectedHash);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "无法写入 Authlib-Injector 校验记录：{Path}", target);
            }
        }
        catch (Exception exception) when (exception is DownloadException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "下载 Authlib-Injector 失败。");
            return Result<AuthlibInjectorArtifact>.Fail(
                "authlibinjector.download_failed",
                $"下载 Authlib-Injector 失败：{exception.Message}");
        }

        logger.LogInformation("Authlib-Injector {Version} 已就绪：{Path}", version, target);
        return Result<AuthlibInjectorArtifact>.Ok(new AuthlibInjectorArtifact(target, version));
    }

    public string? TryGetCachedJarPath()
    {
        if (!Directory.Exists(paths.InjectorDirectory))
        {
            return null;
        }

        foreach (var path in Directory
                     .EnumerateFiles(paths.InjectorDirectory, $"{JarPrefix}*{JarSuffix}")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var fileName = Path.GetFileName(path);
            if (!fileName.StartsWith(JarPrefix, StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(JarSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = fileName[JarPrefix.Length..^JarSuffix.Length];
            if (!IsSafeVersion(version))
            {
                continue;
            }

            var expectedHash = TryReadCachedHash(path);
            if (expectedHash is null || !HasExpectedHash(path, expectedHash))
            {
                continue;
            }

            return path;
        }

        return null;
    }

    private static string? GetCachePath(string injectorDirectory, string version)
    {
        var root = Path.GetFullPath(injectorDirectory);
        var target = Path.GetFullPath(Path.Combine(root, $"{JarPrefix}{version}{JarSuffix}"));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    private static bool IsSafeVersion(string version) =>
        version.Length is > 0 and <= 64 &&
        version.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '+');

    private static bool IsValidSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string GetHashPath(string jarPath) => $"{jarPath}.sha256";

    private static void DeleteCachedHash(string jarPath)
    {
        try
        {
            var hashPath = GetHashPath(jarPath);
            if (File.Exists(hashPath))
            {
                File.Delete(hashPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 删除失败不应阻止重新下载。
        }
    }

    private static string? TryReadCachedHash(string jarPath)
    {
        try
        {
            var hashPath = GetHashPath(jarPath);
            if (!File.Exists(hashPath))
            {
                return null;
            }

            var value = File.ReadAllText(hashPath).Trim().ToLowerInvariant();
            return IsValidSha256(value) ? value : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasExpectedHash(string path, string expectedSha256)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record ArtifactManifest
    {
        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; init; }

        [JsonPropertyName("checksums")]
        public ArtifactChecksums? Checksums { get; init; }
    }

    private sealed record ArtifactChecksums
    {
        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }
    }
}
