// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Content;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Modrinth;
using Ecl.Tests.CurseForge;
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
        var service = new ContentSearchService(curseForge, new FakeCurseForgeClient(), modrinth);

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
        var service = new ContentSearchService(curseForge, new FakeCurseForgeClient(), modrinth);

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
        var service = new ContentSearchService(curseForge, new FakeCurseForgeClient(), modrinth);

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
        var service = new ContentSearchService(curseForge, new FakeCurseForgeClient(), modrinth);

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

    [Fact]
    public async Task CurseForgeHitsCarryTheAuthorNamesFromTheSearchResponse()
    {
        var curseForge = new FakeCurseForgeService
        {
            SearchResult = new CurseForgeSearchResult([CreateCurseForgeMod(7, "jei", "JEI")], 1, 0, 40),
        };
        var service = new ContentSearchService(
            curseForge,
            new FakeCurseForgeClient(),
            new FakeModrinthClient());

        var result = await service.SearchAsync(new ContentSearchQuery
        {
            Kind = ContentKind.Mod,
            Source = ContentSourceFilter.CurseForge,
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal("Author 7", Assert.Single(result.Value!.Hits).Author);
    }

    [Fact]
    public async Task SearchEnrichesPrimaryFileSizesFromOneBatchPerSource()
    {
        var curseForge = new FakeCurseForgeService
        {
            SearchResult = new CurseForgeSearchResult(
                [
                    new CurseForgeMod
                    {
                        Id = 1,
                        Slug = "jei",
                        Name = "JEI",
                        LatestFilesIndexes =
                        [
                            new CurseForgeFileIndex
                            {
                                FileId = 10,
                                GameVersion = "1.21.1",
                                ReleaseType = 1,
                                ModLoader = (int)CurseForgeModLoader.Fabric,
                            },
                        ],
                    },
                ],
                1,
                0,
                40),
        };
        var curseForgeClient = new FakeCurseForgeClient();
        curseForgeClient.Files[10] = new CurseForgeFile
        {
            Id = 10,
            ModId = 1,
            FileName = "jei.jar",
            FileLength = 4096,
            GameVersions = ["1.21.1"],
        };
        var modrinth = new FakeModrinthClient();
        modrinth.SearchHits.Add(CreateModrinthHit("aaa", "sodium", "Sodium") with { LatestVersion = "v1" });
        modrinth.VersionsById["v1"] = new ModrinthVersion
        {
            Id = "v1",
            ProjectId = "aaa",
            VersionNumber = "1.0",
            Files =
            [
                new ModrinthFile
                {
                    Filename = "sodium.jar",
                    Url = "https://cdn.modrinth.com/data/aaa/versions/v1/sodium.jar",
                    Primary = true,
                    Size = 2048,
                },
            ],
        };
        var service = new ContentSearchService(curseForge, curseForgeClient, modrinth);

        var result = await service.SearchAsync(new ContentSearchQuery
        {
            Kind = ContentKind.Mod,
            Source = ContentSourceFilter.All,
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var curseForgeHit = result.Value!.Hits.Single(hit => hit.Source == ContentSource.CurseForge);
        var modrinthHit = result.Value.Hits.Single(hit => hit.Source == ContentSource.Modrinth);
        Assert.Equal(4096, curseForgeHit.FileSize);
        Assert.Equal(2048, modrinthHit.FileSize);
        Assert.True(curseForgeClient.GetFilesCalls > 0);
    }

    private static CurseForgeMod CreateCurseForgeMod(int id, string slug, string name) => new()
    {
        Id = id,
        Slug = slug,
        Name = name,
        Summary = "CurseForge project",
        DownloadCount = 100,
        Authors = [new CurseForgeModAuthor { Id = id, Name = $"Author {id}" }],
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
