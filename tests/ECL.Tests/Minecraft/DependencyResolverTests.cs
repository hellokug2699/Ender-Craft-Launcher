// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Content;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Modrinth;
using Ecl.Tests.CurseForge;
using Ecl.Tests.Modrinth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 缺失依赖解析必须区分 Resolved / NotFound / Ambiguous / Incompatible，
/// 并携带稳定的来源、项目、版本与文件信息，供安装阶段直接使用。
/// </summary>
public sealed class DependencyResolverTests
{
    private static readonly DependencyResolutionContext Context = new("1.21.1", "fabric");

    [Fact]
    public async Task ResolvesModrinthProjectAndKeepsStableFileInfo()
    {
        var modrinth = new FakeModrinthClient();
        modrinth.Versions["fabric-api"] =
        [
            new ModrinthVersion
            {
                Id = "version-116",
                ProjectId = "fabric-api",
                VersionNumber = "0.116.0+1.21.1",
                VersionType = "release",
                DatePublished = DateTimeOffset.UtcNow,
                Files =
                [
                    new ModrinthFile
                    {
                        Url = "https://cdn.modrinth.com/data/fabric-api/versions/116/fabric-api.jar",
                        Filename = "fabric-api.jar",
                        Primary = true,
                        Size = 123,
                        Hashes = new ModrinthFileHashes { Sha1 = "abc" },
                    },
                ],
            },
        ];
        var resolver = CreateResolver(modrinth, new FakeCurseForgeClient());

        var result = await resolver.ResolveAsync("fabric-api", Context);

        Assert.True(result.IsResolved);
        Assert.Equal(ContentSource.Modrinth, result.Source);
        Assert.Equal("fabric-api", result.ProjectId);
        Assert.Equal("version-116", result.VersionId);
        Assert.Equal("fabric-api.jar", result.FileName);
        Assert.Equal("abc", result.Sha1);
        Assert.Equal(123, result.Size);
        Assert.Equal("cdn.modrinth.com", result.FileUrl!.Host);
    }

    [Fact]
    public async Task ReportsIncompatibleWhenProjectHasNoCompatibleVersion()
    {
        var modrinth = new FakeModrinthClient();
        var resolver = CreateResolver(modrinth, new FakeCurseForgeClient());

        var result = await resolver.ResolveAsync("fabric-api", Context);

        Assert.Equal(DependencyResolutionStatus.Incompatible, result.Status);
        Assert.Contains("没有适用于", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.FileName);
    }

    [Fact]
    public async Task FallsBackToCurseForgeWithStableProjectVersionAndFile()
    {
        var modrinth = new FakeModrinthClient();
        modrinth.MissingProjects.Add("jei");
        var curseForge = new FakeCurseForgeClient();
        curseForge.SearchResult = new CurseForgeSearchResult(
            [new CurseForgeMod { Id = 42, Slug = "jei", Name = "Just Enough Items" }],
            1,
            0,
            10);
        curseForge.ListedFiles.Add(new CurseForgeFile
        {
            Id = 7,
            ModId = 42,
            FileName = "jei-1.21.1.jar",
            DisplayName = "JEI 1.0",
            DownloadUrl = "https://edge.forgecdn.net/files/7/jei-1.21.1.jar",
            FileLength = 456,
            ReleaseType = 1,
            GameVersions = ["1.21.1", "Fabric"],
        });
        var resolver = CreateResolver(modrinth, curseForge);

        var result = await resolver.ResolveAsync("jei", Context);

        Assert.True(result.IsResolved);
        Assert.Equal(ContentSource.CurseForge, result.Source);
        Assert.Equal("42", result.ProjectId);
        Assert.Equal("jei", result.Slug);
        Assert.Equal("7", result.VersionId);
        Assert.Equal("jei-1.21.1.jar", result.FileName);
        Assert.Equal(456, result.Size);
        Assert.Equal("edge.forgecdn.net", result.FileUrl!.Host);
    }

    [Fact]
    public async Task ReportsAmbiguousWhenMultipleCurseForgeProjectsShareTheSlug()
    {
        var modrinth = new FakeModrinthClient();
        modrinth.MissingProjects.Add("shared-slug");
        var curseForge = new FakeCurseForgeClient();
        curseForge.SearchResult = new CurseForgeSearchResult(
            [
                new CurseForgeMod { Id = 1, Slug = "shared-slug", Name = "One" },
                new CurseForgeMod { Id = 2, Slug = "shared-slug", Name = "Two" },
            ],
            2,
            0,
            10);
        var resolver = CreateResolver(modrinth, curseForge);

        var result = await resolver.ResolveAsync("shared-slug", Context);

        Assert.Equal(DependencyResolutionStatus.Ambiguous, result.Status);
        Assert.Contains("多个项目", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.FileUrl);
    }

    [Fact]
    public async Task DoesNotTreatAProjectNameAsAnExactMatch()
    {
        var modrinth = new FakeModrinthClient();
        modrinth.MissingProjects.Add("display-name");
        var curseForge = new FakeCurseForgeClient();
        curseForge.SearchResult = new CurseForgeSearchResult(
            [new CurseForgeMod { Id = 9, Slug = "other-slug", Name = "display-name" }],
            1,
            0,
            10);
        curseForge.ListedFiles.Add(new CurseForgeFile
        {
            Id = 1,
            ModId = 9,
            FileName = "other.jar",
            DownloadUrl = "https://edge.forgecdn.net/files/1/other.jar",
            GameVersions = ["1.21.1", "Fabric"],
        });
        var resolver = CreateResolver(modrinth, curseForge);

        var result = await resolver.ResolveAsync("display-name", Context);

        Assert.Equal(DependencyResolutionStatus.NotFound, result.Status);
        Assert.Null(result.FileName);
    }

    private static DependencyResolver CreateResolver(
        IModrinthClient modrinth,
        ICurseForgeClient curseForge) =>
        new(modrinth, curseForge, NullLogger<DependencyResolver>.Instance);
}
