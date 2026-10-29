// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Download;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.CurseForge;

public sealed class CurseForgeServiceTests
{
    [Fact]
    public async Task InstallsSelectedModAndWritesInstallState()
    {
        using var environment = new TestEnvironment();
        var downloads = new RecordingDownloadService();
        var curseForge = CreateClient();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<ICurseForgeClient>(curseForge);
        });
        var instance = await CreateInstanceAsync(provider, "1.20.1", "forge", "47.4.0");
        curseForge.Mods[1] = CreateMod(1, "demo", "Demo Mod");
        curseForge.Files[10] = CreateFile(10, 1, "demo.jar", "https://edge.forgecdn.net/files/0/10/demo.jar");

        var result = await provider.GetRequiredService<ICurseForgeService>()
            .InstallAsync(instance.Id, "1");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.Equal("Demo Mod", report.ModName);
        Assert.Equal(10, report.FileId);
        Assert.Equal("demo.jar", Assert.Single(report.InstalledFiles));

        var request = Assert.Single(downloads.Requests);
        Assert.EndsWith(Path.Combine("mods", "demo.jar"), request.DestinationPath);
        Assert.Equal("https://edge.forgecdn.net/files/0/10/demo.jar", request.Urls[0].ToString());
        Assert.Equal("0000000000000000000000000000000000000000", request.Hash);
        Assert.Equal(4L, request.Size);

        var gameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Assert.True(File.Exists(Path.Combine(gameDirectory, "mods", "demo.jar")));
        using var marker = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(gameDirectory, ".ecl-curseforge.json")));
        Assert.Equal(1, marker.RootElement.GetProperty("mods")[0].GetProperty("projectId").GetInt32());
    }

    [Fact]
    public async Task InstallsRequiredDependenciesRecursively()
    {
        using var environment = new TestEnvironment();
        var downloads = new RecordingDownloadService();
        var curseForge = CreateClient();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<ICurseForgeClient>(curseForge);
        });
        var instance = await CreateInstanceAsync(provider, "1.20.1", "forge", "47.4.0");

        curseForge.Mods[1] = CreateMod(1, "demo", "Demo Mod");
        curseForge.Mods[2] = CreateMod(2, "library", "Demo Library");
        var primary = CreateFile(10, 1, "demo.jar", "https://edge.forgecdn.net/files/0/10/demo.jar");
        primary = primary with { Dependencies = [new CurseForgeFileDependency { ModId = 2, RelationType = 3 }] };
        curseForge.Files[10] = primary;
        curseForge.Files[20] = CreateFile(20, 2, "library.jar", "https://edge.forgecdn.net/files/0/20/library.jar");

        var result = await provider.GetRequiredService<ICurseForgeService>()
            .InstallAsync(instance.Id, "demo");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Value!.InstalledFiles.Count);
        Assert.Equal(2, downloads.Requests.Count);
        Assert.Contains(downloads.Requests, request => request.DestinationPath.EndsWith("library.jar"));
    }

    [Fact]
    public async Task RejectsVanillaInstance()
    {
        using var environment = new TestEnvironment();
        var downloads = new RecordingDownloadService();
        var curseForge = CreateClient();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<ICurseForgeClient>(curseForge);
        });
        var instance = await CreateInstanceAsync(provider, "1.20.1", "vanilla", null);

        var result = await provider.GetRequiredService<ICurseForgeService>()
            .InstallAsync(instance.Id, "1");

        Assert.False(result.IsSuccess);
        Assert.Equal("curseforge.loader_unsupported", result.Error?.Code);
        Assert.Empty(downloads.Requests);
    }

    [Fact]
    public async Task ReportsDistributionRestrictedFile()
    {
        using var environment = new TestEnvironment();
        var downloads = new RecordingDownloadService();
        var curseForge = CreateClient();
        curseForge.ReturnNullDownloadUrl = true;
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<ICurseForgeClient>(curseForge);
        });
        var instance = await CreateInstanceAsync(provider, "1.20.1", "forge", "47.4.0");
        curseForge.Mods[1] = CreateMod(1, "demo", "Demo Mod");
        curseForge.Files[10] = CreateFile(10, 1, "demo.jar", downloadUrl: null);

        var result = await provider.GetRequiredService<ICurseForgeService>()
            .InstallAsync(instance.Id, "1");

        Assert.False(result.IsSuccess);
        Assert.Equal("curseforge.distribution_restricted", result.Error?.Code);
        Assert.Empty(downloads.Requests);
    }

    [Fact]
    public async Task ReportsDownloadFailure()
    {
        using var environment = new TestEnvironment();
        var downloads = new RecordingDownloadService { Fail = true };
        var curseForge = CreateClient();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<ICurseForgeClient>(curseForge);
        });
        var instance = await CreateInstanceAsync(provider, "1.20.1", "forge", "47.4.0");
        curseForge.Mods[1] = CreateMod(1, "demo", "Demo Mod");
        curseForge.Files[10] = CreateFile(10, 1, "demo.jar", "https://edge.forgecdn.net/files/0/10/demo.jar");

        var result = await provider.GetRequiredService<ICurseForgeService>()
            .InstallAsync(instance.Id, "1");

        Assert.False(result.IsSuccess);
        Assert.Equal("curseforge.download_failed", result.Error?.Code);
    }

    private static FakeCurseForgeClient CreateClient() =>
        new() { Status = new CurseForgeStatus(CurseForgeKeySource.Embedded) };

    private static CurseForgeMod CreateMod(int id, string slug, string name) => new()
    {
        Id = id,
        Slug = slug,
        Name = name,
        LatestFilesIndexes =
        [
            new CurseForgeFileIndex
            {
                GameVersion = "1.20.1",
                FileId = id * 10,
                ModLoader = (int)CurseForgeModLoader.Forge,
                ReleaseType = 1,
            },
        ],
    };

    private static CurseForgeFile CreateFile(
        int id,
        int modId,
        string fileName,
        string? downloadUrl = "https://edge.forgecdn.net/files/0/10/demo.jar") => new()
    {
        Id = id,
        ModId = modId,
        FileName = fileName,
        DownloadUrl = downloadUrl,
        FileLength = 4,
        GameVersions = ["1.20.1", "Forge"],
        Hashes = [new CurseForgeFileHash { Algo = 1, Value = "0000000000000000000000000000000000000000" }],
    };

    private static async Task<GameInstance> CreateInstanceAsync(
        IServiceProvider provider,
        string gameVersion,
        string loader,
        string? loaderVersion)
    {
        var result = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("CurseForge Test", gameVersion, loader, loaderVersion);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!;
    }
}
