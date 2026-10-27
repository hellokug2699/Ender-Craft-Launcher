// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;
using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Install;

public sealed class InstallServiceTests
{
    [Fact]
    public async Task InstallsVersionLibrariesAssetsAndLogging()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));

        var instanceResult = await provider.GetRequiredService<IInstanceService>().CreateAsync("Test", "1.21.1");
        Assert.True(instanceResult.IsSuccess);
        var instance = instanceResult.Value!;

        var stages = new List<InstallStage>();
        var progress = new InlineProgress<InstallProgress>(update => stages.Add(update.Stage));

        var result = await provider.GetRequiredService<IVersionInstallService>()
            .InstallAsync(instance.Id, progress);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.Equal("1.21.1", report.VersionId);
        Assert.True(report.DownloadedFiles >= 4);

        Assert.True(File.Exists(VersionFilePlanner.GetVersionJsonPath(environment.Paths, "1.21.1")));
        Assert.True(File.Exists(VersionFilePlanner.GetClientJarPath(environment.Paths, "1.21.1")));
        Assert.True(File.Exists(VersionFilePlanner.GetAssetIndexPath(environment.Paths, "1.21")));
        Assert.True(File.Exists(VersionFilePlanner.GetLoggingFilePath(environment.Paths, "client-1.12.xml")));
        Assert.True(File.Exists(VersionFilePlanner.GetAssetObjectPath(
            environment.Paths,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.LibrariesDirectory,
            "com/example/demo/1.0/demo-1.0.jar".Replace('/', Path.DirectorySeparatorChar))));

        Assert.Contains(InstallStage.Completed, stages);
        Assert.Contains(fake.Requests, request => request.DisplayName == "1.21.1.jar");
        Assert.Contains(fake.Requests, request => request.DisplayName == "demo-1.0.jar");
    }

    [Fact]
    public async Task RejectsUnsupportedLoader()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));

        var instanceResult = await provider.GetRequiredService<IInstanceService>().CreateAsync("Test", "1.21.1");
        Assert.True(instanceResult.IsSuccess);
        var instance = instanceResult.Value! with { Loader = "quilt" };
        await provider.GetRequiredService<IInstanceStore>().SaveAsync(instance);

        var result = await provider.GetRequiredService<IVersionInstallService>()
            .InstallAsync(instance.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("install.unsupported_loader", result.Error?.Code);
    }

    [Fact]
    public async Task InstallsFabricLoaderAndFabricApi()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        var catalog = new FakeLoaderCatalog();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(fake);
            services.AddSingleton<ILoaderCatalog>(catalog);
        });

        var instanceResult = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Fabric", "1.21.1", "fabric", "0.16.0", "0.116.0+1.21.1");
        Assert.True(instanceResult.IsSuccess);

        var stages = new List<InstallStage>();
        var result = await provider.GetRequiredService<IVersionInstallService>()
            .InstallAsync(
                instanceResult.Value!.Id,
                new InlineProgress<InstallProgress>(update => stages.Add(update.Stage)));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Contains(InstallStage.Loader, stages);
        Assert.Contains(InstallStage.FabricApi, stages);

        var versionId = instanceResult.Value.VersionId;
        Assert.True(File.Exists(VersionFilePlanner.GetVersionJsonPath(environment.Paths, versionId)));
        Assert.True(File.Exists(VersionFilePlanner.GetClientJarPath(environment.Paths, versionId)));
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.GetInstanceGameDirectory(instanceResult.Value.Id),
            "mods",
            "fabric-api-0.116.0+1.21.1.jar")));
        Assert.Contains(result.Value!.Components!, component => component.StartsWith("fabric-api:"));
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class FakeLoaderCatalog : ILoaderCatalog
    {
        private const string FabricProfile = """
            {
              "id": "fabric-loader-0.16.0-1.21.1",
              "inheritsFrom": "1.21.1",
              "type": "release",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "arguments": {
                "game": [],
                "jvm": ["-DFabricMcEmu= net.minecraft.client.main.Main "]
              },
              "libraries": [
                {
                  "name": "net.fabricmc:fabric-loader:0.16.0",
                  "url": "https://maven.fabricmc.net/"
                }
              ]
            }
            """;

        public Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetFabricLoadersAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<LoaderVersionInfo>>.Ok(
            [
                new LoaderVersionInfo("0.16.0", true, DateTimeOffset.Parse("2024-06-01T00:00:00Z")),
            ]));

        public Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetCleanroomVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<LoaderVersionInfo>>.Ok([]));

        public Task<Result<IReadOnlyList<FabricApiVersionInfo>>> GetFabricApiVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<FabricApiVersionInfo>>.Ok(
            [
                new FabricApiVersionInfo(
                    "0.116.0+1.21.1",
                    "https://cdn.modrinth.com/data/P7dR8mSH/versions/test/fabric-api.jar",
                    "fabric-api-0.116.0+1.21.1.jar",
                    null,
                    1,
                    DateTimeOffset.Parse("2024-06-01T00:00:00Z")),
            ]));

        public Task<Result<IReadOnlyList<OptiFineVersionInfo>>> GetOptiFineVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<OptiFineVersionInfo>>.Ok([]));

        public Task<Result<string>> GetFabricProfileAsync(
            string gameVersion,
            string loaderVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Ok(FabricProfile));
    }

    private sealed class FakeDownloadService : IDownloadService
    {
        public ConcurrentBag<DownloadRequest> Requests { get; } = [];

        public Task<DownloadBatchResult> DownloadAsync(
            IReadOnlyList<DownloadRequest> requests,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var request in requests)
            {
                Requests.Add(request);
                var directory = Path.GetDirectoryName(request.DestinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(request.DestinationPath, Encoding.UTF8.GetBytes("x"));
            }

            return Task.FromResult(new DownloadBatchResult(requests.Count, 0, requests.Count));
        }

        public Task<string> DownloadTextAsync(
            IReadOnlyList<Uri> urls,
            string? expectedHash = null,
            long? expectedSize = null,
            HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
            CancellationToken cancellationToken = default)
        {
            var url = urls.FirstOrDefault()?.ToString() ?? string.Empty;

            if (url.Contains("version_manifest", StringComparison.Ordinal))
            {
                return Task.FromResult(ManifestJson);
            }

            if (url.EndsWith("1.21.1.json", StringComparison.Ordinal))
            {
                return Task.FromResult(VersionJson);
            }

            if (url.EndsWith("1.21.json", StringComparison.Ordinal))
            {
                return Task.FromResult(AssetIndexJson);
            }

            return Task.FromResult("{}");
        }

        private const string ManifestJson = """
            {
              "latest": { "release": "1.21.1", "snapshot": "24w14a" },
              "versions": [
                {
                  "id": "1.21.1",
                  "type": "release",
                  "url": "https://piston-meta.mojang.com/v1/packages/abc/1.21.1.json",
                  "sha1": "0000000000000000000000000000000000000000",
                  "size": 1
                }
              ]
            }
            """;

        private const string VersionJson = """
            {
              "id": "1.21.1",
              "type": "release",
              "mainClass": "net.minecraft.client.main.Main",
              "assetIndex": {
                "id": "1.21",
                "url": "https://piston-meta.mojang.com/v1/packages/def/1.21.json",
                "sha1": "0000000000000000000000000000000000000000",
                "size": 1
              },
              "downloads": {
                "client": {
                  "url": "https://piston-data.mojang.com/v1/objects/abc/client.jar",
                  "sha1": "0000000000000000000000000000000000000000",
                  "size": 1
                }
              },
              "logging": {
                "client": {
                  "argument": "-Dlog4j.configurationFile=${path}",
                  "file": {
                    "id": "client-1.12.xml",
                    "url": "https://piston-data.mojang.com/v1/objects/def/client-1.12.xml",
                    "sha1": "0000000000000000000000000000000000000000",
                    "size": 1
                  }
                }
              },
              "libraries": [
                {
                  "name": "com.example:demo:1.0",
                  "downloads": {
                    "artifact": {
                      "path": "com/example/demo/1.0/demo-1.0.jar",
                      "url": "https://libraries.minecraft.net/com/example/demo/1.0/demo-1.0.jar",
                      "sha1": "0000000000000000000000000000000000000000",
                      "size": 1
                    }
                  }
                }
              ]
            }
            """;

        private const string AssetIndexJson = """
            {
              "objects": {
                "minecraft/test.txt": {
                  "hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size": 1
                }
              }
            }
            """;
    }
}
