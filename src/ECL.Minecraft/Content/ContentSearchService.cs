// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Modrinth;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Content;

public interface IContentSearchService
{
    Task<Result<ContentSearchResult>> SearchAsync(
        ContentSearchQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 统一搜索：把 CurseForge 与 Modrinth 的结果映射成同一种 <see cref="ContentHit"/>。
/// “全部”模式下两个平台会并行请求并交错排列，因此同名项目同时出现是预期行为。
/// </summary>
public sealed class ContentSearchService(
    ICurseForgeService curseForge,
    ICurseForgeClient curseForgeClient,
    IModrinthClient modrinth,
    ILogger<ContentSearchService>? logger = null) : IContentSearchService
{
    public async Task<Result<ContentSearchResult>> SearchAsync(
        ContentSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var wantsCurseForge = query.Source is ContentSourceFilter.All or ContentSourceFilter.CurseForge;
        var wantsModrinth = query.Source is ContentSourceFilter.All or ContentSourceFilter.Modrinth;

        var curseForgeTask = wantsCurseForge
            ? SearchCurseForgeAsync(query, cancellationToken)
            : Task.FromResult<SourceSearchResult?>(null);
        var modrinthTask = wantsModrinth
            ? SearchModrinthAsync(query, cancellationToken)
            : Task.FromResult<SourceSearchResult?>(null);

        await Task.WhenAll(curseForgeTask, modrinthTask).ConfigureAwait(false);

        var curseForgeResult = await curseForgeTask.ConfigureAwait(false);
        var modrinthResult = await modrinthTask.ConfigureAwait(false);
        var warnings = new List<string>();
        var totals = new Dictionary<ContentSource, int>();
        var anySuccess = false;

        var curseForgeHits = Merge(curseForgeResult, "CurseForge");
        var modrinthHits = Merge(modrinthResult, "Modrinth");

        // “全部”下交错排列，避免整个列表先被一个平台占满；
        // 同一项目在两个平台各有一份时都保留，由来源徽标区分。
        var hits = query.Source == ContentSourceFilter.All
            ? Interleave(curseForgeHits, modrinthHits)
            : [.. curseForgeHits, .. modrinthHits];

        if (!anySuccess && warnings.Count > 0)
        {
            return Result<ContentSearchResult>.Fail(
                "content.search_failed",
                string.Join(" ", warnings));
        }

        return Result<ContentSearchResult>.Ok(new ContentSearchResult(
            hits,
            totals.Values.Sum(),
            totals,
            warnings));

        IReadOnlyList<ContentHit> Merge(SourceSearchResult? result, string sourceName)
        {
            if (result is null)
            {
                return [];
            }

            if (!result.Success)
            {
                warnings.Add($"{sourceName}：{result.Error}");
                return [];
            }

            anySuccess = true;
            totals[result.Source] = result.TotalCount;
            return result.Hits;
        }
    }

    private static IReadOnlyList<ContentHit> Interleave(
        IReadOnlyList<ContentHit> first,
        IReadOnlyList<ContentHit> second)
    {
        var merged = new List<ContentHit>(first.Count + second.Count);
        var max = Math.Max(first.Count, second.Count);
        for (var index = 0; index < max; index++)
        {
            if (index < first.Count)
            {
                merged.Add(first[index]);
            }

            if (index < second.Count)
            {
                merged.Add(second[index]);
            }
        }

        return merged;
    }

    private async Task<SourceSearchResult?> SearchCurseForgeAsync(
        ContentSearchQuery query,
        CancellationToken cancellationToken)
    {
        if (!curseForge.Status.IsConfigured)
        {
            return new SourceSearchResult(ContentSource.CurseForge, false, [], 0, "未配置 CurseForge 访问。");
        }

        try
        {
            var loader = ContentKinds.UsesLoader(query.Kind)
                ? CurseForgeLoaders.FromInstanceLoader(query.Loader)
                : null;

            var result = await curseForge
                .SearchAsync(
                    new CurseForgeSearchQuery
                    {
                        SearchFilter = query.Query,
                        GameVersion = query.GameVersion,
                        Loader = loader,
                        ClassId = ContentKinds.CurseForgeClassId(query.Kind),
                        Index = Math.Max(0, query.Offset),
                        PageSize = Math.Clamp(query.PageSize, 1, 50),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return new SourceSearchResult(
                    ContentSource.CurseForge,
                    false,
                    [],
                    0,
                    result.Error?.ToString() ?? "搜索失败。");
            }

            var mods = result.Value!.Mods;
            var curseForgeSizes = await ResolveCurseForgeFileSizesAsync(mods, cancellationToken)
                .ConfigureAwait(false);
            var hits = mods
                .Select(mod => new ContentHit(
                    ContentSource.CurseForge,
                    mod.Id.ToString(),
                    mod.Slug,
                    mod.Name,
                    mod.Summary ?? string.Empty,
                    Author: DescribeCurseForgeAuthors(mod.Authors),
                    mod.DownloadCount,
                    mod.DateModified,
                    mod.WebsiteUrl,
                    mod.IconUrl,
                    query.Kind)
                {
                    // 搜索响应只带 LatestFilesIndexes：用它声明“最近文件”覆盖的版本与加载器，
                    // 不足以证明整个项目支持某个版本，但可以作为列表上的真实来源字段。
                    GameVersions = mod.LatestFilesIndexes
                        .Select(index => index.GameVersion)
                        .Where(value => !string.IsNullOrWhiteSpace(value) && !ContentLoaders.TryParse(value, out _))
                        .Select(value => value!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    Loaders = mod.LatestFilesIndexes
                        .Select(index => (CurseForgeModLoader)index.ModLoader)
                        .Where(loader => Enum.IsDefined(loader))
                        .Select(CurseForgeLoaders.DisplayName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    FileSize = curseForgeSizes.TryGetValue(mod.Id, out var size) ? size : null,
                })
                .ToArray();

            return new SourceSearchResult(
                ContentSource.CurseForge,
                true,
                hits,
                result.Value.TotalCount,
                null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            return new SourceSearchResult(ContentSource.CurseForge, false, [], 0, exception.Message);
        }
    }

    private async Task<SourceSearchResult?> SearchModrinthAsync(
        ContentSearchQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await modrinth
                .SearchAsync(
                    new ModrinthSearchQuery
                    {
                        Query = query.Query,
                        ProjectType = ToModrinthType(query.Kind),
                        GameVersion = query.GameVersion,
                        Loader = ContentKinds.UsesLoader(query.Kind) ? query.Loader : null,
                        Offset = Math.Max(0, query.Offset),
                        PageSize = Math.Clamp(query.PageSize, 1, 100),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return new SourceSearchResult(
                    ContentSource.Modrinth,
                    false,
                    [],
                    0,
                    result.Error?.ToString() ?? "搜索失败。");
            }

            var searchHits = result.Value!.Hits;
            var modrinthSizes = await ResolveModrinthFileSizesAsync(searchHits, cancellationToken)
                .ConfigureAwait(false);
            var hits = searchHits
                .Select(hit => new ContentHit(
                    ContentSource.Modrinth,
                    hit.ProjectId,
                    hit.Slug,
                    hit.Title,
                    hit.Description,
                    hit.Author,
                    hit.Downloads,
                    hit.DateModified,
                    hit.WebsiteUrl,
                    hit.IconUrl,
                    query.Kind)
                {
                    // Modrinth 搜索命中直接声明支持的 Minecraft 版本与 categories（加载器/分类）。
                    GameVersions = hit.Versions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Loaders = hit.Categories
                        .Where(category => ContentLoaders.TryParse(category, out _))
                        .Select(ContentLoaders.Normalize)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    Tags = hit.Categories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    FileSize = modrinthSizes.TryGetValue(hit.ProjectId, out var size) ? size : null,
                })
                .ToArray();

            return new SourceSearchResult(
                ContentSource.Modrinth,
                true,
                hits,
                result.Value.TotalHits,
                null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            return new SourceSearchResult(ContentSource.Modrinth, false, [], 0, exception.Message);
        }
    }

    /// <summary>CurseForge 搜索响应带 authors；只展示真实作者名，缺失时保持 null（页面显示作者未知）。</summary>
    private static string? DescribeCurseForgeAuthors(IReadOnlyList<CurseForgeModAuthor> authors)
    {
        var names = authors
            .Select(author => author.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length == 0 ? null : string.Join(" / ", names);
    }

    /// <summary>
    /// 搜索响应不包含文件大小。CurseForge 的 <c>LatestFilesIndexes</c> 每个条目都带 fileId，
    /// 因此按“正式版优先、次选最新文件”挑出主要文件，一次批量查询真实字节数；
    /// 查询失败只让大小保持未知，不改变搜索结果本身。
    /// </summary>
    private async Task<IReadOnlyDictionary<int, long>> ResolveCurseForgeFileSizesAsync(
        IReadOnlyList<CurseForgeMod> mods,
        CancellationToken cancellationToken)
    {
        var chosen = new Dictionary<int, int>();
        foreach (var mod in mods)
        {
            var index = mod.LatestFilesIndexes
                .Where(item => item.FileId > 0)
                .OrderBy(item => item.ReleaseType == 1 ? 0 : 1)
                .ThenByDescending(item => item.FileId)
                .FirstOrDefault();
            if (index is not null)
            {
                chosen[mod.Id] = index.FileId;
            }
        }

        if (chosen.Count == 0)
        {
            return new Dictionary<int, long>();
        }

        try
        {
            var files = await curseForgeClient
                .GetFilesAsync(chosen.Values.Distinct().Take(100).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            if (!files.IsSuccess)
            {
                logger?.LogDebug(
                    "CurseForge 搜索结果的大小补全失败，保持「大小未知」：{Error}",
                    files.Error);
                return new Dictionary<int, long>();
            }

            var lengths = files.Value!
                .Where(file => file.FileLength > 0)
                .GroupBy(file => file.Id)
                .ToDictionary(group => group.Key, group => group.First().FileLength);
            return chosen
                .Where(pair => lengths.ContainsKey(pair.Value))
                .ToDictionary(pair => pair.Key, pair => lengths[pair.Value]);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or TaskCanceledException)
        {
            logger?.LogDebug(exception, "CurseForge 搜索结果的大小补全失败，保持「大小未知」。");
            return new Dictionary<int, long>();
        }
    }

    /// <summary>
    /// Modrinth 搜索命中带 <c>latest_version</c>，但同样不含文件大小；
    /// 用一次批量 <c>/versions?ids=[...]</c> 查询取回主文件大小。失败时保持未知。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, long>> ResolveModrinthFileSizesAsync(
        IReadOnlyList<ModrinthSearchHit> hits,
        CancellationToken cancellationToken)
    {
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits)
        {
            if (!string.IsNullOrWhiteSpace(hit.ProjectId) &&
                !string.IsNullOrWhiteSpace(hit.LatestVersion) &&
                !latest.ContainsKey(hit.ProjectId))
            {
                latest[hit.ProjectId] = hit.LatestVersion;
            }
        }

        if (latest.Count == 0)
        {
            return new Dictionary<string, long>();
        }

        try
        {
            var versions = await modrinth
                .GetVersionsByIdsAsync(latest.Values.Distinct(StringComparer.Ordinal).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            if (!versions.IsSuccess)
            {
                logger?.LogDebug(
                    "Modrinth 搜索结果的大小补全失败，保持「大小未知」：{Error}",
                    versions.Error);
                return new Dictionary<string, long>();
            }

            var sizes = versions.Value!
                .Where(version => version.PrimaryFile is { Size: > 0 })
                .GroupBy(version => version.Id)
                .ToDictionary(group => group.Key, group => group.First().PrimaryFile!.Size);
            return latest
                .Where(pair => sizes.ContainsKey(pair.Value))
                .ToDictionary(pair => pair.Key, pair => sizes[pair.Value]);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or TaskCanceledException)
        {
            logger?.LogDebug(exception, "Modrinth 搜索结果的大小补全失败，保持「大小未知」。");
            return new Dictionary<string, long>();
        }
    }

    private static ModrinthProjectType ToModrinthType(ContentKind kind) => kind switch
    {
        ContentKind.Mod => ModrinthProjectType.Mod,
        ContentKind.ModPack => ModrinthProjectType.ModPack,
        ContentKind.ResourcePack => ModrinthProjectType.ResourcePack,
        ContentKind.ShaderPack => ModrinthProjectType.Shader,
        ContentKind.DataPack => ModrinthProjectType.DataPack,
        _ => ModrinthProjectType.Mod,
    };

    private sealed record SourceSearchResult(
        ContentSource Source,
        bool Success,
        IReadOnlyList<ContentHit> Hits,
        int TotalCount,
        string? Error);
}
