// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Download;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Modrinth;

public interface IModrinthClient
{
    Task<Result<ModrinthSearchResult>> SearchAsync(
        ModrinthSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<Result<ModrinthProject>> GetProjectAsync(
        string projectIdOrSlug,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<ModrinthVersion>>> GetVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken cancellationToken = default);

    Task<Result<ModrinthVersion>> GetVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Modrinth v2 REST 客户端。只负责 HTTP、查询参数、错误映射与有限重试；
/// 安装位置、整合包导入等业务判断在 <see cref="ModrinthService"/> 中完成。
/// </summary>
public sealed class ModrinthClient : IModrinthClient, IDisposable
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ILogger<ModrinthClient> _logger;
    private readonly SemaphoreSlim _requestGate = new(4, 4);

    public ModrinthClient(IHttpClientProvider httpClientProvider, ILogger<ModrinthClient> logger)
    {
        _http = httpClientProvider.CreateClient();
        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(ModrinthApi.BaseAddress + "/");
        }

        _http.Timeout = Timeout.InfiniteTimeSpan;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "EnderCraftLauncher/0.8 (+https://github.com/hellokug2699/Ender-Craft-Launcher)");
        }

        _logger = logger;
    }

    public async Task<Result<ModrinthSearchResult>> SearchAsync(
        ModrinthSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var facets = BuildFacets(query);
        var path = "/search" + BuildQuery(
            ("query", query.Query),
            ("facets", facets),
            ("index", query.Sort),
            ("offset", Math.Max(0, query.Offset).ToString()),
            ("limit", pageSize.ToString()));

        var raw = await SendAsync(path, cancellationToken).ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<ModrinthSearchResult>.Fail(raw.Error!);
        }

        try
        {
            using var document = JsonDocument.Parse(raw.Value!);
            var root = document.RootElement;
            var hits = root.TryGetProperty("hits", out var hitArray) && hitArray.ValueKind == JsonValueKind.Array
                ? hitArray.Deserialize<List<ModrinthSearchHit>>(EclJson.Default) ?? []
                : [];

            return Result<ModrinthSearchResult>.Ok(new ModrinthSearchResult(
                hits,
                root.TryGetProperty("offset", out var offset) && offset.TryGetInt32(out var offsetValue) ? offsetValue : query.Offset,
                root.TryGetProperty("limit", out var limit) && limit.TryGetInt32(out var limitValue) ? limitValue : pageSize,
                root.TryGetProperty("total_hits", out var total) && total.TryGetInt32(out var totalValue) ? totalValue : hits.Count));
        }
        catch (JsonException exception)
        {
            return InvalidResponse<ModrinthSearchResult>(exception);
        }
    }

    public async Task<Result<ModrinthProject>> GetProjectAsync(
        string projectIdOrSlug,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectIdOrSlug))
        {
            return Result<ModrinthProject>.Fail("modrinth.invalid_project", "请提供 Modrinth 项目 ID 或 slug。");
        }

        var raw = await SendAsync(
                $"/project/{Uri.EscapeDataString(projectIdOrSlug.Trim())}",
                cancellationToken)
            .ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<ModrinthProject>.Fail(raw.Error!);
        }

        return Parse<ModrinthProject>(raw.Value!);
    }

    public async Task<Result<IReadOnlyList<ModrinthVersion>>> GetVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectIdOrSlug))
        {
            return Result<IReadOnlyList<ModrinthVersion>>.Fail(
                "modrinth.invalid_project",
                "请提供 Modrinth 项目 ID 或 slug。");
        }

        var gameVersions = string.IsNullOrWhiteSpace(gameVersion) ? null : SerializeArray([gameVersion.Trim()]);
        var loaders = string.IsNullOrWhiteSpace(loader) ? null : SerializeArray([loader.Trim()]);
        var path = $"/project/{Uri.EscapeDataString(projectIdOrSlug.Trim())}/version" +
                   BuildQuery(("game_versions", gameVersions), ("loaders", loaders));

        var raw = await SendAsync(path, cancellationToken).ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<IReadOnlyList<ModrinthVersion>>.Fail(raw.Error!);
        }

        return Parse<List<ModrinthVersion>>(raw.Value!) is { IsSuccess: true } parsed
            ? Result<IReadOnlyList<ModrinthVersion>>.Ok(parsed.Value!)
            : Result<IReadOnlyList<ModrinthVersion>>.Fail(
                "modrinth.invalid_response",
                "Modrinth 返回了无法解析的版本列表。");
    }

    public async Task<Result<ModrinthVersion>> GetVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return Result<ModrinthVersion>.Fail("modrinth.invalid_version", "请提供 Modrinth 版本 ID。");
        }

        var raw = await SendAsync(
                $"/version/{Uri.EscapeDataString(versionId.Trim())}",
                cancellationToken)
            .ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<ModrinthVersion>.Fail(raw.Error!);
        }

        return Parse<ModrinthVersion>(raw.Value!);
    }

    public void Dispose()
    {
        _http.Dispose();
        _requestGate.Dispose();
    }

    private static string BuildFacets(ModrinthSearchQuery query)
    {
        var groups = new List<string[]>();
        if (query.ProjectType is { } type && type != ModrinthProjectType.Unknown)
        {
            groups.Add([$"project_type:{ModrinthApi.ToApiValue(type)}"]);
        }

        if (!string.IsNullOrWhiteSpace(query.GameVersion))
        {
            groups.Add([$"versions:{query.GameVersion.Trim()}"]);
        }

        var loader = ModrinthApi.LoaderForModrinth(query.Loader);
        if (loader is not null)
        {
            groups.Add([$"categories:{loader}"]);
        }

        return groups.Count == 0 ? string.Empty : JsonSerializer.Serialize(groups, EclJson.Default);
    }

    private async Task<Result<byte[]>> SendAsync(string path, CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(RequestTimeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path.TrimStart('/'));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                HttpResponseMessage response;
                await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    response = await _http
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _requestGate.Release();
                }

                using (response)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(attemptCts.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return Result<byte[]>.Ok(bytes);
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return Result<byte[]>.Fail(
                            "modrinth.not_found",
                            "Modrinth 上找不到请求的项目或版本。",
                            $"HTTP 404 {path}");
                    }

                    if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxAttempts)
                    {
                        await Task.Delay(GetRetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                    {
                        await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogWarning(
                        "Modrinth 请求失败：GET {Path} -> {Status}",
                        path,
                        (int)response.StatusCode);
                    return Result<byte[]>.Fail(
                        response.StatusCode == HttpStatusCode.TooManyRequests
                            ? "modrinth.rate_limited"
                            : "modrinth.http_error",
                        response.StatusCode == HttpStatusCode.TooManyRequests
                            ? "Modrinth 请求过于频繁，请稍后再试。"
                            : "Modrinth 请求失败。",
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                lastException = exception;
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (IOException exception)
            {
                lastException = exception;
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        _logger.LogWarning(lastException, "Modrinth 请求重试耗尽：GET {Path}", path);
        return Result<byte[]>.Fail(
            "modrinth.network_error",
            "无法连接 Modrinth。",
            lastException?.Message);
    }

    private static Result<T> Parse<T>(byte[] bytes)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(bytes, EclJson.Default);
            return value is null
                ? Result<T>.Fail("modrinth.invalid_response", "Modrinth 返回了空数据。")
                : Result<T>.Ok(value);
        }
        catch (JsonException exception)
        {
            return InvalidResponse<T>(exception);
        }
    }

    private static Result<T> InvalidResponse<T>(JsonException exception) =>
        Result<T>.Fail("modrinth.invalid_response", "Modrinth 返回了无法解析的数据。", exception.Message);

    private static string SerializeArray(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values, EclJson.Default);

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1));

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ??
                    (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : Backoff(attempt));
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
    }

    private static string BuildQuery(params (string Key, string? Value)[] parameters)
    {
        var parts = parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .Select(parameter => $"{parameter.Key}={Uri.EscapeDataString(parameter.Value!)}")
            .ToArray();
        return parts.Length == 0 ? string.Empty : "?" + string.Join("&", parts);
    }
}
