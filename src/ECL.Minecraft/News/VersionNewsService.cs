// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Download;
using Ecl.Minecraft.Versions;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.News;

/// <summary>主页“新版本情报”的一条记录。</summary>
public sealed record VersionNewsItem(
    string Version,
    string Type,
    DateTimeOffset? ReleasedAt,
    string Url);

public interface IVersionNewsService
{
    /// <summary>读取 Mojang 版本清单中的最新版本；用于主页的未来新闻接口。</summary>
    Task<Result<IReadOnlyList<VersionNewsItem>>> GetLatestAsync(
        int count = 6,
        CancellationToken cancellationToken = default);
}

public sealed class MojangVersionNewsService(
    IDownloadService downloads,
    ILogger<MojangVersionNewsService> logger) : IVersionNewsService
{
    public const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    public async Task<Result<IReadOnlyList<VersionNewsItem>>> GetLatestAsync(
        int count = 6,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await downloads
                .DownloadTextAsync(
                    [new Uri(ManifestUrl)],
                    expectedHash: null,
                    expectedSize: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var manifest = JsonSerializer.Deserialize<VersionManifest>(json, EclJson.Default);
            if (manifest?.Versions is null)
            {
                return Result<IReadOnlyList<VersionNewsItem>>.Fail(
                    "news.invalid_manifest",
                    "版本清单无法解析。");
            }

            var items = manifest.Versions
                .Where(version => !string.IsNullOrWhiteSpace(version.Id))
                .Take(Math.Clamp(count, 1, 20))
                .Select(version => new VersionNewsItem(
                    version.Id,
                    version.Type,
                    DateTimeOffset.TryParse(version.ReleaseTime, out var released) ? released : null,
                    version.Url))
                .ToArray();

            return Result<IReadOnlyList<VersionNewsItem>>.Ok(items);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            logger.LogInformation(exception, "读取新版本情报失败。");
            return Result<IReadOnlyList<VersionNewsItem>>.Fail(
                "news.unavailable",
                "暂时无法获取新版本情报。",
                exception.Message);
        }
    }
}
