// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http;
using System.Text;
using Ecl.Minecraft.GitHub;
using Xunit;

namespace Ecl.Tests.GitHub;

public sealed class GitHubRepositoryPolicyTests
{
    [Theory]
    [InlineData("https://github.com/hellokug2699/Ender-Craft-Launcher/issues/42", GitHubResourceKind.Issue, "42")]
    [InlineData("https://github.com/hellokug2699/Ender-Craft-Launcher/pull/7", GitHubResourceKind.PullRequest, "7")]
    [InlineData("https://github.com/hellokug2699/Ender-Craft-Launcher/commit/abcdef1", GitHubResourceKind.Commit, "abcdef1")]
    [InlineData("https://github.com/hellokug2699/Ender-Craft-Launcher/releases/tag/v1.2.3", GitHubResourceKind.Release, "v1.2.3")]
    [InlineData("https://github.com/hellokug2699/Ender-Craft-Launcher/discussions/3", GitHubResourceKind.Discussion, "3")]
    public void AcceptsTargetRepositoryResources(
        string reference,
        GitHubResourceKind kind,
        string identifier)
    {
        Assert.True(GitHubRepositoryPolicy.TryParse(reference, null, out var resource, out var error), error);
        Assert.Equal(kind, resource.Kind);
        Assert.Equal(identifier, resource.Identifier);
        Assert.Equal(GitHubRepositoryPolicy.Target.Slug, $"{resource.Owner}/{resource.Repository}");
    }

    [Fact]
    public void ResolvesFileAndDirectoryReferences()
    {
        Assert.True(GitHubRepositoryPolicy.TryParse(
            "https://github.com/hellokug2699/Ender-Craft-Launcher/blob/main/docs/readme.md",
            null,
            out var file,
            out var error), error);
        Assert.Equal(GitHubResourceKind.File, file.Kind);
        Assert.Equal("docs/readme.md", file.Path);

        Assert.True(GitHubRepositoryPolicy.TryParse(
            "https://github.com/hellokug2699/Ender-Craft-Launcher/tree/main/src",
            null,
            out var directory,
            out error), error);
        Assert.Equal(GitHubResourceKind.Directory, directory.Kind);
        Assert.Equal("src", directory.Path);
    }

    [Fact]
    public void ResolvesRelativeMarkdownLinksAgainstContext()
    {
        Assert.True(GitHubRepositoryPolicy.TryParse(
            "https://github.com/hellokug2699/Ender-Craft-Launcher/blob/main/docs/readme.md",
            null,
            out var context,
            out _));

        Assert.True(GitHubRepositoryPolicy.TryParse("../README.md", context, out var relative, out var error), error);
        Assert.Equal(GitHubResourceKind.File, relative.Kind);
        Assert.Equal("README.md", relative.Path);

        Assert.True(GitHubRepositoryPolicy.TryParse("images/logo.png", context, out var image, out error), error);
        Assert.Equal("docs/images/logo.png", image.Path);
    }

    [Theory]
    [InlineData("https://github.com/hellokug2699/Other/issues/1")]
    [InlineData("https://github.com/someone/else/issues/1")]
    [InlineData("https://github.com/hellokug2699/Ender-Craft-Launcher-Core/issues/1")]
    [InlineData("https://example.com/hellokug2699/Ender-Craft-Launcher/issues/1")]
    [InlineData("http://github.com/hellokug2699/Ender-Craft-Launcher/issues/1")]
    [InlineData("//evil.example/hellokug2699/Ender-Craft-Launcher/issues/1")]
    [InlineData("/hellokug2699/Ender-Craft-Launcher/settings")]
    [InlineData("/hellokug2699/Ender-Craft-Launcher/issues/abc")]
    [InlineData("/hellokug2699/Ender-Craft-Launcher/issues/1/%2e%2e/2")]
    [InlineData("/hellokug2699/Ender-Craft-Launcher//issues/1")]
    [InlineData("javascript:alert(1)")]
    public void RejectsOutsideOrAmbiguousReferences(string reference)
    {
        Assert.False(GitHubRepositoryPolicy.TryParse(reference, null, out _, out _));
    }

    [Fact]
    public void ResolvesMarkdownLinksWithoutTurningExternalLinksIntoNavigation()
    {
        var markdown = """
            See [issue](https://github.com/hellokug2699/Ender-Craft-Launcher/issues/12),
            [other repo](https://github.com/other/repo/issues/1),
            and ![image](https://example.com/image.png).
            """;
        var links = GitHubRepositoryPolicy.ResolveMarkdownLinks(markdown, null);
        Assert.Equal(3, links.Count);
        Assert.Contains(links, link => link.Resource?.Kind == GitHubResourceKind.Issue && link.Resource.Identifier == "12");
        Assert.Contains(links, link => link.Resource is null && link.Rejection is not null);
        Assert.Contains(links, link => link.IsImage && link.Resource is null);
    }

    [Fact]
    public void ApiResourceUrlMustStayOnFixedRepository()
    {
        Assert.True(GitHubRepositoryPolicy.TryParseApiResourceUrl(
            "https://api.github.com/repos/hellokug2699/Ender-Craft-Launcher/issues/5",
            out var resource,
            out var error), error);
        Assert.Equal(GitHubResourceKind.Issue, resource.Kind);

        Assert.False(GitHubRepositoryPolicy.TryParseApiResourceUrl(
            "https://api.github.com/repos/other/repo/issues/5",
            out _,
            out _));
        Assert.False(GitHubRepositoryPolicy.TryParseApiResourceUrl(
            "https://evil.example/repos/hellokug2699/Ender-Craft-Launcher/issues/5",
            out _,
            out _));
    }
}

public sealed class GitHubApiClientTests
{
    [Fact]
    public async Task ReadsReleasesFromFixedEndpoint()
    {
        using var environment = new TestEnvironment();
        var transport = new FakeTransport(_ => new GitHubTransportResponse(
            200,
            """
            [{ "id": 1, "tag_name": "v1.0.0", "name": "Release", "body": "notes", "draft": false,
               "prerelease": false, "published_at": "2026-11-03T00:00:00Z",
               "html_url": "https://github.com/hellokug2699/Ender-Craft-Launcher/releases/tag/v1.0.0" }]
            """,
            "\"etag-1\"",
            null,
            new Dictionary<string, string>()));
        var client = new GitHubApiClient(transport, new FakeTokenStore(null), environment.Paths);

        var releases = await client.GetReleasesAsync();
        Assert.True(releases.IsSuccess, releases.Error?.ToString());
        Assert.Single(releases.Value!.Value!);
        Assert.Contains("/repos/hellokug2699/Ender-Craft-Launcher/releases", transport.Requests[0].Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task WriteRequiresTokenAndNeverExposesItInErrors()
    {
        using var environment = new TestEnvironment();
        var transport = new FakeTransport(_ => new GitHubTransportResponse(
            200, "[]", null, null, new Dictionary<string, string>()));
        var client = new GitHubApiClient(transport, new FakeTokenStore(null), environment.Paths);

        var result = await client.CreateIssueAsync("Title", "Body");
        Assert.False(result.IsSuccess);
        Assert.Equal("github.auth_required", result.Error?.Code);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task CreateIssueUsesBearerTokenAndFixedRepository()
    {
        using var environment = new TestEnvironment();
        var transport = new FakeTransport(_ => new GitHubTransportResponse(
            201,
            """
            { "id": 9, "number": 12, "title": "Title", "body": "Body", "state": "open",
              "user": { "login": "someone" }, "created_at": "2026-11-03T00:00:00Z",
              "updated_at": "2026-11-03T00:00:00Z",
              "html_url": "https://github.com/hellokug2699/Ender-Craft-Launcher/issues/12" }
            """,
            null,
            null,
            new Dictionary<string, string>()));
        var client = new GitHubApiClient(transport, new FakeTokenStore("secret-token"), environment.Paths);

        var issue = await client.CreateIssueAsync("Title", "Body");
        Assert.True(issue.IsSuccess, issue.Error?.ToString());
        Assert.Equal(12, issue.Value!.Number);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal("secret-token", request.AuthorizationParameter);
        Assert.EndsWith("/repos/hellokug2699/Ender-Craft-Launcher/issues", request.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task RateLimitIsReportedSeparatelyFromNetworkFailure()
    {
        using var environment = new TestEnvironment();
        var transport = new FakeTransport(_ => new GitHubTransportResponse(
            403,
            "{}",
            null,
            null,
            new Dictionary<string, string> { ["X-RateLimit-Remaining"] = "0" }));
        var client = new GitHubApiClient(transport, new FakeTokenStore(null), environment.Paths);

        var result = await client.GetIssuesAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal("github.rate_limited", result.Error?.Code);
    }

    [Fact]
    public async Task ConditionalRequestUsesCachedContentOn304()
    {
        using var environment = new TestEnvironment();
        var call = 0;
        var transport = new FakeTransport(_ => ++call == 1
            ? new GitHubTransportResponse(
                200,
                """
                [{ "id": 1, "number": 1, "title": "T", "state": "open", "user": { "login": "u" },
                   "created_at": "2026-11-03T00:00:00Z", "updated_at": "2026-11-03T00:00:00Z",
                   "html_url": "https://github.com/hellokug2699/Ender-Craft-Launcher/issues/1" }]
                """,
                "\"etag-1\"",
                null,
                new Dictionary<string, string>())
            : new GitHubTransportResponse(304, string.Empty, null, null, new Dictionary<string, string>()));
        var client = new GitHubApiClient(transport, new FakeTokenStore(null), environment.Paths);

        var first = await client.GetIssuesAsync();
        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.False(first.Value!.FromCache);

        var second = await client.GetIssuesAsync();
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.True(second.Value!.FromCache);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Contains(transport.Requests[1].IfNoneMatch, tag => tag.Tag == "\"etag-1\"");
    }

    private sealed class FakeTokenStore(string? token) : IGitHubTokenStore
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(token);

        public Task SetTokenAsync(string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTransport(
        Func<HttpRequestMessage, GitHubTransportResponse> handler) : IGitHubApiTransport
    {
        public List<RecordedRequest> Requests { get; } = [];

        public Task<GitHubTransportResponse> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Headers.IfNoneMatch.ToArray()));
            return Task.FromResult(handler(request));
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        IReadOnlyList<System.Net.Http.Headers.EntityTagHeaderValue> IfNoneMatch);
}
