// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Download;
using Ecl.Minecraft.Versions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Versions;

/// <summary>
/// 安装前产物大小（`download-instance-versions.md` §9）：只汇总官方版本 JSON 与 Fabric profile
/// 声明的真实文件大小；缺失时整体未知，不用 0 或经验值替代。
/// </summary>
public sealed class VersionDownloadSizeEstimatorTests
{
    private const string VersionJson = """
        {
          "id": "test",
          "type": "release",
          "downloads": { "client": { "size": 40000000, "url": "https://piston-meta.mojang.com/client.jar" } },
          "libraries": [
            {
              "name": "example:alpha:1.0",
              "downloads": {
                "artifact": { "path": "example/alpha/1.0/alpha-1.0.jar", "size": 1000000 }
              }
            },
            {
              "name": "example:beta:1.0",
              "downloads": {
                "artifact": { "path": "example/beta/1.0/beta-1.0.jar", "size": 2000000 }
              }
            }
          ],
          "assetIndex": { "id": "test", "size": 1000, "totalSize": 5000000 },
          "logging": { "client": { "file": { "id": "client-1.0.xml", "size": 2000 } } }
        }
        """;

    [Fact]
    public async Task SumsClientLibrariesAssetsAndLoggingFromTheVersionJson()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IDownloadService>(new JsonDownloadService(_ => VersionJson)));

        var result = await provider.GetRequiredService<IVersionDownloadSizeEstimator>()
            .EstimateGameFilesAsync("test", "https://piston-meta.mojang.com/version.json");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var sizes = result.Value!;
        Assert.Equal(40000000, sizes.ClientBytes);
        Assert.Equal(3000000, sizes.LibraryBytes);
        Assert.Equal(5000000, sizes.AssetBytes);
        Assert.Equal(2000, sizes.LoggingBytes);
        Assert.Equal(48002000, sizes.TotalBytes);
    }

    [Fact]
    public async Task AssetIndexWithoutTotalSizeKeepsTheTotalUnknown()
    {
        const string json = """
            {
              "id": "old",
              "type": "release",
              "downloads": { "client": { "size": 40000000 } },
              "libraries": [],
              "assetIndex": { "id": "old", "size": 1000 }
            }
            """;
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IDownloadService>(new JsonDownloadService(_ => json)));

        var result = await provider.GetRequiredService<IVersionDownloadSizeEstimator>()
            .EstimateGameFilesAsync("old", "https://piston-meta.mojang.com/old.json");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Null(result.Value!.TotalBytes);
        Assert.Equal(40000000, result.Value.ClientBytes);
    }

    [Fact]
    public async Task DeclaredLoggingWithoutSizeIsUnknown()
    {
        const string json = """
            {
              "id": "nosize",
              "type": "release",
              "downloads": { "client": { "size": 40000000 } },
              "libraries": [],
              "logging": { "client": { "file": { "id": "client.xml" } } }
            }
            """;
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IDownloadService>(new JsonDownloadService(_ => json)));

        var result = await provider.GetRequiredService<IVersionDownloadSizeEstimator>()
            .EstimateGameFilesAsync("nosize", "https://piston-meta.mojang.com/nosize.json");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Null(result.Value!.LoggingBytes);
        Assert.Null(result.Value.TotalBytes);
    }

    [Fact]
    public async Task FabricProfileLibrarySizesAreSummed()
    {
        const string profile = """
            {
              "id": "fabric-loader-0.16.0-1.21.1",
              "type": "release",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [
                { "name": "org.ow2.asm:asm:9.10.1", "size": 100000 },
                { "name": "org.ow2.asm:asm-tree:9.10.1", "size": 200000 }
              ]
            }
            """;
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IDownloadService>(new JsonDownloadService(_ => profile)));

        var size = await provider.GetRequiredService<IVersionDownloadSizeEstimator>()
            .EstimateFabricLoaderAsync("1.21.1", "0.16.0");

        Assert.Equal(300000, size);
    }

    [Fact]
    public async Task FabricProfileWithoutLibrarySizesStaysUnknown()
    {
        const string profile = """
            {
              "id": "fabric-loader-0.16.0-1.21.1",
              "type": "release",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [
                { "name": "org.ow2.asm:asm:9.10.1" }
              ]
            }
            """;
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IDownloadService>(new JsonDownloadService(_ => profile)));

        var size = await provider.GetRequiredService<IVersionDownloadSizeEstimator>()
            .EstimateFabricLoaderAsync("1.21.1", "0.16.0");

        Assert.Null(size);
    }

    private sealed class JsonDownloadService(Func<IReadOnlyList<Uri>, string> payload) : IDownloadService
    {
        public Task<DownloadBatchResult> DownloadAsync(
            IReadOnlyList<DownloadRequest> requests,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> DownloadTextAsync(
            IReadOnlyList<Uri> urls,
            string? expectedHash = null,
            long? expectedSize = null,
            HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(payload(urls));
    }
}
