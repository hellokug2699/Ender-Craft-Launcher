// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Download;
using Ecl.Minecraft.Versions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class VersionCatalogTests
{
    private const string Manifest = """
        {
          "latest": { "release": "1.21.1", "snapshot": "25w14a" },
          "versions": [
            {
              "id": "1.21.1",
              "type": "release",
              "url": "https://piston-meta.mojang.com/v1/packages/abc/1.21.1.json",
              "releaseTime": "2024-08-08T12:00:00+00:00"
            },
            {
              "id": "25w14a",
              "type": "snapshot",
              "url": "https://piston-meta.mojang.com/v1/packages/def/25w14a.json",
              "releaseTime": "2025-04-02T12:00:00+00:00"
            },
            {
              "id": "25w14craftmine",
              "type": "snapshot",
              "url": "https://piston-meta.mojang.com/v1/packages/ghi/craftmine.json",
              "releaseTime": "2025-04-01T12:00:00+00:00"
            },
            {
              "id": "b1.7.3",
              "type": "old_beta",
              "url": "https://piston-meta.mojang.com/v1/packages/jkl/b1.7.3.json",
              "releaseTime": "2011-07-08T12:00:00+00:00"
            }
          ]
        }
        """;

    [Theory]
    [InlineData("1.21.1", "release", MinecraftVersionKind.Release)]
    [InlineData("24w14a", "snapshot", MinecraftVersionKind.Snapshot)]
    [InlineData("21w13a", "snapshot", MinecraftVersionKind.Snapshot)]
    [InlineData("15w14a", "snapshot", MinecraftVersionKind.AprilFools)]
    [InlineData("1.RV-Pre1", "snapshot", MinecraftVersionKind.AprilFools)]
    [InlineData("22w13oneblockatatime", "snapshot", MinecraftVersionKind.AprilFools)]
    [InlineData("25w14craftmine", "snapshot", MinecraftVersionKind.AprilFools)]
    public void ClassifiesManifestTypes(string id, string type, MinecraftVersionKind expected)
    {
        Assert.Equal(expected, MojangVersionCatalog.Classify(id, type));
    }

    [Fact]
    public void SkipsLegacyVersionsThatCannotCreateInstances()
    {
        Assert.Null(MojangVersionCatalog.Classify("b1.7.3", "old_beta"));
        Assert.Null(MojangVersionCatalog.Classify("a1.2.6", "old_alpha"));
        Assert.Null(MojangVersionCatalog.Classify("rd-132211", "old_alpha"));
    }

    [Fact]
    public async Task LoadsManifestOnceAndKeepsOnlySupportedKinds()
    {
        using var environment = new TestEnvironment();
        var downloads = new ManifestDownloadService(Manifest);
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IDownloadService>(downloads));
        var catalog = provider.GetRequiredService<IVersionCatalog>();

        var first = await catalog.GetVersionsAsync();
        var second = await catalog.GetVersionsAsync();

        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.Equal(3, first.Value!.Count);
        Assert.Equal(1, downloads.Calls);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(
            new[] { "1.21.1", "25w14a", "25w14craftmine" },
            first.Value.Select(version => version.Id).ToArray());
        Assert.Equal(
            MinecraftVersionKind.AprilFools,
            first.Value.Single(version => version.Id == "25w14craftmine").Kind);
    }

    [Fact]
    public async Task RefreshBypassesTheCache()
    {
        using var environment = new TestEnvironment();
        var downloads = new ManifestDownloadService(Manifest);
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IDownloadService>(downloads));
        var catalog = provider.GetRequiredService<IVersionCatalog>();

        await catalog.GetVersionsAsync();
        var refreshed = await catalog.GetVersionsAsync(refresh: true);

        Assert.True(refreshed.IsSuccess);
        Assert.Equal(2, downloads.Calls);
    }

    [Fact]
    public async Task ReportsUnavailableManifest()
    {
        using var environment = new TestEnvironment();
        var downloads = new ManifestDownloadService(Manifest) { Fail = true };
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IDownloadService>(downloads));
        var catalog = provider.GetRequiredService<IVersionCatalog>();

        var result = await catalog.GetVersionsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("versions.unavailable", result.Error?.Code);
    }

    private sealed class ManifestDownloadService(string manifest) : IDownloadService
    {
        public int Calls { get; private set; }

        public bool Fail { get; init; }

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
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Fail
                ? Task.FromException<string>(new HttpRequestException("offline"))
                : Task.FromResult(manifest);
        }
    }
}
