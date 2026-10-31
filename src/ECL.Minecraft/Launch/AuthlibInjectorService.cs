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
        var expectedHash = manifest.Checksums.Sha256.Trim().ToLowerInvariant();
        var target = Path.Combine(paths.InjectorDirectory, $"{JarPrefix}{version}{JarSuffix}");

        try
        {
            Directory.CreateDirectory(paths.InjectorDirectory);

            if (File.Exists(target) && !HasExpectedHash(target, expectedHash))
            {
                logger.LogWarning("缓存的 Authlib-Injector 校验失败，将重新下载：{Path}", target);
                File.Delete(target);
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
                return Result<AuthlibInjectorArtifact>.Fail(
                    "authlibinjector.checksum_failed",
                    "Authlib-Injector 下载文件校验失败，已拒绝加载。");
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

        return Directory
            .EnumerateFiles(paths.InjectorDirectory, $"{JarPrefix}*{JarSuffix}")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
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
