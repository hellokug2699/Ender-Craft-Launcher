// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Download;
using Ecl.Java;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class JavaRuntimeProviderTests
{
    [Fact]
    public async Task AdoptiumResolvesLatestPackage()
    {
        var provider = new AdoptiumJavaRuntimeProvider(new JsonDownloadService("""
            [
              {
                "binary": {
                  "package": {
                    "checksum": "f9d6e191ab098c0d416e7d588a24420a8621cd2f4720dab2459b8b7b2d2d8b4e",
                    "link": "https://example.test/OpenJDK21U-jdk_x64_windows_hotspot_21.0.12.1_1.zip",
                    "name": "OpenJDK21U-jdk_x64_windows_hotspot_21.0.12.1_1.zip",
                    "size": 205073461
                  }
                },
                "version": { "semver": "21.0.12+101.0.LTS" }
              }
            ]
            """));

        var result = await provider.ResolveAsync(new JavaRequirement(21, "java-runtime-delta"));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var package = result.Value!;
        Assert.Equal("adoptium", package.Provider);
        Assert.Equal("java-runtime-delta", package.Component);
        Assert.EndsWith(".zip", package.FileName, StringComparison.Ordinal);
        Assert.Equal("f9d6e191ab098c0d416e7d588a24420a8621cd2f4720dab2459b8b7b2d2d8b4e", package.Sha256);
        Assert.Equal(205073461, package.Size);
        Assert.Equal(JavaArchiveKind.Zip, package.ArchiveKind);
        Assert.Equal(21, package.Version?.Major);
    }

    [Fact]
    public async Task ZuluResolvesPackageWithoutHash()
    {
        var provider = new ZuluJavaRuntimeProvider(new JsonDownloadService("""
            [
              {
                "download_url": "https://example.test/zulu21.54.107-ca-jdk21.0.13-win_x64.zip",
                "name": "zulu21.54.107-ca-jdk21.0.13-win_x64.zip",
                "java_version": [21, 0, 13],
                "latest": true
              }
            ]
            """));

        var result = await provider.ResolveAsync(new JavaRequirement(21));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var package = result.Value!;
        Assert.Equal("zulu", package.Provider);
        Assert.Null(package.Sha1);
        Assert.Null(package.Sha256);
        Assert.Equal(21, package.Version?.Major);
        Assert.Equal(13, package.Version?.Patch);
    }

    [Fact]
    public async Task LibericaResolvesPackageWithSha1()
    {
        var provider = new LibericaJavaRuntimeProvider(new JsonDownloadService("""
            [
              {
                "downloadUrl": "https://example.test/bellsoft-jdk21.0.13.0.3+5-windows-amd64.zip",
                "filename": "bellsoft-jdk21.0.13.0.3+5-windows-amd64.zip",
                "sha1": "cf1bf4593b445f2f8b08c932e08ce5b9dd4471be",
                "size": 218979630,
                "version": "21.0.13.0.3+5",
                "latestInFeatureVersion": true
              }
            ]
            """));

        var result = await provider.ResolveAsync(new JavaRequirement(21));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var package = result.Value!;
        Assert.Equal("liberica", package.Provider);
        Assert.Equal("cf1bf4593b445f2f8b08c932e08ce5b9dd4471be", package.Sha1);
        Assert.Equal(218979630, package.Size);
        Assert.Equal(21, package.Version?.Major);
    }

    private sealed class JsonDownloadService(string json) : IDownloadService
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
            Task.FromResult(json);
    }
}
