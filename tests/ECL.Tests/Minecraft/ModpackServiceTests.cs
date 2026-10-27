// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using Ecl.Download;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
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
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task ImportsCurseForgeOverridesWithWarning()
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
            Assert.Single(result.Value.Warnings);
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

    private static string CreateCurseForgePack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ecl-cf-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "manifest.json", """
            {
              "name": "CurseForge Pack",
              "version": "1.0",
              "minecraft": { "version": "1.20.1" },
              "files": []
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
