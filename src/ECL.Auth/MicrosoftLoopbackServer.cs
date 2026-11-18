// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Text;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Auth;

/// <summary>一次 loopback 回调请求；Query 只包含解析后的键值，不保留原始 URL。</summary>
internal sealed record MicrosoftCallback(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query);

/// <summary>
/// 本机回调监听。抽成接口是为了让登录流程可以在不真正占用端口的情况下测试，
/// 同时也让 HttpListener 的平台差异不进入会话状态机。
/// </summary>
internal interface IMicrosoftLoopbackServer : IAsyncDisposable
{
    Uri RedirectUri { get; }

    /// <summary>
    /// 等待一个可用的授权回调；普通探测（favicon、错误方法、错误路径、缺少 code）只会收到错误页并继续等待。
    /// 监听被释放或取消时返回 <c>null</c>。
    /// </summary>
    Task<MicrosoftCallback?> WaitForCallbackAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 基于 <see cref="HttpListener"/> 的 loopback 实现。只允许绑定 localhost / 127.0.0.1 / [::1]：
/// 回调地址不能指向局域网或公网接口，否则授权码可能在传输途中被第三方截获。
/// </summary>
internal sealed class HttpListenerLoopbackServer : IMicrosoftLoopbackServer
{
    private const int MaxStartAttempts = 5;

    private readonly HttpListener _listener;
    private readonly ILogger? _logger;
    private int _disposed;

    private HttpListenerLoopbackServer(HttpListener listener, Uri redirectUri, ILogger? logger)
    {
        _listener = listener;
        RedirectUri = redirectUri;
        _logger = logger;
    }

    public Uri RedirectUri { get; }

    internal static bool IsLoopbackHost(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "[::1]", StringComparison.Ordinal));

    public static Result<IMicrosoftLoopbackServer> Start(string host, int port, ILogger? logger)
    {
        if (!IsLoopbackHost(host))
        {
            return Result<IMicrosoftLoopbackServer>.Fail(
                "auth.invalid_redirect_host",
                "Microsoft 登录回调只允许绑定本机回环地址（localhost / 127.0.0.1 / [::1]）。");
        }

        // 端口超出 IPEndPoint 范围时 HttpListener 前缀本身就不合法，直接按监听不可用处理。
        if (port > IPEndPoint.MaxPort)
        {
            return Result<IMicrosoftLoopbackServer>.Fail(
                "auth.callback_listener_unavailable",
                "Microsoft 登录回调端口超出有效范围。");
        }

        for (var attempt = 1; attempt <= MaxStartAttempts; attempt++)
        {
            // 端口 0 表示由系统分配；固定端口被占用时仍然重试若干次，避免瞬时冲突直接失败。
            var candidatePort = port > 0 ? port : GetFreePort();
            var prefix = $"http://{host}:{candidatePort}/";
            HttpListener? listener = null;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();
                var redirectUri = new Uri(prefix, UriKind.Absolute);
                logger?.LogDebug(
                    "Microsoft 登录回环监听已就绪：{RedirectUri}（第 {Attempt} 次尝试）。",
                    SecretRedaction.OAuthUrl(redirectUri),
                    attempt);
                return Result<IMicrosoftLoopbackServer>.Ok(new HttpListenerLoopbackServer(listener, redirectUri, logger));
            }
            catch (Exception exception) when (IsStartFailure(exception))
            {
                logger?.LogWarning(
                    "Microsoft 登录回环监听启动失败（第 {Attempt} 次，{RedirectUri}）：{ErrorType}。",
                    attempt,
                    SecretRedaction.OAuthUrl(new Uri(prefix, UriKind.Absolute)),
                    exception.GetType().Name);
                if (listener is not null)
                {
                    CloseListener(listener);
                }
            }
        }

        logger?.LogWarning("Microsoft 登录回环监听在 {Attempts} 次尝试后仍不可用。", MaxStartAttempts);
        return Result<IMicrosoftLoopbackServer>.Fail(
            "auth.callback_listener_unavailable",
            "无法启动本机登录回调监听（端口被占用或缺少权限）。");
    }

    public async Task<MicrosoftCallback?> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception exception)
            {
                // 监听被 Stop/Close 时 GetContextAsync 会以平台相关异常结束；这是正常的释放路径。
                if (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return null;
                }

                _logger?.LogDebug("Microsoft 登录回调监听读取请求失败：{ErrorType}。", exception.GetType().Name);
                return null;
            }

            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "/";
            if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(path, "/", StringComparison.Ordinal))
            {
                // 浏览器会并行请求 favicon 等资源：这类探测必须被忽略，不能终止仍在等待的登录。
                await WriteResponseAsync(context.Response, 404, "Not Found").ConfigureAwait(false);
                continue;
            }

            var query = ParseQuery(request.QueryString);
            if (!query.ContainsKey("code") && !query.ContainsKey("error"))
            {
                await WriteResponseAsync(context.Response, 400, "缺少授权码。").ConfigureAwait(false);
                continue;
            }

            await WriteResponseAsync(
                context.Response,
                200,
                query.ContainsKey("error") ? "登录失败，请返回 ECL 查看错误。" : "登录完成，请返回 ECL。").ConfigureAwait(false);
            return new MicrosoftCallback(request.HttpMethod, path, query);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // 先 Stop 再 Close：Stop 让挂起的 GetContextAsync 立即返回，Close 释放 HTTP.sys 端口注册。
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // 已停止或从未启动的监听器不需要再处理。
        }

        CloseListener(_listener);
        _logger?.LogDebug("Microsoft 登录回环监听已释放：{RedirectUri}。", SecretRedaction.OAuthUrl(RedirectUri));
        return ValueTask.CompletedTask;
    }

    private static bool IsStartFailure(Exception exception) =>
        exception is HttpListenerException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or InvalidOperationException
            or ObjectDisposedException
            or ArgumentException;

    private static void CloseListener(HttpListener listener)
    {
        try
        {
            listener.Close();
        }
        catch (Exception)
        {
            // 关闭失败不影响安全结论：回调端口要么已释放，要么由进程退出回收。
        }
    }

    private static Dictionary<string, string> ParseQuery(
        System.Collections.Specialized.NameValueCollection values)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in values.AllKeys)
        {
            if (key is null)
            {
                continue;
            }

            var value = values[key];
            if (!string.IsNullOrEmpty(value))
            {
                query[key] = value;
            }
        }

        return query;
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, int statusCode, string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(
                $"<!doctype html><meta charset=\"utf-8\"><title>ECL</title><p>{WebUtility.HtmlEncode(message)}</p>");
            response.StatusCode = statusCode;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 浏览器提前断开连接时写响应会失败；用户仍可在启动器里看到最终结果。
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // 响应已关闭或连接已断开，无需处理。
            }
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
