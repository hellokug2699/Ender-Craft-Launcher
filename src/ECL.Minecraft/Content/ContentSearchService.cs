// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Modrinth;

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
    IModrinthClient modrinth) : IContentSearchService
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

            var hits = result.Value!.Mods
                .Select(mod => new ContentHit(
                    ContentSource.CurseForge,
                    mod.Id.ToString(),
                    mod.Slug,
                    mod.Name,
                    mod.Summary ?? string.Empty,
                    Author: null,
                    mod.DownloadCount,
                    mod.DateModified,
                    mod.WebsiteUrl,
                    mod.IconUrl,
                    query.Kind))
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

            var hits = result.Value!.Hits
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
                    query.Kind))
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
