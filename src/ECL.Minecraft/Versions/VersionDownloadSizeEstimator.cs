// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Download;
using Ecl.Minecraft.Loaders;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Versions;

/// <summary>
/// 一个下载项在安装前的真实文件大小（`download-instance-versions.md` §9）。
/// 每个字段只来自可核对的官方/加载器元数据；任一必需组成缺失时 <see cref="TotalBytes"/> 为 null，
/// 由页面显示「大小未知」，不用 0 或经验估值替代。
/// </summary>
public sealed record VersionDownloadSizes(
    long? ClientBytes,
    long? LibraryBytes,
    long? AssetBytes,
    long? LoggingBytes)
{
    public long? TotalBytes => ClientBytes is { } client
                               && LibraryBytes is { } libraries
                               && AssetBytes is { } assets
                               && LoggingBytes is { } logging
        ? client + libraries + assets + logging
        : null;
}

/// <summary>
/// 安装前产物大小估算（只读）。数据来源：
/// <list type="bullet">
/// <item>Minecraft 版本 JSON：<c>downloads.client</c>、<c>libraries[].downloads.artifact.size</c>、
/// <c>assetIndex.totalSize</c> 与 <c>logging.client.file.size</c>；</item>
/// <item>Fabric 加载器 profile JSON：<c>libraries[].size</c>（Fabric Meta 提供真实字节数）。</item>
/// </list>
/// Forge / NeoForge / Cleanroom / OptiFine 的加载器目录没有声明安装包或库的文件大小，
/// 因此这些项保持未知；不按经验估算、也不把多个不同产物混成一个数字。
/// </summary>
public interface IVersionDownloadSizeEstimator
{
    /// <summary>Minecraft 游戏文件（客户端 + 当前平台库 + 资源 + 日志配置）的总下载大小。</summary>
    Task<Result<VersionDownloadSizes>> EstimateGameFilesAsync(
        string versionId,
        string? versionJsonUrl,
        CancellationToken cancellationToken = default);

    /// <summary>Fabric 加载器 profile 声明的库文件总大小；目录未提供大小或不是 Fabric 时返回 null。</summary>
    Task<long?> EstimateFabricLoaderAsync(
        string gameVersion,
        string loaderVersion,
        CancellationToken cancellationToken = default);
}

public sealed class VersionDownloadSizeEstimator(
    IDownloadService downloads,
    DownloadSourceResolver sources,
    IVersionResolver resolver,
    ILoaderCatalog loaders,
    ILogger<VersionDownloadSizeEstimator> logger) : IVersionDownloadSizeEstimator
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, CacheEntry<VersionDownloadSizes>> _gameCache =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, CacheEntry<long?>> _loaderCache =
        new(StringComparer.Ordinal);

    public async Task<Result<VersionDownloadSizes>> EstimateGameFilesAsync(
        string versionId,
        string? versionJsonUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return Result<VersionDownloadSizes>.Fail("version.invalid_id", "版本 ID 为空。");
        }

        if (TryGetCached(_gameCache, versionId, out var cached))
        {
            return Result<VersionDownloadSizes>.Ok(cached);
        }

        var version = await ReadVersionAsync(versionId, versionJsonUrl, cancellationToken).ConfigureAwait(false);
        if (!version.IsSuccess)
        {
            return Result<VersionDownloadSizes>.Fail(version.Error!);
        }

        var sizes = Compute(version.Value!);
        _gameCache[versionId] = new CacheEntry<VersionDownloadSizes>(sizes, DateTimeOffset.UtcNow);
        return Result<VersionDownloadSizes>.Ok(sizes);
    }

    public async Task<long?> EstimateFabricLoaderAsync(
        string gameVersion,
        string loaderVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameVersion) || string.IsNullOrWhiteSpace(loaderVersion))
        {
            return null;
        }

        var key = $"{gameVersion}|{loaderVersion}";
        if (TryGetCached(_loaderCache, key, out var cached))
        {
            return cached;
        }

        long? total = null;
        try
        {
            var profile = await loaders
                .GetFabricProfileAsync(gameVersion, loaderVersion, cancellationToken)
                .ConfigureAwait(false);
            if (profile.IsSuccess && !string.IsNullOrWhiteSpace(profile.Value))
            {
                var version = JsonSerializer.Deserialize<MinecraftVersion>(profile.Value, EclJson.Default);
                total = version is null ? null : SumLibrarySizes(version);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogInformation(exception, "读取 Fabric 加载器文件大小失败：{LoaderVersion}", loaderVersion);
        }

        if (total is > 0)
        {
            _loaderCache[key] = new CacheEntry<long?>(total, DateTimeOffset.UtcNow);
        }

        return total;
    }

    private async Task<Result<MinecraftVersion>> ReadVersionAsync(
        string versionId,
        string? versionJsonUrl,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(versionJsonUrl) &&
            Uri.TryCreate(versionJsonUrl, UriKind.Absolute, out var uri))
        {
            try
            {
                var json = await downloads
                    .DownloadTextAsync(sources.GetCandidates(uri), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var remote = JsonSerializer.Deserialize<MinecraftVersion>(json, EclJson.Default);
                if (remote is not null)
                {
                    return Result<MinecraftVersion>.Ok(remote);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 大小只是辅助信息：网络失败时退回本机版本文件，两个来源都不可用才显示未知。
                logger.LogInformation(exception, "读取 Minecraft 版本文件失败：{VersionId}", versionId);
            }
        }

        return await resolver.ResolveAsync(versionId, cancellationToken).ConfigureAwait(false);
    }

    private static VersionDownloadSizes Compute(MinecraftVersion version) => new(
        version.Downloads?.Client?.Size,
        SumLibrarySizes(version),
        // 没有资源索引表示该版本没有需要下载的资源对象（0）；有索引但缺少总大小时才算未知。
        version.AssetIndex is null ? 0 : version.AssetIndex.TotalSize,
        // 没有日志配置表示没有日志文件可下载（0）；声明了日志但缺少大小时才算未知。
        version.Logging is null ? 0 : version.Logging.Client?.File?.Size);

    /// <summary>只汇总当前平台实际会下载的库/natives；任一文件缺少声明大小时整体返回 null。</summary>
    private static long? SumLibrarySizes(MinecraftVersion version)
    {
        long total = 0;
        foreach (var artifact in PlatformLibrarySelector.GetDownloadArtifacts(
                     VersionFilePlanner.PlanLibraries(version)))
        {
            if (artifact.Size is not { } size)
            {
                return null;
            }

            total += size;
        }

        return total;
    }

    private static bool TryGetCached<TKey, TValue>(
        ConcurrentDictionary<TKey, CacheEntry<TValue>> cache,
        TKey key,
        out TValue value)
        where TKey : notnull
    {
        if (cache.TryGetValue(key, out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < CacheLifetime)
        {
            value = entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    private sealed record CacheEntry<T>(T Value, DateTimeOffset CachedAt);
}
