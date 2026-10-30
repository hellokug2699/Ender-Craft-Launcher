// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ecl.Download;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Modrinth;
using Ecl.Minecraft.Mods;
using Ecl.Tests.CurseForge;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Modrinth;

public sealed class ContentInstallTests
{
    [Fact]
    public void ModpackArchiveInspectorRejectsOversizedMetadata()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"ecl-oversized-{Guid.NewGuid():N}.mrpack");
        try
        {
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("modrinth.index.json");
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(new string(' ', 4 * 1024 * 1024 + 1));
            }

            var result = ModpackArchiveInspector.Read(archivePath);

            Assert.False(result.IsSuccess);
            Assert.Equal("modpack.invalid_archive", result.Error!.Code);
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    [Fact]
    public async Task DownloadsMrpackWithoutAnyInstance()
    {
        var payload = CreateMrpack();
        using var environment = new TestEnvironment();
        var downloads = new PayloadDownloadService(payload);
        var modrinth = CreateModrinthClient(payload.Length);
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<IModrinthClient>(modrinth);
        });

        var result = await provider.GetRequiredService<IContentInstallService>()
            .DownloadAsync(CreateModpackHit());

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.Equal("Test Pack", report.Name);
        Assert.True(File.Exists(report.Path));
        Assert.Equal(payload.Length, new FileInfo(report.Path).Length);
        Assert.Contains(Path.Combine("downloads", "modpacks"), report.Path);
        Assert.Single(downloads.Requests);
    }

    [Fact]
    public async Task DownloadsAndImportsMrpackIntoExistingInstance()
    {
        var payload = CreateMrpack();
        using var environment = new TestEnvironment();
        var modrinth = CreateModrinthClient(payload.Length);
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(new PayloadDownloadService(payload));
            services.AddSingleton<IModrinthClient>(modrinth);
        });

        var instance = await CreateInstanceAsync(provider, "1.21.1", "fabric", "0.16.0");
        var result = await provider.GetRequiredService<IContentInstallService>()
            .ImportModpackAsync(instance.Id, CreateModpackHit());

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(instance.Id, result.Value!.InstanceId);
        Assert.True(File.Exists(result.Value.ArchivePath));
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.GetInstanceGameDirectory(instance.Id),
            "config",
            "test.txt")));
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.GetInstanceGameDirectory(instance.Id),
            ".ecl-modpack.json")));
    }

    [Fact]
    public async Task RollsBackCreatedInstanceWhenModpackImportFails()
    {
        var payload = CreateMrpack();
        using var environment = new TestEnvironment();
        var modrinth = CreateModrinthClient(payload.Length);
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(new PayloadDownloadService(payload));
            services.AddSingleton<IModrinthClient>(modrinth);
            services.AddSingleton<IModpackService>(new FailingModpackService());
        });

        var result = await provider.GetRequiredService<IContentInstallService>()
            .CreateInstanceAndImportModpackAsync(CreateModpackHit());

        Assert.False(result.IsSuccess);
        Assert.Empty(await provider.GetRequiredService<IInstanceService>().GetInstancesAsync());
    }

    [Fact]
    public async Task CreatesInstanceFromMrpackMetadataAndImports()
    {
        var payload = CreateMrpack();
        using var environment = new TestEnvironment();
        var modrinth = CreateModrinthClient(payload.Length);
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(new PayloadDownloadService(payload));
            services.AddSingleton<IModrinthClient>(modrinth);
        });

        var result = await provider.GetRequiredService<IContentInstallService>()
            .CreateInstanceAndImportModpackAsync(CreateModpackHit());

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal("Test Pack", result.Value!.Name);
        Assert.NotNull(result.Value.InstanceId);

        var instances = await provider.GetRequiredService<IInstanceService>().GetInstancesAsync();
        var created = Assert.Single(instances);
        Assert.Equal(result.Value.InstanceId, created.Id);
        Assert.Equal("1.21.1", created.GameVersion);
        Assert.Equal("fabric", created.Loader);
        Assert.Equal("0.16.0", created.LoaderVersion);
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.GetInstanceGameDirectory(created.Id),
            "config",
            "test.txt")));
    }

    [Fact]
    public async Task InstallsModrinthModAndRequiredDependencyIntoModsFolder()
    {
        var payload = Encoding.UTF8.GetBytes("demo");
        using var environment = new TestEnvironment();
        var modrinth = new FakeModrinthClient();
        var version = CreateModVersion("ver1", "demo.jar");
        version = version with
        {
            Dependencies = [new ModrinthDependency { ProjectId = "fabric-api", DependencyType = "required" }],
        };
        modrinth.Versions["demo"] = [version];
        modrinth.VersionsById["ver1"] = version;
        modrinth.Versions["fabric-api"] = [CreateModVersion("ver2", "fabric-api.jar")];

        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(new PayloadDownloadService(payload));
            services.AddSingleton<IModrinthClient>(modrinth);
        });

        var instance = await CreateInstanceAsync(provider, "1.21.1", "fabric", "0.16.0");
        var result = await provider.GetRequiredService<IContentInstallService>()
            .InstallAsync(instance.Id, new ContentHit(
                ContentSource.Modrinth,
                "demo",
                "demo",
                "Demo Mod",
                "desc",
                "author",
                1,
                null,
                "https://modrinth.com/mod/demo",
                null,
                ContentKind.Mod));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Value!.Files.Count);
        var modsDirectory = Path.Combine(environment.Paths.GetInstanceGameDirectory(instance.Id), "mods");
        Assert.True(File.Exists(Path.Combine(modsDirectory, "demo.jar")));
        Assert.True(File.Exists(Path.Combine(modsDirectory, "fabric-api.jar")));
    }

    [Fact]
    public async Task DownloadsAndImportsCurseForgeModpackArchive()
    {
        var payload = CreateCurseForgePack();
        using var environment = new TestEnvironment();
        var curseForge = new FakeCurseForgeClient();
        curseForge.Mods[1] = new CurseForgeMod
        {
            Id = 1,
            Slug = "cf-pack",
            Name = "CF Pack",
            ClassId = CurseForgeClassIds.Modpack,
            LatestFilesIndexes =
            [
                new CurseForgeFileIndex
                {
                    GameVersion = "1.20.1",
                    FileId = 10,
                    ModLoader = (int)CurseForgeModLoader.Forge,
                    ReleaseType = 1,
                },
            ],
        };
        curseForge.Files[10] = new CurseForgeFile
        {
            Id = 10,
            ModId = 1,
            FileName = "cf-pack.zip",
            DownloadUrl = "https://edge.forgecdn.net/files/0/10/cf-pack.zip",
            FileLength = payload.Length,
            GameVersions = ["1.20.1", "Forge"],
        };

        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(new PayloadDownloadService(payload));
            services.AddSingleton<ICurseForgeClient>(curseForge);
        });

        var instance = await CreateInstanceAsync(provider, "1.20.1", "forge", "47.4.0");
        var result = await provider.GetRequiredService<IContentInstallService>()
            .ImportModpackAsync(instance.Id, new ContentHit(
                ContentSource.CurseForge,
                "1",
                "cf-pack",
                "CF Pack",
                "desc",
                null,
                1,
                null,
                "https://www.curseforge.com/minecraft/modpacks/cf-pack",
                null,
                ContentKind.ModPack));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(File.Exists(result.Value!.ArchivePath));
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.GetInstanceGameDirectory(instance.Id),
            "config",
            "cf.txt")));
    }

    private sealed class FailingModpackService : IModpackService
    {
        public Task<Result<ModpackImportReport>> ImportAsync(
            string instanceId,
            string archivePath,
            IProgress<ModpackProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ModpackImportReport>.Fail("test.import_failed", "模拟导入失败。"));

        public Task<Result<ModpackExportReport>> ExportAsync(
            string instanceId,
            string outputPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static FakeModrinthClient CreateModrinthClient(int payloadSize)
    {
        var client = new FakeModrinthClient();
        var version = new ModrinthVersion
        {
            Id = "ver1",
            ProjectId = "testpack",
            VersionNumber = "1.0.0",
            VersionType = "release",
            GameVersions = ["1.21.1"],
            Loaders = ["fabric"],
            DatePublished = DateTimeOffset.UtcNow,
            Files =
            [
                new ModrinthFile
                {
                    Url = "https://cdn.modrinth.com/data/testpack/versions/ver1/test.mrpack",
                    Filename = "test.mrpack",
                    Primary = true,
                    Size = payloadSize,
                },
            ],
        };
        client.Versions["testpack"] = [version];
        client.VersionsById["ver1"] = version;
        return client;
    }

    private static ModrinthVersion CreateModVersion(string id, string fileName) => new()
    {
        Id = id,
        ProjectId = "demo",
        VersionNumber = id,
        VersionType = "release",
        GameVersions = ["1.21.1"],
        Loaders = ["fabric"],
        Files =
        [
            new ModrinthFile
            {
                Url = $"https://cdn.modrinth.com/data/demo/versions/{id}/{fileName}",
                Filename = fileName,
                Primary = true,
                Size = 4,
            },
        ],
    };

    private static ContentHit CreateModpackHit() => new(
        ContentSource.Modrinth,
        "testpack",
        "testpack",
        "Test Pack",
        "A test modpack",
        "author",
        100,
        DateTimeOffset.UtcNow,
        "https://modrinth.com/modpack/testpack",
        null,
        ContentKind.ModPack);

    private static byte[] CreateMrpack()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var index = new
            {
                formatVersion = 1,
                game = "minecraft",
                versionId = "1.0.0",
                name = "Test Pack",
                summary = "Test",
                files = Array.Empty<object>(),
                dependencies = new Dictionary<string, string>
                {
                    ["minecraft"] = "1.21.1",
                    ["fabric-loader"] = "0.16.0",
                },
            };

            WriteEntry(archive, "modrinth.index.json", JsonSerializer.Serialize(index));
            WriteEntry(archive, "overrides/config/test.txt", "hello");
        }

        return stream.ToArray();
    }

    private static byte[] CreateCurseForgePack()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new
            {
                name = "CF Pack",
                version = "1.0",
                minecraft = new
                {
                    version = "1.20.1",
                    modLoaders = new[]
                    {
                        new { id = "forge-47.4.0", primary = true },
                    },
                },
                files = Array.Empty<object>(),
            };

            WriteEntry(archive, "manifest.json", JsonSerializer.Serialize(manifest));
            WriteEntry(archive, "overrides/config/cf.txt", "hello cf");
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static async Task<GameInstance> CreateInstanceAsync(
        IServiceProvider provider,
        string gameVersion,
        string loader,
        string loaderVersion)
    {
        var result = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Test", gameVersion, loader, loaderVersion);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!;
    }
}
