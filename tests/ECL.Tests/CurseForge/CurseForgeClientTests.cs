// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using Ecl.Download;
using Ecl.Minecraft.CurseForge;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.CurseForge;

public sealed class CurseForgeClientTests
{
    private const string SearchJson = """
        {
          "data": [
            {
              "id": 238222,
              "slug": "jei",
              "name": "Just Enough Items",
              "summary": "Recipe viewer",
              "downloadCount": 641575402,
              "dateModified": "2026-01-02T03:04:05Z",
              "links": { "websiteUrl": "https://www.curseforge.com/minecraft/mc-mods/jei" },
              "latestFilesIndexes": [
                { "gameVersion": "1.20.1", "fileId": 9352806, "releaseType": 1, "modLoader": 1 }
              ]
            }
          ],
          "pagination": { "index": 0, "pageSize": 20, "resultCount": 1, "totalCount": 1 }
        }
        """;

    [Fact]
    public async Task SearchSendsKeyAndParsesFilters()
    {
        using var environment = new TestEnvironment();
        var handler = new StubHandler { Response = JsonResponse(SearchJson) };
        using var client = CreateClient(environment, handler, "test-key");

        var result = await client.SearchModsAsync(new CurseForgeSearchQuery
        {
            SearchFilter = "jei",
            GameVersion = "1.20.1",
            Loader = CurseForgeModLoader.Forge,
            PageSize = 20,
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var search = result.Value!;
        Assert.Equal(1, search.TotalCount);
        var mod = Assert.Single(search.Mods);
        Assert.Equal(238222, mod.Id);
        Assert.Equal("jei", mod.Slug);
        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/jei", mod.WebsiteUrl);
        Assert.Equal(9352806, Assert.Single(mod.LatestFilesIndexes).FileId);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("test-key", handler.LastApiKey);
        Assert.Contains("gameId=432", handler.LastUri!.Query);
        Assert.Contains("classId=6", handler.LastUri.Query);
        Assert.Contains("searchFilter=jei", handler.LastUri.Query);
        Assert.Contains("gameVersion=1.20.1", handler.LastUri.Query);
        Assert.Contains("modLoaderType=1", handler.LastUri.Query);
    }

    [Fact]
    public async Task RepeatedSearchUsesDiskCache()
    {
        using var environment = new TestEnvironment();
        var handler = new StubHandler { Response = JsonResponse(SearchJson) };
        using var client = CreateClient(environment, handler, "test-key");
        var query = new CurseForgeSearchQuery { SearchFilter = "jei" };

        var first = await client.SearchModsAsync(query);
        var second = await client.SearchModsAsync(query);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MissingKeyFailsWithoutNetworkRequest()
    {
        using var environment = new TestEnvironment();
        var handler = new StubHandler { Response = JsonResponse(SearchJson) };
        using var client = CreateClient(environment, handler, null);

        var result = await client.SearchModsAsync(new CurseForgeSearchQuery());

        Assert.False(result.IsSuccess);
        Assert.Equal("curseforge.not_configured", result.Error?.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task UnauthorizedResponseIsReported()
    {
        using var environment = new TestEnvironment();
        var handler = new StubHandler
        {
            Response = JsonResponse("""{"error":"invalid key"}""", HttpStatusCode.Unauthorized),
        };
        using var client = CreateClient(environment, handler, "bad-key");

        var result = await client.SearchModsAsync(new CurseForgeSearchQuery());

        Assert.False(result.IsSuccess);
        Assert.Equal("curseforge.unauthorized", result.Error?.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BatchFilesUsesPostBodyAndParsesHashes()
    {
        using var environment = new TestEnvironment();
        var handler = new StubHandler
        {
            Response = JsonResponse("""
                {
                  "data": [
                    {
                      "id": 2,
                      "modId": 1,
                      "fileName": "demo.jar",
                      "fileLength": 4,
                      "downloadUrl": "https://edge.forgecdn.net/files/0/2/demo.jar",
                      "gameVersions": ["1.20.1", "Forge"],
                      "hashes": [{ "algo": 1, "value": "ABCDEF" }],
                      "dependencies": [{ "modId": 3, "relationType": 3 }]
                    }
                  ]
                }
                """),
        };
        using var client = CreateClient(environment, handler, "test-key");

        var result = await client.GetFilesAsync([2, 3, 2]);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var file = Assert.Single(result.Value!);
        Assert.Equal(2, file.Id);
        Assert.Equal("ABCDEF", file.Sha1);
        Assert.Equal(3, Assert.Single(file.Dependencies).ModId);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/v1/mods/files", handler.LastUri!.AbsolutePath);
        Assert.Contains("\"fileIds\":[2,3]", handler.LastBody);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DownloadUrlMayBeNull()
    {
        using var environment = new TestEnvironment();
        var handler = new StubHandler { Response = JsonResponse("""{"data":null}""") };
        using var client = CreateClient(environment, handler, "test-key");

        var result = await client.GetDownloadUrlAsync(1, 2);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    private static CurseForgeClient CreateClient(
        TestEnvironment environment,
        StubHandler handler,
        string? apiKey) =>
        new(
            new StubHttpClientProvider(handler),
            environment.Paths,
            new StaticKeyProvider(apiKey),
            NullLogger<CurseForgeClient>.Instance);

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StaticKeyProvider(string? apiKey) : ICurseForgeApiKeyProvider
    {
        public CurseForgeStatus Status => new(
            apiKey is null ? CurseForgeKeySource.None : CurseForgeKeySource.Environment);

        public string? GetApiKey() => apiKey;
    }

    private sealed class StubHttpClientProvider(HttpMessageHandler handler) : IHttpClientProvider
    {
        public HttpClient CreateClient() => new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        public Uri? LastUri { get; private set; }

        public string? LastApiKey { get; private set; }

        public string? LastBody { get; private set; }

        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            LastApiKey = request.Headers.TryGetValues("x-api-key", out var values)
                ? values.FirstOrDefault()
                : null;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return Response;
        }
    }
}
