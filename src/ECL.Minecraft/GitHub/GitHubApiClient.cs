// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.GitHub;

/// <summary>
/// 用户授权令牌的本地存储边界。实现必须使用平台安全存储；令牌不得进入日志、普通配置或诊断附件。
/// 本版本只定义接口，未绑定具体安全存储实现，因此写入功能默认不可用。
/// </summary>
public interface IGitHubTokenStore
{
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);

    Task SetTokenAsync(string token, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class NullGitHubTokenStore : IGitHubTokenStore
{
    public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task SetTokenAsync(string token, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("尚未实现平台安全存储。"));

    public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed record GitHubTransportResponse(
    int StatusCode,
    string Content,
    string? ETag,
    DateTimeOffset? LastModified,
    IReadOnlyDictionary<string, string> Headers);

public interface IGitHubApiTransport
{
    Task<GitHubTransportResponse> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 只允许固定 GitHub API 主机与目标仓库 endpoint 的传输层。禁止自动重定向。
/// </summary>
public sealed class HttpGitHubApiTransport : IGitHubApiTransport, IDisposable
{
    private readonly HttpClient _http;

    public HttpGitHubApiTransport()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{GitHubRepositoryPolicy.ApiHost}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ECL-GitHub/1.0");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<GitHubTransportResponse> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("请求缺少 URI。");
        if (!GitHubRepositoryPolicy.IsAllowedApiUri(uri))
        {
            throw new InvalidOperationException("GitHub API 请求越出固定仓库边界。");
        }

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .ToDictionary(
                header => header.Key,
                header => string.Join(", ", header.Value),
                StringComparer.OrdinalIgnoreCase);
        return new GitHubTransportResponse(
            (int)response.StatusCode,
            content,
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified ?? response.Headers.Date,
            headers);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// 受控 GitHub API 客户端（ADR 0049 §3/§9）。所有 endpoint 由代码构造；
/// 读取公开数据可匿名，写入必须使用用户授权的 GitHub App token。
/// </summary>
public sealed class GitHubApiClient
{
    private const int MaxBodyLength = 65_536;
    private const int MaxTitleLength = 256;

    private static readonly string[] DiscussionQuery =
    [
        "query { repository(owner: \"hellokug2699\", name: \"Ender-Craft-Launcher\") {",
        "discussions(first: 20) { nodes { id number title body category { name } author { login } url } } } }",
    ];

    private readonly IGitHubApiTransport _transport;
    private readonly IGitHubTokenStore _tokens;
    private readonly IAppPaths _paths;
    private readonly ILogger<GitHubApiClient> _logger;

    public GitHubApiClient(
        IGitHubApiTransport transport,
        IGitHubTokenStore tokens,
        IAppPaths paths,
        ILogger<GitHubApiClient>? logger = null)
    {
        _transport = transport;
        _tokens = tokens;
        _paths = paths;
        _logger = logger ?? NullLogger<GitHubApiClient>.Instance;
    }

    public async Task<Result<GitHubApiResult<IReadOnlyList<GitHubRelease>>>> GetReleasesAsync(
        int perPage = 20,
        CancellationToken cancellationToken = default)
    {
        var uri = BuildUri($"repos/{GitHubRepositoryPolicy.Target.Slug}/releases?per_page={Math.Clamp(perPage, 1, 100)}");
        var result = await GetJsonAsync(
            uri,
            "releases",
            json =>
            {
                var releases = JsonSerializer.Deserialize<List<ReleaseDto>>(json, EclJson.Default) ?? [];
                return (IReadOnlyList<GitHubRelease>)releases.Select(release => release.ToModel()).ToArray();
            },
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<Result<GitHubApiResult<IReadOnlyList<GitHubIssue>>>> GetIssuesAsync(
        string state = "open",
        int perPage = 30,
        CancellationToken cancellationToken = default)
    {
        var normalizedState = state is "open" or "closed" or "all" ? state : "open";
        var uri = BuildUri(
            $"repos/{GitHubRepositoryPolicy.Target.Slug}/issues?state={normalizedState}&per_page={Math.Clamp(perPage, 1, 100)}");
        return await GetJsonAsync(
            uri,
            "issues",
            json =>
            {
                var issues = JsonSerializer.Deserialize<List<IssueDto>>(json, EclJson.Default) ?? [];
                return (IReadOnlyList<GitHubIssue>)issues.Select(issue => issue.ToModel()).ToArray();
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<GitHubApiResult<GitHubIssue>>> GetIssueAsync(
        int number,
        CancellationToken cancellationToken = default)
    {
        if (number <= 0)
        {
            return Result<GitHubApiResult<GitHubIssue>>.Fail("github.invalid_issue", "Issue 编号不合法。");
        }

        var uri = BuildUri($"repos/{GitHubRepositoryPolicy.Target.Slug}/issues/{number}");
        return await GetJsonAsync(
            uri,
            $"issue-{number}",
            json => JsonSerializer.Deserialize<IssueDto>(json, EclJson.Default)?.ToModel()
                    ?? throw new JsonException("Issue 响应为空。"),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<GitHubApiResult<IReadOnlyList<GitHubComment>>>> GetIssueCommentsAsync(
        int number,
        int perPage = 50,
        CancellationToken cancellationToken = default)
    {
        if (number <= 0)
        {
            return Result<GitHubApiResult<IReadOnlyList<GitHubComment>>>.Fail("github.invalid_issue", "Issue 编号不合法。");
        }

        var uri = BuildUri(
            $"repos/{GitHubRepositoryPolicy.Target.Slug}/issues/{number}/comments?per_page={Math.Clamp(perPage, 1, 100)}");
        return await GetJsonAsync(
            uri,
            $"issue-{number}-comments",
            json =>
            {
                var comments = JsonSerializer.Deserialize<List<CommentDto>>(json, EclJson.Default) ?? [];
                return (IReadOnlyList<GitHubComment>)comments.Select(comment => comment.ToModel()).ToArray();
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<GitHubApiResult<IReadOnlyList<GitHubDiscussion>>>> GetDiscussionsAsync(
        CancellationToken cancellationToken = default)
    {
        var token = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Result<GitHubApiResult<IReadOnlyList<GitHubDiscussion>>>.Fail(
                "github.auth_required",
                "读取 Discussions 需要用户授权；未连接 GitHub 时可浏览 Release 与 Issue。");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("graphql"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query = string.Join(' ', DiscussionQuery) }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // 令牌只存在于请求头；日志与错误消息不包含它。
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return Result<GitHubApiResult<IReadOnlyList<GitHubDiscussion>>>.Fail(response.Error!);
        }

        try
        {
            using var document = JsonDocument.Parse(response.Value!.Content);
            var nodes = document.RootElement
                .GetProperty("data")
                .GetProperty("repository")
                .GetProperty("discussions")
                .GetProperty("nodes");
            var discussions = new List<GitHubDiscussion>();
            foreach (var node in nodes.EnumerateArray())
            {
                discussions.Add(new GitHubDiscussion(
                    node.GetProperty("id").GetString() ?? string.Empty,
                    node.GetProperty("number").GetInt32(),
                    node.GetProperty("title").GetString() ?? string.Empty,
                    node.TryGetProperty("body", out var body) ? body.GetString() : null,
                    node.GetProperty("category").GetProperty("name").GetString() ?? string.Empty,
                    node.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
                        ? author.GetProperty("login").GetString() ?? string.Empty
                        : string.Empty,
                    node.GetProperty("url").GetString() ?? string.Empty));
            }

            return Result<GitHubApiResult<IReadOnlyList<GitHubDiscussion>>>.Ok(
                new GitHubApiResult<IReadOnlyList<GitHubDiscussion>>(discussions, false, null, null));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            return Result<GitHubApiResult<IReadOnlyList<GitHubDiscussion>>>.Fail(
                "github.invalid_response",
                "GitHub Discussions 响应无法解析。",
                exception.Message);
        }
    }

    public async Task<Result<GitHubIssue>> CreateIssueAsync(
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateSubmission(title, body, out var error))
        {
            return Result<GitHubIssue>.Fail("github.invalid_submission", error);
        }

        var token = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Result<GitHubIssue>.Fail("github.auth_required", "创建 Issue 需要先连接 GitHub。");
        }

        var request = BuildRequest(
            HttpMethod.Post,
            $"repos/{GitHubRepositoryPolicy.Target.Slug}/issues",
            new { title, body },
            token);
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return Result<GitHubIssue>.Fail(response.Error!);
        }

        try
        {
            var issue = JsonSerializer.Deserialize<IssueDto>(response.Value!.Content, EclJson.Default);
            return issue is null
                ? Result<GitHubIssue>.Fail("github.invalid_response", "Issue 响应为空。")
                : Result<GitHubIssue>.Ok(issue.ToModel());
        }
        catch (JsonException exception)
        {
            return Result<GitHubIssue>.Fail("github.invalid_response", "Issue 响应无法解析。", exception.Message);
        }
    }

    public async Task<Result<GitHubComment>> CreateIssueCommentAsync(
        int issueNumber,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (issueNumber <= 0)
        {
            return Result<GitHubComment>.Fail("github.invalid_issue", "Issue 编号不合法。");
        }

        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxBodyLength)
        {
            return Result<GitHubComment>.Fail("github.invalid_submission", "评论内容为空或超过长度限制。");
        }

        var token = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Result<GitHubComment>.Fail("github.auth_required", "发表评论需要先连接 GitHub。");
        }

        var request = BuildRequest(
            HttpMethod.Post,
            $"repos/{GitHubRepositoryPolicy.Target.Slug}/issues/{issueNumber}/comments",
            new { body },
            token);
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return Result<GitHubComment>.Fail(response.Error!);
        }

        try
        {
            var comment = JsonSerializer.Deserialize<CommentDto>(response.Value!.Content, EclJson.Default);
            return comment is null
                ? Result<GitHubComment>.Fail("github.invalid_response", "评论响应为空。")
                : Result<GitHubComment>.Ok(comment.ToModel());
        }
        catch (JsonException exception)
        {
            return Result<GitHubComment>.Fail("github.invalid_response", "评论响应无法解析。", exception.Message);
        }
    }

    private async Task<Result<GitHubApiResult<T>>> GetJsonAsync<T>(
        Uri uri,
        string cacheKey,
        Func<string, T> parse,
        CancellationToken cancellationToken)
    {
        var token = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var authenticated = !string.IsNullOrWhiteSpace(token);
        var cache = await ReadCacheAsync($"{cacheKey}:{(authenticated ? "auth" : "anon")}", cancellationToken)
            .ConfigureAwait(false);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (cache is not null && !string.IsNullOrWhiteSpace(cache.ETag))
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cache.ETag));
        }

        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        Result<GitHubTransportResponse> response;
        try
        {
            response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // 断网时公开数据可以展示最后缓存，但必须标记来源与时间。
            if (cache is not null)
            {
                return Result<GitHubApiResult<T>>.Ok(new GitHubApiResult<T>(
                    parse(cache.Content),
                    true,
                    cache.LastModified,
                    cache.ETag));
            }

            return Result<GitHubApiResult<T>>.Fail(
                "github.unavailable",
                "GitHub 当前不可用；不影响 ECL 启动与实例管理。",
                exception.Message);
        }

        if (!response.IsSuccess)
        {
            return Result<GitHubApiResult<T>>.Fail(response.Error!);
        }

        var value = response.Value!;
        if (value.StatusCode == 304 && cache is not null)
        {
            return Result<GitHubApiResult<T>>.Ok(new GitHubApiResult<T>(
                parse(cache.Content),
                true,
                cache.LastModified,
                cache.ETag));
        }

        try
        {
            var parsed = parse(value.Content);
            await WriteCacheAsync(
                $"{cacheKey}:{(authenticated ? "auth" : "anon")}",
                new GitHubCacheEntry(value.Content, value.ETag, value.LastModified),
                cancellationToken).ConfigureAwait(false);
            return Result<GitHubApiResult<T>>.Ok(new GitHubApiResult<T>(
                parsed,
                false,
                value.LastModified,
                value.ETag));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Result<GitHubApiResult<T>>.Fail(
                "github.invalid_response",
                "GitHub 响应无法解析。",
                exception.Message);
        }
    }

    private async Task<Result<GitHubTransportResponse>> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is 401)
            {
                return Result<GitHubTransportResponse>.Fail(
                    "github.unauthorized",
                    "GitHub 授权已失效；公开读取仍可用，写入需要重新授权。");
            }

            if (response.StatusCode is 403 or 429)
            {
                var remaining = response.Headers.TryGetValue("X-RateLimit-Remaining", out var value)
                    ? value
                    : null;
                var retryAfter = response.Headers.TryGetValue("Retry-After", out var retry)
                    ? retry
                    : null;
                if (remaining == "0" || retryAfter is not null || response.StatusCode == 429)
                {
                    return Result<GitHubTransportResponse>.Fail(
                        "github.rate_limited",
                        "GitHub API 速率限制已触发；请稍后重试。",
                        retryAfter);
                }
            }

            if ((response.StatusCode is < 200 or >= 300) && response.StatusCode != 304)
            {
                return Result<GitHubTransportResponse>.Fail(
                    "github.request_failed",
                    $"GitHub 请求失败（HTTP {response.StatusCode}）。");
            }

            return Result<GitHubTransportResponse>.Ok(response);
        }
        catch (HttpRequestException exception)
        {
            return Result<GitHubTransportResponse>.Fail("github.unavailable", "无法连接 GitHub。", exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<GitHubTransportResponse>.Fail("github.timeout", "GitHub 请求超时。");
        }
    }

    private static bool TryValidateSubmission(string title, string body, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength)
        {
            error = $"标题需为 1-{MaxTitleLength} 个字符。";
            return false;
        }

        if (body.Length > MaxBodyLength)
        {
            error = $"正文超过 {MaxBodyLength} 个字符。";
            return false;
        }

        return true;
    }

    private static Uri BuildUri(string relative) => new(
        new Uri($"https://{GitHubRepositoryPolicy.ApiHost}/"),
        relative);

    private static HttpRequestMessage BuildRequest(
        HttpMethod method,
        string relative,
        object payload,
        string token)
    {
        var request = new HttpRequestMessage(method, BuildUri(relative))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private string CachePath(string key)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_paths.ConfigDirectory, "github", "cache", $"{hash}.json");
    }

    private async Task<GitHubCacheEntry?> ReadCacheAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var path = CachePath(key);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<GitHubCacheEntry>(json, EclJson.Default);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(
        string key,
        GitHubCacheEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = CachePath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(entry, EclJson.Default),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "写入 GitHub 缓存失败。");
        }
    }

    private sealed record GitHubCacheEntry(string Content, string? ETag, DateTimeOffset? LastModified);

    private sealed record ReleaseDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("html_url")] string? HtmlUrl)
    {
        public GitHubRelease ToModel() => new(
            Id,
            TagName ?? string.Empty,
            Name ?? TagName ?? string.Empty,
            Body,
            Draft,
            Prerelease,
            PublishedAt,
            HtmlUrl ?? string.Empty);
    }

    private sealed record IssueUserDto([property: JsonPropertyName("login")] string? Login);

    private sealed record IssueDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("user")] IssueUserDto? User,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("pull_request")] JsonElement? PullRequest)
    {
        public GitHubIssue ToModel() => new(
            Id,
            Number,
            Title ?? string.Empty,
            Body,
            State ?? "unknown",
            User?.Login ?? string.Empty,
            CreatedAt,
            UpdatedAt,
            HtmlUrl ?? string.Empty,
            PullRequest is { ValueKind: not JsonValueKind.Null });
    }

    private sealed record CommentDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("user")] IssueUserDto? User,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("html_url")] string? HtmlUrl)
    {
        public GitHubComment ToModel() => new(
            Id,
            Body ?? string.Empty,
            User?.Login ?? string.Empty,
            CreatedAt,
            HtmlUrl ?? string.Empty);
    }
}
