// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Modrinth;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Content;

/// <summary>
/// 某项目的版本清单。列表按发布时间从新到旧排序；<see cref="IsComplete"/> 为 false 表示来源只返回了
/// 一页版本（例如 CurseForge 客户端的分页上限），页面必须如实说明，不能把不完整列表叫作「全部版本」。
/// </summary>
public sealed record ContentVersionCatalogResult(
    IReadOnlyList<ContentVersion> Versions,
    bool IsComplete,
    IReadOnlyList<string> Warnings);

public interface IContentVersionCatalog
{
    Task<Result<ContentVersionCatalogResult>> GetVersionsAsync(
        ContentHit hit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 版本清单只做“来源 API 字段 → 统一版本记录”的映射，不做兼容性推断：
/// 版本声明了哪些 Minecraft 版本与加载器就记录哪些，缺失时保持空集合。
/// </summary>
public sealed class ContentVersionCatalog(
    ICurseForgeClient curseForge,
    IModrinthClient modrinth,
    ILogger<ContentVersionCatalog> logger) : IContentVersionCatalog
{
    /// <summary>CurseForge 客户端单页上限；列表最多读取四页（200 个版本）。</summary>
    private const int CurseForgePageSize = 50;

    private const int CurseForgeMaxPages = 4;

    public async Task<Result<ContentVersionCatalogResult>> GetVersionsAsync(
        ContentHit hit,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return hit.Source switch
            {
                ContentSource.CurseForge => await GetCurseForgeVersionsAsync(hit, cancellationToken)
                    .ConfigureAwait(false),
                ContentSource.Modrinth => await GetModrinthVersionsAsync(hit, cancellationToken)
                    .ConfigureAwait(false),
                _ => Result<ContentVersionCatalogResult>.Fail("content.invalid_source", "未知的下载来源。"),
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            logger.LogWarning(exception, "读取 {Source} 项目 {Project} 的版本清单失败。", hit.Source, hit.Id);
            return Result<ContentVersionCatalogResult>.Fail(
                "content.versions_failed",
                $"读取版本清单失败：{exception.Message}");
        }
    }

    private async Task<Result<ContentVersionCatalogResult>> GetCurseForgeVersionsAsync(
        ContentHit hit,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(hit.Id, out var projectId) || projectId <= 0)
        {
            return Result<ContentVersionCatalogResult>.Fail(
                "curseforge.invalid_project",
                "CurseForge 项目 ID 无效。");
        }

        var versions = new List<ContentVersion>();
        var seen = new HashSet<int>();
        var isComplete = true;
        for (var page = 0; page < CurseForgeMaxPages; page++)
        {
            var pageResult = await curseForge
                .GetFilesForModAsync(
                    projectId,
                    gameVersion: null,
                    loader: null,
                    pageSize: CurseForgePageSize,
                    index: page * CurseForgePageSize,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!pageResult.IsSuccess)
            {
                // 第一页就失败时把错误交给页面；后续页失败时保留已取得的版本并明确说明不完整。
                if (versions.Count == 0)
                {
                    return Result<ContentVersionCatalogResult>.Fail(pageResult.Error!);
                }

                logger.LogWarning(
                    "CurseForge 项目 {ProjectId} 的版本清单第 {Page} 页读取失败：{Error}",
                    projectId,
                    page + 1,
                    pageResult.Error);
                isComplete = false;
                break;
            }

            var added = 0;
            foreach (var file in pageResult.Value!)
            {
                if (file.Id > 0 && !seen.Add(file.Id))
                {
                    continue;
                }

                versions.Add(ToContentVersion(projectId, file));
                added++;
            }

            if (pageResult.Value!.Count < CurseForgePageSize)
            {
                break;
            }

            if (added == 0)
            {
                // 来源返回了满页但全部重复：继续翻页已无意义。
                isComplete = false;
                break;
            }

            if (page == CurseForgeMaxPages - 1)
            {
                isComplete = false;
            }
        }

        return Result<ContentVersionCatalogResult>.Ok(new ContentVersionCatalogResult(
            SortByPublished(versions),
            isComplete,
            []));
    }

    private async Task<Result<ContentVersionCatalogResult>> GetModrinthVersionsAsync(
        ContentHit hit,
        CancellationToken cancellationToken)
    {
        var result = await modrinth
            .GetVersionsAsync(hit.Id, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Result<ContentVersionCatalogResult>.Fail(result.Error!);
        }

        var versions = result.Value!
            .Select(version => ToContentVersion(hit.Id, version))
            .ToArray();
        return Result<ContentVersionCatalogResult>.Ok(new ContentVersionCatalogResult(
            SortByPublished(versions),
            true,
            []));
    }

    private static IReadOnlyList<ContentVersion> SortByPublished(IEnumerable<ContentVersion> versions) =>
        versions
            .OrderByDescending(version => version.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
            .ToArray();

    private static ContentVersion ToContentVersion(int projectId, CurseForgeFile file)
    {
        var loaders = file.GameVersions
            .Where(value => ContentLoaders.TryParse(value, out _))
            .Select(value => ContentLoaders.Normalize(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var gameVersions = file.GameVersions
            .Where(value => !ContentLoaders.TryParse(value, out _) && !ContentEnvironments.IsTag(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ContentVersion(
            ContentSource.CurseForge,
            projectId.ToString(),
            file.Id.ToString(),
            string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName,
            gameVersions,
            loaders,
            file.FileName,
            file.FileLength > 0 ? file.FileLength : null,
            file.FileDate);
    }

    private static ContentVersion ToContentVersion(string projectId, ModrinthVersion version)
    {
        var file = version.PrimaryFile;
        return new ContentVersion(
            ContentSource.Modrinth,
            string.IsNullOrWhiteSpace(version.ProjectId) ? projectId : version.ProjectId,
            version.Id,
            string.IsNullOrWhiteSpace(version.VersionNumber) ? version.Name ?? version.Id : version.VersionNumber,
            version.GameVersions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            version.Loaders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            file?.Filename ?? string.Empty,
            file is { Size: > 0 } ? file.Size : null,
            version.DatePublished);
    }
}

/// <summary>
/// CurseForge 在文件的 <c>gameVersions</c> 里混入 <c>Client</c>/<c>Server</c> 环境标签；
/// 它们不是 Minecraft 版本，展示「适用版本」时必须排除，否则会出现「1.21.1 – Client」。
/// </summary>
public static class ContentEnvironments
{
    public static bool IsTag(string? value) => value?.Trim().ToLowerInvariant() is "client" or "server";
}

/// <summary>来源侧加载器词汇的统一解析。只识别真实存在的加载器名，不把任意分类当作加载器。</summary>
public static class ContentLoaders
{
    public static bool TryParse(string? value, out string canonical)
    {
        canonical = Normalize(value);
        return canonical.Length > 0;
    }

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "forge" or "cleanroom" => "forge",
        "fabric" => "fabric",
        "neoforge" => "neoforge",
        "quilt" => "quilt",
        _ => string.Empty,
    };

    /// <summary>最近的加载器模式匹配：来源未声明加载器时返回 false（未知不等于兼容）。</summary>
    public static bool Matches(string? filter, string? declared) =>
        string.Equals(Normalize(filter), Normalize(declared), StringComparison.Ordinal);

    public static string DisplayName(string value) => Normalize(value) switch
    {
        "forge" => "Forge / Cleanroom",
        "fabric" => "Fabric",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        _ => string.Empty,
    };
}
