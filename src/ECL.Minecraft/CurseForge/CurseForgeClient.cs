// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Download;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.CurseForge;

public interface ICurseForgeClient
{
    CurseForgeStatus Status { get; }

    Task<Result<CurseForgeSearchResult>> SearchModsAsync(
        CurseForgeSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<Result<CurseForgeMod>> GetModAsync(
        int modId,
        CancellationToken cancellationToken = default);

    Task<Result<CurseForgeMod?>> FindModBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<CurseForgeFile>>> GetFilesAsync(
        IReadOnlyList<int> fileIds,
        CancellationToken cancellationToken = default);

    Task<Result<CurseForgeFile>> GetFileAsync(
        int modId,
        int fileId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<CurseForgeFile>>> GetFilesForModAsync(
        int modId,
        string? gameVersion,
        CurseForgeModLoader? loader,
        int pageSize = 50,
        CancellationToken cancellationToken = default);

    Task<Result<string?>> GetDownloadUrlAsync(
        int modId,
        int fileId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// CurseForge for Minecraft REST 客户端。请求统一发往 ECL 自建的 CurseForge 代理，
/// API key 只存在于代理服务端，客户端既不读取也不发送 key。
/// 只负责 HTTP、错误映射与有限重试；业务兼容性判断在 <see cref="CurseForgeService"/> 与整合包导入中完成。
/// </summary>
public sealed class CurseForgeClient : ICurseForgeClient, IDisposable
{
    /// <summary>ECL 的 CurseForge 代理主机；路径与官方 API 完全一致，不做前缀改写。</summary>
    public const string BaseAddress = "https://api.wikifactory.top";

    /// <summary>CurseForge API 版本前缀，实际请求形如 <c>/v1/mods/search</c>。</summary>
    public const string ApiVersionPath = "/v1";

    private const int MaxAttempts = 3;
    private const int MaxBatchSize = 50;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ILogger<CurseForgeClient> _logger;
    private readonly SemaphoreSlim _requestGate = new(4, 4);

    public CurseForgeClient(
        IHttpClientProvider httpClientProvider,
        ILogger<CurseForgeClient> logger)
    {
        _http = httpClientProvider.CreateClient();
        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(BaseAddress);
        }

        _http.Timeout = Timeout.InfiniteTimeSpan;
        _logger = logger;
    }

    public CurseForgeStatus Status => new(CurseForgeKeySource.Proxy);

    public async Task<Result<CurseForgeSearchResult>> SearchModsAsync(
        CurseForgeSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 50);
        var path = "/v1/mods/search" + BuildQuery(
            ("gameId", CurseForgeGame.Minecraft.ToString()),
            ("classId", query.ClassId.ToString()),
            ("index", Math.Max(0, query.Index).ToString()),
            ("pageSize", pageSize.ToString()),
            ("sortField", ((int)query.SortField).ToString()),
            ("sortOrder", query.Descending ? "desc" : "asc"),
            ("searchFilter", query.SearchFilter),
            ("slug", query.Slug),
            ("gameVersion", query.GameVersion),
            ("modLoaderType", query.Loader is null ? null : ((int)query.Loader.Value).ToString()));

        var raw = await SendAsync(HttpMethod.Get, path, null, cancellationToken)
            .ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<CurseForgeSearchResult>.Fail(raw.Error!);
        }

        try
        {
            using var document = JsonDocument.Parse(raw.Value!);
            var root = document.RootElement;
            var mods = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.Deserialize<List<CurseForgeMod>>(EclJson.Default) ?? []
                : [];
            var pagination = root.TryGetProperty("pagination", out var page)
                ? page.Deserialize<CurseForgePagination>(EclJson.Default)
                : null;

            return Result<CurseForgeSearchResult>.Ok(new CurseForgeSearchResult(
                mods,
                pagination?.TotalCount ?? mods.Count,
                pagination?.Index ?? query.Index,
                pagination?.PageSize ?? pageSize));
        }
        catch (JsonException exception)
        {
            return InvalidResponse<CurseForgeSearchResult>(exception);
        }
    }

    public async Task<Result<CurseForgeMod>> GetModAsync(
        int modId,
        CancellationToken cancellationToken = default)
    {
        var raw = await SendAsync(
                HttpMethod.Get,
                $"/v1/mods/{modId}",
                null,
                cancellationToken)
            .ConfigureAwait(false);
        return raw.IsSuccess
            ? ParseData<CurseForgeMod>(raw.Value!)
            : Result<CurseForgeMod>.Fail(raw.Error!);
    }

    public async Task<Result<CurseForgeMod?>> FindModBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Result<CurseForgeMod?>.Ok(null);
        }

        var result = await SearchModsAsync(
                new CurseForgeSearchQuery
                {
                    Slug = slug.Trim(),
                    ClassId = CurseForgeClassIds.Mod,
                    PageSize = 1,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Result<CurseForgeMod?>.Ok(result.Value!.Mods.FirstOrDefault())
            : Result<CurseForgeMod?>.Fail(result.Error!);
    }

    public async Task<Result<IReadOnlyList<CurseForgeFile>>> GetFilesAsync(
        IReadOnlyList<int> fileIds,
        CancellationToken cancellationToken = default)
    {
        var ids = fileIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        if (ids.Length == 0)
        {
            return Result<IReadOnlyList<CurseForgeFile>>.Ok([]);
        }

        var files = new List<CurseForgeFile>(ids.Length);
        foreach (var chunk in ids.Chunk(MaxBatchSize))
        {
            var body = new { fileIds = chunk };
            var raw = await SendAsync(
                    HttpMethod.Post,
                    "/v1/mods/files",
                    body,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!raw.IsSuccess)
            {
                return Result<IReadOnlyList<CurseForgeFile>>.Fail(raw.Error!);
            }

            var parsed = ParseData<List<CurseForgeFile>>(raw.Value!);
            if (!parsed.IsSuccess)
            {
                return Result<IReadOnlyList<CurseForgeFile>>.Fail(parsed.Error!);
            }

            files.AddRange(parsed.Value!);
        }

        return Result<IReadOnlyList<CurseForgeFile>>.Ok(files);
    }

    public async Task<Result<CurseForgeFile>> GetFileAsync(
        int modId,
        int fileId,
        CancellationToken cancellationToken = default)
    {
        var raw = await SendAsync(
                HttpMethod.Get,
                $"/v1/mods/{modId}/files/{fileId}",
                null,
                cancellationToken)
            .ConfigureAwait(false);
        return raw.IsSuccess
            ? ParseData<CurseForgeFile>(raw.Value!)
            : Result<CurseForgeFile>.Fail(raw.Error!);
    }

    public async Task<Result<IReadOnlyList<CurseForgeFile>>> GetFilesForModAsync(
        int modId,
        string? gameVersion,
        CurseForgeModLoader? loader,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var path = $"/v1/mods/{modId}/files" + BuildQuery(
            ("gameVersion", gameVersion),
            ("modLoaderType", loader is null ? null : ((int)loader.Value).ToString()),
            ("index", "0"),
            ("pageSize", Math.Clamp(pageSize, 1, 50).ToString()));

        var raw = await SendAsync(HttpMethod.Get, path, null, cancellationToken)
            .ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<IReadOnlyList<CurseForgeFile>>.Fail(raw.Error!);
        }

        var parsed = ParseData<List<CurseForgeFile>>(raw.Value!);
        return parsed.IsSuccess
            ? Result<IReadOnlyList<CurseForgeFile>>.Ok(parsed.Value!)
            : Result<IReadOnlyList<CurseForgeFile>>.Fail(parsed.Error!);
    }

    public async Task<Result<string?>> GetDownloadUrlAsync(
        int modId,
        int fileId,
        CancellationToken cancellationToken = default)
    {
        var raw = await SendAsync(
                HttpMethod.Get,
                $"/v1/mods/{modId}/files/{fileId}/download-url",
                null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<string?>.Fail(raw.Error!);
        }

        try
        {
            using var document = JsonDocument.Parse(raw.Value!);
            if (!document.RootElement.TryGetProperty("data", out var data))
            {
                return Result<string?>.Ok(null);
            }

            return data.ValueKind switch
            {
                JsonValueKind.String => Result<string?>.Ok(data.GetString()),
                JsonValueKind.Null or JsonValueKind.Undefined => Result<string?>.Ok(null),
                _ => Result<string?>.Ok(null),
            };
        }
        catch (JsonException exception)
        {
            return InvalidResponse<string?>(exception);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _requestGate.Dispose();
    }

    private async Task<Result<byte[]>> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var bodyJson = body is null ? null : JsonSerializer.Serialize(body, EclJson.Default);

        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(RequestTimeout);

            try
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (bodyJson is not null)
                {
                    request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                }

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

                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        _logger.LogWarning(
                            "CurseForge 拒绝请求：{Method} {Path} -> {Status}",
                            method,
                            path,
                            (int)response.StatusCode);
                        return Result<byte[]>.Fail(
                            "curseforge.unauthorized",
                            "CurseForge 代理拒绝请求：代理服务端的 API key 无效或没有访问权限。",
                            $"HTTP {(int)response.StatusCode}");
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return Result<byte[]>.Fail(
                            "curseforge.not_found",
                            "CurseForge 上找不到请求的项目或文件。",
                            $"HTTP 404 {path}");
                    }

                    if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxAttempts)
                    {
                        var delay = GetRetryDelay(response, attempt);
                        _logger.LogWarning(
                            "CurseForge 限流，{Delay} 后重试（{Attempt}/{MaxAttempts}）。",
                            delay,
                            attempt,
                            MaxAttempts);
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                    {
                        await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogWarning(
                        "CurseForge 请求失败：{Method} {Path} -> {Status}",
                        method,
                        path,
                        (int)response.StatusCode);
                    return Result<byte[]>.Fail(
                        response.StatusCode == HttpStatusCode.TooManyRequests
                            ? "curseforge.rate_limited"
                            : "curseforge.http_error",
                        response.StatusCode == HttpStatusCode.TooManyRequests
                            ? "CurseForge 请求过于频繁，请稍后再试。"
                            : "CurseForge 请求失败。",
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                // 单次请求超时；继续重试。
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

        _logger.LogWarning(lastException, "CurseForge 请求重试耗尽：{Method} {Path}", method, path);
        return Result<byte[]>.Fail(
            "curseforge.network_error",
            "无法连接 CurseForge。",
            lastException?.Message);
    }

    private Result<T> ParseData<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return Result<T>.Fail("curseforge.invalid_response", "CurseForge 返回了空数据。");
            }

            var value = data.Deserialize<T>(EclJson.Default);
            return value is null
                ? Result<T>.Fail("curseforge.invalid_response", "CurseForge 返回了无法解析的数据。")
                : Result<T>.Ok(value);
        }
        catch (JsonException exception)
        {
            return InvalidResponse<T>(exception);
        }
    }

    private static Result<T> InvalidResponse<T>(JsonException exception) =>
        Result<T>.Fail("curseforge.invalid_response", "CurseForge 返回了无法解析的数据。", exception.Message);

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1));

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? fromDate = retryAfter?.Date is { } date
            ? date - DateTimeOffset.UtcNow
            : null;
        var delay = retryAfter?.Delta ?? fromDate ?? Backoff(attempt);
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
