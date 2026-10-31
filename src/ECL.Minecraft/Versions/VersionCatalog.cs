// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Download;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Versions;

/// <summary>Mojang 版本清单中的版本类型。旧版 alpha/beta 不属于创建实例的主流程。</summary>
public enum MinecraftVersionKind
{
    Release,
    Snapshot,
    AprilFools,
}

/// <summary>可供实例创建选择的 Minecraft 版本。</summary>
public sealed record MinecraftGameVersion(
    string Id,
    MinecraftVersionKind Kind,
    DateTimeOffset? ReleasedAt);

/// <summary>
/// 读取 Mojang 版本清单，按“正式版 / 快照版 / 愚人节版”分类。
/// 版本清单较长，读取结果在进程内缓存一段时间；用户可以显式刷新。
/// </summary>
public interface IVersionCatalog
{
    Task<Result<IReadOnlyList<MinecraftGameVersion>>> GetVersionsAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default);
}

public sealed class MojangVersionCatalog(
    IDownloadService downloads,
    DownloadSourceResolver sources,
    ILogger<MojangVersionCatalog> logger) : IVersionCatalog
{
    /// <summary>
    /// Mojang 版本清单里标记为 snapshot、但实际是愚人节玩笑的版本。
    /// 不能只用发布日期判断：21w13a、26w14a 等正常快照也在 4 月 1 日发布。
    /// 新的愚人节版本需要加入此列表。
    /// </summary>
    private static readonly HashSet<string> AprilFoolsVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        "15w14a",
        "1.RV-Pre1",
        "3D Shareware v1.34",
        "20w14infinite",
        "22w13oneblockatatime",
        "23w13a_or_b",
        "24w14potato",
        "25w14craftmine",
    };

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<MinecraftGameVersion>? _cache;
    private DateTimeOffset _cachedAt;

    public async Task<Result<IReadOnlyList<MinecraftGameVersion>>> GetVersionsAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!refresh && TryGetCached(out var cached))
        {
            return Result<IReadOnlyList<MinecraftGameVersion>>.Ok(cached);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && TryGetCached(out cached))
            {
                return Result<IReadOnlyList<MinecraftGameVersion>>.Ok(cached);
            }

            string json;
            try
            {
                json = await downloads
                    .DownloadTextAsync(sources.GetManifestCandidates(), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogInformation(exception, "读取 Minecraft 版本清单失败。");
                return Result<IReadOnlyList<MinecraftGameVersion>>.Fail(
                    "versions.unavailable",
                    "无法读取 Minecraft 版本清单，请检查网络或下载源设置。",
                    exception.Message);
            }

            VersionManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<VersionManifest>(json, EclJson.Default);
            }
            catch (JsonException exception)
            {
                return Result<IReadOnlyList<MinecraftGameVersion>>.Fail(
                    "versions.invalid_manifest",
                    "Minecraft 版本清单无法解析。",
                    exception.Message);
            }

            if (manifest?.Versions is null || manifest.Versions.Count == 0)
            {
                return Result<IReadOnlyList<MinecraftGameVersion>>.Fail(
                    "versions.invalid_manifest",
                    "Minecraft 版本清单为空。");
            }

            var versions = manifest.Versions
                .Where(version => !string.IsNullOrWhiteSpace(version.Id))
                .Select(version => Create(version.Id, version.Type, version.ReleaseTime))
                .Where(version => version is not null)
                .Cast<MinecraftGameVersion>()
                .ToArray();

            _cache = versions;
            _cachedAt = DateTimeOffset.UtcNow;
            return Result<IReadOnlyList<MinecraftGameVersion>>.Ok(versions);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>把清单条目映射为可选择的版本；不支持的旧版本返回 null。</summary>
    public static MinecraftGameVersion? Create(string id, string type, string? releaseTime)
    {
        var kind = Classify(id, type);
        if (kind is null)
        {
            return null;
        }

        var releasedAt = DateTimeOffset.TryParse(releaseTime, out var parsed) ? parsed : (DateTimeOffset?)null;
        return new MinecraftGameVersion(id, kind.Value, releasedAt);
    }

    /// <summary>按清单 type 与已知愚人节版本列表分类；alpha/beta 等不参与实例创建。</summary>
    public static MinecraftVersionKind? Classify(string id, string type)
    {
        if (string.Equals(type, "snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return AprilFoolsVersions.Contains(id)
                ? MinecraftVersionKind.AprilFools
                : MinecraftVersionKind.Snapshot;
        }

        return string.Equals(type, "release", StringComparison.OrdinalIgnoreCase)
            ? MinecraftVersionKind.Release
            : null;
    }

    private bool TryGetCached(out IReadOnlyList<MinecraftGameVersion> versions)
    {
        var cached = _cache;
        if (cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
        {
            versions = cached;
            return true;
        }

        versions = [];
        return false;
    }
}
