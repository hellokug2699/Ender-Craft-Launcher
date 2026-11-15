// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Modrinth;
using Ecl.Minecraft.Mods;
using Ecl.Tests.CurseForge;
using Ecl.Tests.Modrinth;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Content;

/// <summary>
/// 下载中心的版本清单与“按版本安装”领域行为：
/// 版本字段只来自来源 API；文件按版本 ID 重新解析；整合包与数据包不走实例内容安装。
/// </summary>
public sealed class ContentVersionCatalogTests
{
    [Fact]
    public async Task CurseForgeVersionsMapDeclaredVersionsAndLoadersAndSortNewestFirst()
    {
        using var environment = new TestEnvironment();
        var curseForge = new FakeCurseForgeClient();
        curseForge.ListedFiles.AddRange(
        [
            new CurseForgeFile
            {
                Id = 11,
                ModId = 1,
                FileName = "demo-1.0.jar",
                DisplayName = "Demo 1.0",
                FileLength = 2048,
                FileDate = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero),
                GameVersions = ["1.20.1", "Fabric"],
                Hashes = [new CurseForgeFileHash { Algo = 1, Value = "sha1" }],
            },
            new CurseForgeFile
            {
                Id = 12,
                ModId = 1,
                FileName = "demo-2.0.jar",
                ReleaseType = 1,
                FileLength = 0,
                FileDate = new DateTimeOffset(2024, 5, 2, 0, 0, 0, TimeSpan.Zero),
                GameVersions = ["1.21.1", "Forge", "Client", "Server"],
            },
        ]);
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<ICurseForgeClient>(curseForge));

        var result = await provider.GetRequiredService<IContentVersionCatalog>()
            .GetVersionsAsync(Hit(ContentSource.CurseForge, "1", ContentKind.Mod));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var versions = result.Value!.Versions;
        Assert.Equal(["12", "11"], versions.Select(version => version.VersionId));
        Assert.True(result.Value.IsComplete);
        Assert.Equal(["1.21.1"], versions[0].GameVersions);
        Assert.Equal(["forge"], versions[0].Loaders);
        Assert.Null(versions[0].FileSize);
        Assert.Equal(2048, versions[1].FileSize);
        Assert.Equal(["fabric"], versions[1].Loaders);
        Assert.Equal("Demo 1.0", versions[1].VersionName);
    }

    [Fact]
    public async Task FullCurseForgePageWithoutNewIdsIsReportedAsIncomplete()
    {
        using var environment = new TestEnvironment();
        var curseForge = new FakeCurseForgeClient();
        curseForge.ListedFiles.AddRange(Enumerable.Range(1, 50).Select(id => new CurseForgeFile
        {
            Id = id,
            ModId = 1,
            FileName = $"demo-{id}.jar",
            GameVersions = ["1.20.1"],
        }));
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<ICurseForgeClient>(curseForge));

        var result = await provider.GetRequiredService<IContentVersionCatalog>()
            .GetVersionsAsync(Hit(ContentSource.CurseForge, "1", ContentKind.Mod));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(50, result.Value!.Versions.Count);
        Assert.False(result.Value.IsComplete);
    }

    [Fact]
    public async Task ModrinthVersionsKeepUnknownSizesAsNull()
    {
        using var environment = new TestEnvironment();
        var modrinth = new FakeModrinthClient();
        modrinth.Versions["demo"] =
        [
            Version("v2", "2.0", ["1.21.1"], ["fabric"], size: 0, new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero)),
            Version("v1", "1.0", ["1.20.1"], ["fabric"], size: 1024, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        ];
        modrinth.VersionsById["v1"] = modrinth.Versions["demo"][1];
        modrinth.VersionsById["v2"] = modrinth.Versions["demo"][0];
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IModrinthClient>(modrinth));

        var result = await provider.GetRequiredService<IContentVersionCatalog>()
            .GetVersionsAsync(Hit(ContentSource.Modrinth, "demo", ContentKind.Mod));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var versions = result.Value!.Versions;
        Assert.Equal(["v2", "v1"], versions.Select(version => version.VersionId));
        Assert.Null(versions[0].FileSize);
        Assert.Equal(1024, versions[1].FileSize);
        Assert.True(versions[0].MatchesGameVersion("1.21.1"));
        Assert.False(versions[0].MatchesGameVersion("1.20.1"));
        Assert.True(versions[0].MatchesLoader("fabric"));
    }

    [Fact]
    public async Task InstallVersionInstallsTheChosenVersionInsteadOfTheLatest()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("demo");
        using var environment = new TestEnvironment();
        var downloads = new PayloadDownloadService(payload);
        var modrinth = new FakeModrinthClient();
        var older = Version("v1", "1.0", ["1.20.1"], ["fabric"], payload.Length, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Version("v2", "2.0", ["1.20.1"], ["fabric"], payload.Length, new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero));
        modrinth.Versions["demo"] = [newer, older];
        modrinth.VersionsById["v1"] = older;
        modrinth.VersionsById["v2"] = newer;
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<IModrinthClient>(modrinth);
        });

        var instance = await CreateInstanceAsync(provider);
        var chosen = new ContentVersion(
            ContentSource.Modrinth,
            "demo",
            "v1",
            "1.0",
            ["1.20.1"],
            ["fabric"],
            "demo-1.0.jar",
            payload.Length,
            older.DatePublished);

        var result = await provider.GetRequiredService<IContentInstallService>()
            .InstallVersionAsync(instance.Id, Hit(ContentSource.Modrinth, "demo", ContentKind.Mod), chosen);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var file = Assert.Single(downloads.Requests);
        Assert.Equal("demo-1.0.jar", Path.GetFileName(file.DestinationPath));
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.GetInstanceGameDirectory(instance.Id),
            "mods",
            "demo-1.0.jar")));
    }

    [Fact]
    public async Task DownloadVersionRequiresAnExistingAbsoluteDirectory()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("demo");
        using var environment = new TestEnvironment();
        var downloads = new PayloadDownloadService(payload);
        var modrinth = new FakeModrinthClient();
        var version = Version("v1", "1.0", ["1.20.1"], ["fabric"], payload.Length, DateTimeOffset.UtcNow);
        modrinth.Versions["demo"] = [version];
        modrinth.VersionsById["v1"] = version;
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<IModrinthClient>(modrinth);
        });

        var service = provider.GetRequiredService<IContentInstallService>();
        var relative = await service.DownloadVersionAsync(
            Hit(ContentSource.Modrinth, "demo", ContentKind.Mod),
            ToVersion(version),
            "relative-folder");
        Assert.Equal("content.invalid_target_directory", relative.Error!.Code);

        var missing = await service.DownloadVersionAsync(
            Hit(ContentSource.Modrinth, "demo", ContentKind.Mod),
            ToVersion(version),
            Path.Combine(environment.Root, "missing"));
        Assert.Equal("content.target_directory_missing", missing.Error!.Code);

        var target = Directory.CreateDirectory(Path.Combine(environment.Root, "picked")).FullName;
        var result = await service.DownloadVersionAsync(
            Hit(ContentSource.Modrinth, "demo", ContentKind.Mod),
            ToVersion(version),
            target);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(File.Exists(Path.Combine(target, "demo-1.0.jar")));
    }

    [Fact]
    public async Task InstallVersionRejectsModpacksAndDatapacksWithSpecificReasons()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var service = provider.GetRequiredService<IContentInstallService>();
        var version = new ContentVersion(
            ContentSource.Modrinth,
            "demo",
            "v1",
            "1.0",
            ["1.20.1"],
            ["fabric"],
            "demo.jar",
            null,
            null);

        var modpack = await service.InstallVersionAsync(
            instance.Id,
            Hit(ContentSource.Modrinth, "demo", ContentKind.ModPack),
            version);
        Assert.Equal("content.modpack_requires_deployment", modpack.Error!.Code);

        var datapack = await service.InstallVersionAsync(
            instance.Id,
            Hit(ContentSource.Modrinth, "demo", ContentKind.DataPack),
            version);
        Assert.Equal("content.datapack_requires_world", datapack.Error!.Code);
    }

    [Fact]
    public async Task InstallVersionInstallsCurseForgeModThroughTheExistingInstaller()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("demo");
        using var environment = new TestEnvironment();
        var curseForge = new FakeCurseForgeClient();
        curseForge.Mods[1] = new CurseForgeMod { Id = 1, Name = "Demo", Slug = "demo" };
        curseForge.Files[42] = new CurseForgeFile
        {
            Id = 42,
            ModId = 1,
            FileName = "demo-42.jar",
            DownloadUrl = "https://edge.forgecdn.net/files/0/42/demo-42.jar",
            FileLength = payload.Length,
            GameVersions = ["1.20.1", "Fabric"],
        };
        curseForge.ListedFiles.Add(curseForge.Files[42]);
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(new PayloadDownloadService(payload));
            services.AddSingleton<ICurseForgeClient>(curseForge);
        });

        var instance = await CreateInstanceAsync(provider);
        var version = new ContentVersion(
            ContentSource.CurseForge,
            "1",
            "42",
            "Demo 42",
            ["1.20.1"],
            ["fabric"],
            "demo-42.jar",
            payload.Length,
            null);

        var result = await provider.GetRequiredService<IContentInstallService>()
            .InstallVersionAsync(instance.Id, Hit(ContentSource.CurseForge, "1", ContentKind.Mod), version);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.GetInstanceGameDirectory(instance.Id),
            "mods",
            "demo-42.jar")));
    }

    private static ContentHit Hit(ContentSource source, string id, ContentKind kind) => new(
        source,
        id,
        id,
        "Demo",
        "desc",
        "author",
        0,
        null,
        "https://example.invalid/demo",
        null,
        kind);

    private static ContentVersion ToVersion(ModrinthVersion version) => new(
        ContentSource.Modrinth,
        version.ProjectId,
        version.Id,
        version.VersionNumber,
        version.GameVersions,
        version.Loaders,
        version.PrimaryFile!.Filename,
        version.PrimaryFile.Size > 0 ? version.PrimaryFile.Size : null,
        version.DatePublished);

    private static ModrinthVersion Version(
        string id,
        string number,
        IReadOnlyList<string> gameVersions,
        IReadOnlyList<string> loaders,
        long size,
        DateTimeOffset? published) => new()
        {
            Id = id,
            ProjectId = "demo",
            VersionNumber = number,
            VersionType = "release",
            GameVersions = gameVersions,
            Loaders = loaders,
            DatePublished = published,
            Files =
            [
                new ModrinthFile
                {
                    Url = $"https://cdn.modrinth.com/data/demo/versions/{id}/demo-{number}.jar",
                    Filename = $"demo-{number}.jar",
                    Primary = true,
                    Size = size,
                },
            ],
        };

    private static async Task<GameInstance> CreateInstanceAsync(IServiceProvider provider)
    {
        var result = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Test", "1.20.1", "fabric", "0.16.0");
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!;
    }
}
