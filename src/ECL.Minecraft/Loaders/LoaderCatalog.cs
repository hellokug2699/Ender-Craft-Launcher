// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Download;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Loaders;

/// <summary>
/// 加载器与组件的公开版本目录。网络细节集中在这里，安装流程只消费结构化结果。
/// </summary>
public sealed class LoaderCatalog(
    IDownloadService downloads,
    ILogger<LoaderCatalog> logger) : ILoaderCatalog
{
    private const string FabricMetaRoot = "https://meta.fabricmc.net/v2/versions/loader";
    private const string FabricReleasesAtom = "https://github.com/FabricMC/fabric-loader/releases.atom";
    private const string CleanroomMetadata =
        "https://repo.cleanroommc.com/releases/com/cleanroommc/cleanroom/maven-metadata.xml";
    private const string CleanroomReleasesAtom = "https://github.com/CleanroomMC/Cleanroom/releases.atom";
    private const string FabricApiVersionsRoot = "https://api.modrinth.com/v2/project/fabric-api/version";
    private const string OptiFineRoot = "https://bmclapi2.bangbang93.com/optifine";

    public async Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetFabricLoadersAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await DownloadTextAsync(
                    $"{FabricMetaRoot}/{Uri.EscapeDataString(gameVersion.Trim())}",
                    cancellationToken)
                .ConfigureAwait(false);

            var dates = await TryGetAtomDatesAsync(FabricReleasesAtom, cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyList<LoaderVersionInfo>>.Ok(
                LoaderCatalogParsers.ParseFabricLoaders(json, dates));
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            logger.LogWarning(exception, "读取 Fabric 加载器版本失败：{GameVersion}", gameVersion);
            return Result<IReadOnlyList<LoaderVersionInfo>>.Fail(
                "loader.catalog_failed",
                $"读取 Fabric 加载器版本失败：{exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetCleanroomVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(gameVersion.Trim(), "1.12.2", StringComparison.OrdinalIgnoreCase))
        {
            return Result<IReadOnlyList<LoaderVersionInfo>>.Ok([]);
        }

        try
        {
            var xml = await DownloadTextAsync(CleanroomMetadata, cancellationToken).ConfigureAwait(false);
            var dates = await TryGetAtomDatesAsync(CleanroomReleasesAtom, cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyList<LoaderVersionInfo>>.Ok(
                LoaderCatalogParsers.ParseCleanroomVersions(xml, dates));
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            logger.LogWarning(exception, "读取 Cleanroom 版本失败。");
            return Result<IReadOnlyList<LoaderVersionInfo>>.Fail(
                "loader.catalog_failed",
                $"读取 Cleanroom 版本失败：{exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<FabricApiVersionInfo>>> GetFabricApiVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var gameVersions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion.Trim() }));
            var loaders = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { "fabric" }));
            var json = await DownloadTextAsync(
                    $"{FabricApiVersionsRoot}?game_versions={gameVersions}&loaders={loaders}",
                    cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<FabricApiVersionInfo>>.Ok(
                LoaderCatalogParsers.ParseFabricApiVersions(json));
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            logger.LogWarning(exception, "读取 Fabric API 版本失败：{GameVersion}", gameVersion);
            return Result<IReadOnlyList<FabricApiVersionInfo>>.Fail(
                "loader.catalog_failed",
                $"读取 Fabric API 版本失败：{exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<OptiFineVersionInfo>>> GetOptiFineVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await DownloadTextAsync(
                    $"{OptiFineRoot}/{Uri.EscapeDataString(gameVersion.Trim())}",
                    cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<OptiFineVersionInfo>>.Ok(
                LoaderCatalogParsers.ParseOptiFineVersions(json));
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            logger.LogWarning(exception, "读取 OptiFine 版本失败：{GameVersion}", gameVersion);
            return Result<IReadOnlyList<OptiFineVersionInfo>>.Fail(
                "loader.catalog_failed",
                $"读取 OptiFine 版本失败：{exception.Message}");
        }
    }

    public async Task<Result<string>> GetFabricProfileAsync(
        string gameVersion,
        string loaderVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await DownloadTextAsync(
                    $"{FabricMetaRoot}/{Uri.EscapeDataString(gameVersion.Trim())}/{Uri.EscapeDataString(loaderVersion.Trim())}/profile/json",
                    cancellationToken)
                .ConfigureAwait(false);

            var profile = JsonSerializer.Deserialize<Versions.MinecraftVersion>(json, EclJson.Default);
            if (profile is null || string.IsNullOrWhiteSpace(profile.MainClass))
            {
                return Result<string>.Fail("loader.invalid_profile", "Fabric 安装配置无效。");
            }

            return Result<string>.Ok(json);
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            logger.LogWarning(exception, "读取 Fabric 安装配置失败：{GameVersion} / {LoaderVersion}", gameVersion, loaderVersion);
            return Result<string>.Fail(
                "loader.catalog_failed",
                $"读取 Fabric 安装配置失败：{exception.Message}");
        }
    }

    private Task<string> DownloadTextAsync(string url, CancellationToken cancellationToken) =>
        downloads.DownloadTextAsync([new Uri(url)], cancellationToken: cancellationToken);

    private async Task<IReadOnlyDictionary<string, DateTimeOffset>> TryGetAtomDatesAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var xml = await DownloadTextAsync(url, cancellationToken).ConfigureAwait(false);
            return LoaderCatalogParsers.ParseAtomDates(xml);
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            // 更新时间是辅助信息，失败时不影响版本列表。
            logger.LogDebug(exception, "读取更新时间失败：{Url}", url);
            return new Dictionary<string, DateTimeOffset>();
        }
    }

    private static bool IsCatalogFailure(Exception exception) =>
        exception is DownloadException or JsonException or HttpRequestException or IOException or UriFormatException
            or System.Xml.XmlException;
}
