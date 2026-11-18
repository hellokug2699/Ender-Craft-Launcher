// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Ecl.Auth;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Tests.Auth;

/// <summary>
/// F-11 登录流程测试的共用装配：假 loopback 监听 + 假 HTTP 链路，
/// 不依赖真实浏览器、真实端口或网络。
/// </summary>
internal sealed class MicrosoftSignInTestHarness : IDisposable
{
    public const string ClientId = "a1e97b44-9c00-4a60-8e32-1119fefe7aab";
    public const string AuthCode = "the-auth-code";

    private int _browserOpenCount;

    public MicrosoftSignInTestHarness(
        CapturingLogger<MicrosoftAuthenticationService>? logger = null,
        Func<string, int, ILogger?, Result<IMicrosoftLoopbackServer>>? startLoopbackServer = null)
    {
        Client = new HttpClient(Handler);
        Service = new MicrosoftAuthenticationService(
            Client,
            logger,
            startLoopbackServer ?? ((_, _, _) => Result<IMicrosoftLoopbackServer>.Ok(Server)));
    }

    public FakeAuthenticationHandler Handler { get; } = new();

    public FakeLoopbackServer Server { get; } = new();

    public HttpClient Client { get; }

    public MicrosoftAuthenticationService Service { get; }

    public TaskCompletionSource<Uri> BrowserOpened { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int BrowserOpenCount => Volatile.Read(ref _browserOpenCount);

    public Task OpenBrowserAsync(Uri uri)
    {
        Interlocked.Increment(ref _browserOpenCount);
        BrowserOpened.TrySetResult(uri);
        return Task.CompletedTask;
    }

    public static MicrosoftAuthenticationOptions Options(TimeSpan? loginTimeout = null) => new()
    {
        ClientId = ClientId,
        LoginTimeout = loginTimeout ?? TimeSpan.FromMinutes(1),
    };

    public static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            query[Uri.UnescapeDataString(parts[0])] = parts.Length > 1
                ? Uri.UnescapeDataString(parts[1])
                : string.Empty;
        }

        return query;
    }

    public static MicrosoftCallback CreateCallback(string? state, string code = AuthCode, string? error = null)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (error is not null)
        {
            query["error"] = error;
        }
        else
        {
            query["code"] = code;
        }

        if (state is not null)
        {
            query["state"] = state;
        }

        return new MicrosoftCallback("GET", "/", query);
    }

    public void Dispose() => Client.Dispose();
}

internal sealed class FakeLoopbackServer : IMicrosoftLoopbackServer
{
    private readonly TaskCompletionSource<MicrosoftCallback?> _callback =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Uri RedirectUri { get; } = new("http://localhost:49152/");

    public bool Disposed { get; private set; }

    public void Complete(MicrosoftCallback callback) => _callback.TrySetResult(callback);

    public Task<MicrosoftCallback?> WaitForCallbackAsync(CancellationToken cancellationToken) =>
        _callback.Task.WaitAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeAuthenticationHandler : HttpMessageHandler
{
    private static readonly Uri XboxUserAuthEndpoint = new("https://user.auth.xboxlive.com/user/authenticate");
    private static readonly Uri XstsEndpoint = new("https://xsts.auth.xboxlive.com/xsts/authorize");
    private static readonly Uri MinecraftLoginEndpoint = new("https://api.minecraftservices.com/authentication/login_with_xbox");

    public int TokenRequestCount { get; private set; }

    public string? AuthorizationCode { get; private set; }

    public string? CodeVerifier { get; private set; }

    public string? RedirectUri { get; private set; }

    public string? GrantType { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal))
        {
            TokenRequestCount++;
            var form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
            AuthorizationCode = form.GetValueOrDefault("code");
            CodeVerifier = form.GetValueOrDefault("code_verifier");
            RedirectUri = form.GetValueOrDefault("redirect_uri");
            GrantType = form.GetValueOrDefault("grant_type");
            return Json(HttpStatusCode.OK, new
            {
                access_token = "microsoft-access-token",
                refresh_token = "microsoft-refresh-token",
            });
        }

        if (uri == XboxUserAuthEndpoint)
        {
            return Json(HttpStatusCode.OK, new
            {
                Token = "xbox-token",
                DisplayClaims = new { xui = new[] { new { uhs = "user-hash" } } },
            });
        }

        if (uri == XstsEndpoint)
        {
            return Json(HttpStatusCode.OK, new
            {
                Token = "xsts-token",
                DisplayClaims = new { xui = new[] { new { uhs = "user-hash" } } },
            });
        }

        if (uri == MinecraftLoginEndpoint)
        {
            return Json(HttpStatusCode.OK, new { access_token = "minecraft-access-token", expires_in = 3600 });
        }

        if (uri.AbsolutePath == "/entitlements/mcstore")
        {
            return Json(HttpStatusCode.OK, new { items = new[] { new { name = "game" } } });
        }

        if (uri.AbsolutePath == "/minecraft/profile")
        {
            return Json(HttpStatusCode.OK, new { id = "profile-uuid", name = "Steve" });
        }

        throw new InvalidOperationException($"Unexpected request: {request.Method} {uri}");
    }

    private static Dictionary<string, string> ParseForm(string form)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in form.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            values[Uri.UnescapeDataString(parts[0].Replace('+', ' '))] = parts.Length > 1
                ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                : string.Empty;
        }

        return values;
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, object value) => new(statusCode)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}
