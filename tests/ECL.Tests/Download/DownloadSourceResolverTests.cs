// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;
using Ecl.Download;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Download;

public sealed class DownloadSourceResolverTests
{
    private static DownloadSourceResolver CreateResolver(ISettingsService settings) =>
        new(settings) { MirrorRoot = DownloadSourceResolver.DefaultMirrorRoot };

    private static void SetMode(ISettingsService settings, string mode)
    {
        Assert.True(settings.TrySetRaw("network.downloadSource", mode, null, out var error), error);
    }

    [Theory]
    [InlineData("official-only", 1, "piston-meta.mojang.com")]
    [InlineData("official-first", 2, "piston-meta.mojang.com")]
    [InlineData("mirror-first", 2, "bmclapi2.bangbang93.com")]
    [InlineData("mirror-only", 1, "bmclapi2.bangbang93.com")]
    public void OrdersCandidatesByMode(string mode, int expectedCount, string expectedFirstHost)
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        SetMode(settings, mode);

        var candidates = CreateResolver(settings).GetCandidates(new Uri(DownloadSourceResolver.OfficialManifestUrl));

        Assert.Equal(expectedCount, candidates.Count);
        Assert.Equal(expectedFirstHost, candidates[0].Host);
    }

    [Fact]
    public void MapsMojangHostsToBmclApi()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var resolver = CreateResolver(provider.GetRequiredService<ISettingsService>());

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
            resolver.MapToMirror(new Uri(DownloadSourceResolver.OfficialManifestUrl))?.ToString());

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/assets/ab/abcdef",
            resolver.MapToMirror(new Uri("https://resources.download.minecraft.net/ab/abcdef"))?.ToString());

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/libraries/com/example/demo/1.0/demo-1.0.jar",
            resolver.MapToMirror(new Uri("https://libraries.minecraft.net/com/example/demo/1.0/demo-1.0.jar"))?.ToString());

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/v1/objects/abc/client.jar",
            resolver.MapToMirror(new Uri("https://piston-data.mojang.com/v1/objects/abc/client.jar"))?.ToString());

        Assert.Equal(
            "https://maven.aliyun.com/repository/public/org/ow2/asm/asm/9.10.1/asm-9.10.1.jar",
            resolver.MapToMirror(new Uri(
                "https://repo.maven.apache.org/maven2/org/ow2/asm/asm/9.10.1/asm-9.10.1.jar"))?.ToString());

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/1.20.1-47.4.0/forge-1.20.1-47.4.0-installer.jar",
            resolver.MapToMirror(new Uri(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.0/forge-1.20.1-47.4.0-installer.jar"))?.ToString());

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/21.1.264/neoforge-21.1.264-installer.jar",
            resolver.MapToMirror(new Uri(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.264/neoforge-21.1.264-installer.jar"))?.ToString());
    }

    [Fact]
    public void MirrorOnlyRejectsUnmappedUrls()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        SetMode(settings, "mirror-only");

        var candidates = CreateResolver(settings).GetCandidates(new Uri("https://example.com/file"));

        Assert.Empty(candidates);
    }
}
