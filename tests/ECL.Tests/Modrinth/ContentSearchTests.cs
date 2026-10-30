// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Content;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Modrinth;
using Xunit;

namespace Ecl.Tests.Modrinth;

public sealed class ContentSearchTests
{
    [Fact]
    public async Task AllModeInterleavesBothSourcesAndKeepsDuplicateNames()
    {
        var curseForge = new FakeCurseForgeService
        {
            SearchResult = new CurseForgeSearchResult(
                [CreateCurseForgeMod(1, "jei", "JEI"), CreateCurseForgeMod(2, "sodium", "Sodium")],
                2,
                0,
                40),
        };
        var modrinth = new FakeModrinthClient();
        modrinth.SearchHits.Add(CreateModrinthHit("aaa", "jei", "JEI"));
        modrinth.SearchHits.Add(CreateModrinthHit("bbb", "sodium", "Sodium"));
        var service = new ContentSearchService(curseForge, modrinth);

        var result = await service.SearchAsync(new ContentSearchQuery
        {
            Kind = ContentKind.Mod,
            Source = ContentSourceFilter.All,
            Query = "jei",
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var hits = result.Value!.Hits;
        Assert.Equal(4, hits.Count);
        Assert.Equal(ContentSource.CurseForge, hits[0].Source);
        Assert.Equal(ContentSource.Modrinth, hits[1].Source);
        Assert.Equal("JEI", hits[0].Name);
        Assert.Equal("JEI", hits[1].Name);
        Assert.Equal(2, hits.Count(hit => hit.Name == "JEI"));
        Assert.Equal(4, result.Value.TotalCount);
    }

    [Fact]
    public async Task SourceFilterOnlyQueriesTheSelectedPlatform()
    {
        var curseForge = new FakeCurseForgeService
        {
            SearchResult = new CurseForgeSearchResult([CreateCurseForgeMod(1, "jei", "JEI")], 1, 0, 40),
        };
        var modrinth = new FakeModrinthClient();
        var service = new ContentSearchService(curseForge, modrinth);

        var result = await service.SearchAsync(new ContentSearchQuery
        {
            Kind = ContentKind.Mod,
            Source = ContentSourceFilter.CurseForge,
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Single(result.Value!.Hits);
        Assert.Equal(1, curseForge.SearchCalls);
        Assert.Equal(0, modrinth.SearchCalls);
    }

    [Fact]
    public async Task ModrinthOnlyFilterUsesProjectTypeAndLoader()
    {
        var curseForge = new FakeCurseForgeService();
        var modrinth = new FakeModrinthClient();
        modrinth.SearchHits.Add(CreateModrinthHit("aaa", "sodium", "Sodium"));
        var service = new ContentSearchService(curseForge, modrinth);

        var result = await service.SearchAsync(new ContentSearchQuery
        {
            Kind = ContentKind.ShaderPack,
            Source = ContentSourceFilter.Modrinth,
            GameVersion = "1.21.1",
            Loader = "fabric",
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(0, curseForge.SearchCalls);
        Assert.Equal(ModrinthProjectType.Shader, modrinth.LastQuery!.ProjectType);
        Assert.Null(modrinth.LastQuery.Loader);
        Assert.Equal("1.21.1", modrinth.LastQuery.GameVersion);
        Assert.Equal(ContentKind.ShaderPack, Assert.Single(result.Value!.Hits).Kind);
    }

    [Fact]
    public async Task CurseForgeClassIdsFollowContentKind()
    {
        var curseForge = new FakeCurseForgeService();
        var modrinth = new FakeModrinthClient();
        var service = new ContentSearchService(curseForge, modrinth);

        await service.SearchAsync(new ContentSearchQuery
        {
            Kind = ContentKind.ModPack,
            Source = ContentSourceFilter.CurseForge,
        });

        Assert.Equal(4471, ContentKinds.CurseForgeClassId(ContentKind.ModPack));
        Assert.Equal(12, ContentKinds.CurseForgeClassId(ContentKind.ResourcePack));
        Assert.Equal(6552, ContentKinds.CurseForgeClassId(ContentKind.ShaderPack));
        Assert.Equal(6945, ContentKinds.CurseForgeClassId(ContentKind.DataPack));
        Assert.Equal(6, ContentKinds.CurseForgeClassId(ContentKind.Mod));
        Assert.Equal(1, curseForge.SearchCalls);
    }

    private static CurseForgeMod CreateCurseForgeMod(int id, string slug, string name) => new()
    {
        Id = id,
        Slug = slug,
        Name = name,
        Summary = "CurseForge project",
        DownloadCount = 100,
    };

    private static ModrinthSearchHit CreateModrinthHit(string id, string slug, string title) => new()
    {
        ProjectId = id,
        Slug = slug,
        Title = title,
        Description = "Modrinth project",
        Author = "author",
        ProjectType = "mod",
        Downloads = 100,
        IconUrl = null,
    };
}
