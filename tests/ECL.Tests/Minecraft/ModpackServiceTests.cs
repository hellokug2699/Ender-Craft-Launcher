// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using Ecl.Download;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
using Ecl.Operations;
using Ecl.Tests.CurseForge;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class ModpackServiceTests
{
    [Fact]
    public async Task ImportsModrinthPackAndOverrides()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));
        var instance = await CreateInstanceAsync(provider, "1.21.1", "fabric", "0.16.0");
        var archive = CreateModrinthPack();

        try
        {
            var result = await provider.GetRequiredService<IModpackService>()
                .ImportAsync(instance.Id, archive);

            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Equal(1, result.Value!.DownloadedFiles);
            Assert.Equal(1, result.Value.OverrideFiles);

            var gameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id);
            Assert.True(File.Exists(Path.Combine(gameDirectory, "mods", "test.jar")));
            Assert.True(File.Exists(Path.Combine(gameDirectory, "config", "test.txt")));
            Assert.True(File.Exists(Path.Combine(gameDirectory, ".ecl-modpack.json")));

            var snapshot = Assert.Single(provider.GetRequiredService<IOperationStore>().Snapshots);
            Assert.Equal(OperationKind.Import, snapshot.Kind);
            Assert.Equal(OperationState.Completed, snapshot.State);
            Assert.Equal(Path.GetFileName(archive), snapshot.Title);
            Assert.Equal("Modrinth", snapshot.Source);
            Assert.Equal("modpack.overrides", snapshot.StageId);
            Assert.False(snapshot.CanCancel);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task RejectsPackForDifferentGameVersion()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));
        var instance = await CreateInstanceAsync(provider, "1.20.1", "fabric", "0.16.0");
        var archive = CreateModrinthPack("1.21.1");

        try
        {
            var result = await provider.GetRequiredService<IModpackService>()
                .ImportAsync(instance.Id, archive);

            Assert.False(result.IsSuccess);
            Assert.Equal("modpack.game_version_mismatch", result.Error?.Code);

            var snapshot = Assert.Single(provider.GetRequiredService<IOperationStore>().Snapshots);
            Assert.Equal(OperationKind.Import, snapshot.Kind);
            Assert.Equal(OperationState.Failed, snapshot.State);
            Assert.Equal("modpack.game_version_mismatch", snapshot.Error?.Code);
            Assert.Equal(OperationErrorKind.Configuration, snapshot.Error?.Kind);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task ImportsCurseForgeOverridesWithoutApiKey()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));
        var instance = await CreateInstanceAsync(provider, "1.20.1", "forge", "47.4.0");
        var archive = CreateCurseForgePack();

        try
        {
            var result = await provider.GetRequiredService<IModpackService>()
                .ImportAsync(instance.Id, archive);

            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Equal(0, result.Value!.DownloadedFiles);
            Assert.Empty(result.Value.Warnings);
            Assert.True(File.Exists(Path.Combine(
                environment.Paths.GetInstanceGameDirectory(instance.Id),
                "config",
                "test.txt")));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task ImportsCurseForgeRemoteFilesAndOverrides()
    {
        using var environment = new TestEnvironment();
        var downloads = new RecordingDownloadService();
        var curseForge = CreateConfiguredClient();
        curseForge.Files[2] = new CurseForgeFile
        {
            Id = 2,
            ModId = 1,
            FileName = "demo.jar",
            DownloadUrl = "https://edge.forgecdn.net/files/0/2/demo.jar",
            FileLength = 4,
            GameVersions = ["1.20.1", "Forge"],
            Hashes = [new CurseForgeFileHash { Algo = 1, Value = "0000000000000000000000000000000000000000" }],
        };

        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<ICurseForgeClient>(curseForge);
        });
        var instance = await CreateInstanceAsync(provider, "1.20.1", "forge", "47.4.0");
        var archive = CreateCurseForgePack(
            """[{"projectID":1,"fileID":2,"required":true}]""");

        try
        {
            var result = await provider.GetRequiredService<IModpackService>()
                .ImportAsync(instance.Id, archive);

            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Equal(1, result.Value!.DownloadedFiles);
            Assert.Equal(1, result.Value.OverrideFiles);
            var request = Assert.Single(downloads.Requests);
            Assert.EndsWith(Path.Combine("mods", "demo.jar"), request.DestinationPath);
            Assert.Equal("https://edge.forgecdn.net/files/0/2/demo.jar", request.Urls[0].ToString());
            Assert.Equal(HashingAlgorithm.Sha1, request.Algorithm);
            Assert.True(File.Exists(Path.Combine(
                environment.Paths.GetInstanceGameDirectory(instance.Id),
                "mods",
                "demo.jar")));
            Assert.Contains("curseforge", await File.ReadAllTextAsync(Path.Combine(
                environment.Paths.GetInstanceGameDirectory(instance.Id),
                ".ecl-modpack.json")));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task ImportsCurseForgeRemoteFilesWithoutClientSideKeyGate()
    {
        using var environment = new TestEnvironment();
        var downloads = new RecordingDownloadService();
        var curseForge = new FakeCurseForgeClient
        {
            // API key 由代理持有；即使状态对象报告未配置，导入也不应在客户端被拦截。
            Status = new CurseForgeStatus(CurseForgeKeySource.None),
        };
        curseForge.Files[2] = new CurseForgeFile
        {
            Id = 2,
            ModId = 1,
            FileName = "demo.jar",
            DownloadUrl = "https://edge.forgecdn.net/files/0/2/demo.jar",
            FileLength = 4,
            GameVersions = ["1.20.1", "Forge"],
            Hashes = [new CurseForgeFileHash { Algo = 1, Value = "0000000000000000000000000000000000000000" }],
        };
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<ICurseForgeClient>(curseForge);
        });
        var instance = await CreateInstanceAsync(provider, "1.20.1", "forge", "47.4.0");
        var archive = CreateCurseForgePack(
            """[{"projectID":1,"fileID":2,"required":true}]""");

        try
        {
            var result = await provider.GetRequiredService<IModpackService>()
                .ImportAsync(instance.Id, archive);

            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Single(downloads.Requests);
            Assert.Equal(1, curseForge.GetFilesCalls);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task RejectsCurseForgePackWithDifferentLoader()
    {
        using var environment = new TestEnvironment();
        var downloads = new RecordingDownloadService();
        var curseForge = CreateConfiguredClient();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<ICurseForgeClient>(curseForge);
        });
        var instance = await CreateInstanceAsync(provider, "1.20.1", "fabric", "0.16.0");
        var archive = CreateCurseForgePack(
            """[{"projectID":1,"fileID":2,"required":true}]""");

        try
        {
            var result = await provider.GetRequiredService<IModpackService>()
                .ImportAsync(instance.Id, archive);

            Assert.False(result.IsSuccess);
            Assert.Equal("modpack.loader_mismatch", result.Error?.Code);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task SkipsOptionalCurseForgeFilesWithWarning()
    {
        using var environment = new TestEnvironment();
        var downloads = new RecordingDownloadService();
        var curseForge = CreateConfiguredClient();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<ICurseForgeClient>(curseForge);
        });
        var instance = await CreateInstanceAsync(provider, "1.20.1", "forge", "47.4.0");
        var archive = CreateCurseForgePack(
            """[{"projectID":1,"fileID":2,"required":false}]""");

        try
        {
            var result = await provider.GetRequiredService<IModpackService>()
                .ImportAsync(instance.Id, archive);

            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Equal(0, result.Value!.DownloadedFiles);
            Assert.Equal(1, result.Value.SkippedFiles);
            Assert.Contains(result.Value.Warnings, warning => warning.Contains("可选"));
            Assert.Empty(downloads.Requests);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task ExportsInstanceAsModrinthPack()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));
        var instance = await CreateInstanceAsync(provider, "1.21.1", "fabric", "0.16.0");

        var gameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Directory.CreateDirectory(Path.Combine(gameDirectory, "mods"));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "mods", "demo.jar"), "demo");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "config"));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "config", "demo.txt"), "config");

        var output = Path.Combine(Path.GetTempPath(), $"ecl-export-{Guid.NewGuid():N}.mrpack");
        try
        {
            var result = await provider.GetRequiredService<IModpackService>()
                .ExportAsync(instance.Id, output);

            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.True(File.Exists(output));

            using var archive = ZipFile.OpenRead(output);
            Assert.NotNull(archive.GetEntry("modrinth.index.json"));
            Assert.NotNull(archive.GetEntry("overrides/mods/demo.jar"));
            Assert.NotNull(archive.GetEntry("overrides/config/demo.txt"));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task ExportFailureIsReturnedAsResult()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));
        var instance = await CreateInstanceAsync(provider, "1.21.1", "fabric", "0.16.0");

        var blocker = Path.Combine(Path.GetTempPath(), $"ecl-export-blocker-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blocker, "not a directory");
        try
        {
            var result = await provider.GetRequiredService<IModpackService>()
                .ExportAsync(instance.Id, Path.Combine(blocker, "out.mrpack"));

            Assert.False(result.IsSuccess);
            Assert.Equal("modpack.export_failed", result.Error?.Code);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    private static async Task<GameInstance> CreateInstanceAsync(
        IServiceProvider provider,
        string gameVersion,
        string loader,
        string loaderVersion)
    {
        var result = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Pack Test", gameVersion, loader, loaderVersion);
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static string CreateModrinthPack(string gameVersion = "1.21.1")
    {
        var path = Path.Combine(Path.GetTempPath(), $"ecl-pack-{Guid.NewGuid():N}.mrpack");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "modrinth.index.json", $$"""
            {
              "formatVersion": 1,
              "game": "minecraft",
              "versionId": "1.0.0",
              "name": "Test Pack",
              "summary": "Test",
              "dependencies": {
                "minecraft": "{{gameVersion}}",
                "fabric-loader": "0.16.0"
              },
              "files": [
                {
                  "path": "mods/test.jar",
                  "hashes": { "sha1": "0000000000000000000000000000000000000000" },
                  "downloads": ["https://example.test/test.jar"],
                  "fileSize": 4
                }
              ]
            }
            """);
        WriteEntry(archive, "overrides/config/test.txt", "config");
        return path;
    }

    private static FakeCurseForgeClient CreateConfiguredClient() =>
        new() { Status = new CurseForgeStatus(CurseForgeKeySource.Proxy) };

    private static string CreateCurseForgePack(string filesJson = "[]")
    {
        var path = Path.Combine(Path.GetTempPath(), $"ecl-cf-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "manifest.json", $$"""
            {
              "name": "CurseForge Pack",
              "version": "1.0",
              "minecraft": {
                "version": "1.20.1",
                "modLoaders": [
                  { "id": "forge-47.4.0", "primary": true }
                ]
              },
              "files": {{filesJson}}
            }
            """);
        WriteEntry(archive, "overrides/config/test.txt", "config");
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class FakeDownloadService : IDownloadService
    {
        public Task<DownloadBatchResult> DownloadAsync(
            IReadOnlyList<DownloadRequest> requests,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var request in requests)
            {
                var directory = Path.GetDirectoryName(request.DestinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(request.DestinationPath, "demo");
            }

            return Task.FromResult(new DownloadBatchResult(requests.Count, 0, requests.Count * 4));
        }

        public Task<string> DownloadTextAsync(
            IReadOnlyList<Uri> urls,
            string? expectedHash = null,
            long? expectedSize = null,
            HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
