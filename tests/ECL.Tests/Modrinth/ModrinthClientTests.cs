// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Modrinth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Modrinth;

public sealed class ModrinthClientTests
{
    [Fact]
    public async Task ParsesSearchResponseAndSendsFacets()
    {
        var handler = new StubHttpMessageHandler(_ => """
            {
              "hits": [
                {
                  "project_id": "abc123",
                  "project_type": "mod",
                  "title": "Sodium",
                  "description": "Rendering engine",
                  "author": "jellysquid3",
                  "categories": ["fabric", "optimization"],
                  "versions": ["1.21.1"],
                  "downloads": 123456,
                  "follows": 42,
                  "icon_url": "https://cdn.modrinth.com/data/abc/icon.png",
                  "date_modified": "2024-06-01T12:00:00Z",
                  "slug": "sodium",
                  "latest_version": "ver1"
                }
              ],
              "offset": 0,
              "limit": 20,
              "total_hits": 1
            }
            """);
        using var client = new ModrinthClient(
            new StubHttpClientProvider(handler),
            NullLogger<ModrinthClient>.Instance);

        var result = await client.SearchAsync(new ModrinthSearchQuery
        {
            Query = "sodium",
            ProjectType = ModrinthProjectType.Mod,
            GameVersion = "1.21.1",
            Loader = "fabric",
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var hit = Assert.Single(result.Value!.Hits);
        Assert.Equal("abc123", hit.ProjectId);
        Assert.Equal("Sodium", hit.Title);
        Assert.Equal("jellysquid3", hit.Author);
        Assert.Equal(123456, hit.Downloads);
        Assert.Equal(ModrinthProjectType.Mod, hit.MappedProjectType);
        Assert.Equal("https://modrinth.com/mod/sodium", hit.WebsiteUrl);
        Assert.Equal(1, result.Value.TotalHits);

        var request = Assert.Single(handler.Requests).ToString();
        Assert.Contains("project_type%3Amod", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("versions%3A1.21.1", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("categories%3Afabric", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("index=relevance", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParsesVersionsWithFilesAndDependencies()
    {
        var handler = new StubHttpMessageHandler(_ => """
            [
              {
                "id": "ver1",
                "project_id": "abc123",
                "name": "Sodium 1.0",
                "version_number": "1.0.0",
                "version_type": "release",
                "game_versions": ["1.21.1"],
                "loaders": ["fabric"],
                "date_published": "2024-06-01T12:00:00Z",
                "downloads": 10,
                "dependencies": [
                  { "project_id": "fabric-api", "dependency_type": "required" }
                ],
                "files": [
                  {
                    "hashes": { "sha1": "abc", "sha512": "def" },
                    "url": "https://cdn.modrinth.com/data/abc/versions/ver1/sodium.jar",
                    "filename": "sodium.jar",
                    "primary": true,
                    "size": 1234
                  }
                ]
              }
            ]
            """);
        using var client = new ModrinthClient(
            new StubHttpClientProvider(handler),
            NullLogger<ModrinthClient>.Instance);

        var result = await client.GetVersionsAsync("sodium", "1.21.1", "fabric");

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var version = Assert.Single(result.Value!);
        Assert.Equal("ver1", version.Id);
        Assert.Equal("release", version.VersionType);
        var file = Assert.Single(version.Files);
        Assert.Equal("sodium.jar", file.Filename);
        Assert.Equal("abc", file.Hashes.Sha1);
        Assert.True(file.Primary);
        Assert.Equal("fabric-api", Assert.Single(version.Dependencies).ProjectId);
        Assert.Equal("required", version.Dependencies[0].DependencyType);

        var request = Assert.Single(handler.Requests).ToString();
        Assert.Contains("game_versions=", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("loaders=", request, StringComparison.OrdinalIgnoreCase);
    }

}
