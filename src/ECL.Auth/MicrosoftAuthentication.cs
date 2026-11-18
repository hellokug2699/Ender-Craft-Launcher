// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Auth;

public sealed record MicrosoftAuthenticationOptions
{
    public required string ClientId { get; init; }
    public string Authority { get; init; } = "https://login.microsoftonline.com/consumers";

    /// <summary>
    /// 回调主机。Entra 的 loopback 例外只对 <c>localhost</c> 忽略端口，
    /// 因此这里必须使用 localhost；127.0.0.1 会得到 invalid_request。
    /// </summary>
    public string RedirectHost { get; init; } = "localhost";
    public int RedirectPort { get; init; }

    /// <summary>
    /// 等待浏览器回调的最长时间。默认 5 分钟；测试可以缩短它来验证超时路径，
    /// 不再依赖外部 CancellationToken 模拟超时。
    /// </summary>
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// ECL 使用的 Microsoft OAuth public client 标识符。
/// 这是公开的应用标识符，不是秘密；登录链路依赖固定的应用注册，
/// 因此不在设置中暴露给用户修改。
/// </summary>
public static class MicrosoftClientIdentity
{
    public const string DefaultClientId = "a1e97b44-9c00-4a60-8e32-1119fefe7aab";
}

public interface IMicrosoftAuthenticationService
{
    Task<Result<Account>> SignInAsync(
        MicrosoftAuthenticationOptions options,
        Func<Uri, Task> openBrowser,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用 Microsoft refresh token 静默续期：不开浏览器、不经过 loopback 回调。
    /// </summary>
    Task<Result<AccountCredentials>> RefreshAsync(
        MicrosoftAuthenticationOptions options,
        string microsoftRefreshToken,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 显式发起一次交互式登录并返回可终止、可提交的会话（ADR 0065）。
/// 与 <see cref="IMicrosoftAuthenticationService.SignInAsync"/> 不同，调用方可以在
/// 浏览器打开后取消登录，并且必须先 <see cref="IMicrosoftLoginSession.TryCommit"/> 成功才能保存账户。
/// </summary>
public interface IMicrosoftLoginSessionService
{
    Result<IMicrosoftLoginSession> BeginSignIn(
        MicrosoftAuthenticationOptions options,
        Func<Uri, Task> openBrowser,
        CancellationToken cancellationToken = default);
}

public sealed class MicrosoftAuthenticationService : IMicrosoftAuthenticationService, IMicrosoftLoginSessionService
{
    private const string Scope = "XboxLive.signin offline_access";
    // Xbox Live / XSTS 契约使用 PascalCase 属性名；System.Net.Http.Json 默认的
    // camelCase 会被服务端以 HTTP 400 拒绝，因此这两类请求单独指定序列化选项。
    private static readonly JsonSerializerOptions XboxRequestJson = new() { PropertyNamingPolicy = null };
    private static readonly Uri XboxUserAuthEndpoint = new("https://user.auth.xboxlive.com/user/authenticate");
    private static readonly Uri XstsEndpoint = new("https://xsts.auth.xboxlive.com/xsts/authorize");
    private static readonly Uri MinecraftLoginEndpoint = new("https://api.minecraftservices.com/authentication/login_with_xbox");
    private static readonly Uri EntitlementsEndpoint = new("https://api.minecraftservices.com/entitlements/mcstore");
    private static readonly Uri ProfileEndpoint = new("https://api.minecraftservices.com/minecraft/profile");
    private readonly HttpClient _httpClient;
    private readonly ILogger<MicrosoftAuthenticationService>? _logger;
    private readonly Func<string, int, ILogger?, Result<IMicrosoftLoopbackServer>> _startLoopbackServer;

    public MicrosoftAuthenticationService(
        HttpClient? httpClient = null,
        ILogger<MicrosoftAuthenticationService>? logger = null)
        : this(httpClient, logger, HttpListenerLoopbackServer.Start)
    {
    }

    /// <summary>测试入口：替换 loopback 监听工厂，避免真实占用端口。</summary>
    internal MicrosoftAuthenticationService(
        HttpClient? httpClient,
        ILogger<MicrosoftAuthenticationService>? logger,
        Func<string, int, ILogger?, Result<IMicrosoftLoopbackServer>> startLoopbackServer)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger;
        _startLoopbackServer = startLoopbackServer ?? throw new ArgumentNullException(nameof(startLoopbackServer));
    }

    public Result<IMicrosoftLoginSession> BeginSignIn(
        MicrosoftAuthenticationOptions options,
        Func<Uri, Task> openBrowser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(openBrowser);

        if (!Guid.TryParse(options.ClientId, out _))
        {
            return Result<IMicrosoftLoginSession>.Fail("auth.invalid_client_id", "Microsoft Client ID 无效。");
        }

        // 授权地址与回调主机都决定授权码会被送到哪里：在打开浏览器或绑定端口之前就必须拒绝非法值。
        if (!IsAllowedAuthority(options.Authority))
        {
            return Result<IMicrosoftLoginSession>.Fail(
                "auth.invalid_authority",
                "Microsoft 授权地址必须是 login.microsoftonline.com 的 HTTPS 地址。");
        }

        if (!HttpListenerLoopbackServer.IsLoopbackHost(options.RedirectHost))
        {
            return Result<IMicrosoftLoginSession>.Fail(
                "auth.invalid_redirect_host",
                "Microsoft 登录回调只允许绑定本机回环地址（localhost / 127.0.0.1 / [::1]）。");
        }

        var serverResult = _startLoopbackServer(options.RedirectHost, options.RedirectPort, _logger);
        if (!serverResult.IsSuccess)
        {
            _logger?.LogWarning(
                "Microsoft 登录会话未能启动回调监听（host={RedirectHost}, port={RedirectPort}）：{ErrorCode}。",
                options.RedirectHost,
                options.RedirectPort,
                serverResult.Error?.Code);
            return Result<IMicrosoftLoginSession>.Fail(serverResult.Error!);
        }

        var server = serverResult.Value!;
        try
        {
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            // verifier 以字节数组保存（Dispose 时清零），但内容必须是 ASCII 的 base64url 文本：
            // 直接把原始随机字节当字符串会因非 ASCII 字节被替换，导致发出去的 verifier 与 challenge 不匹配。
            var verifier = Encoding.ASCII.GetBytes(Base64Url(RandomNumberGenerator.GetBytes(32)));
            var challenge = Base64Url(SHA256.HashData(verifier));
            var redirectUri = server.RedirectUri;

            var authorize = new UriBuilder($"{options.Authority.TrimEnd('/')}/oauth2/v2.0/authorize")
            {
                Query = QueryString(new Dictionary<string, string>
                {
                    ["client_id"] = options.ClientId,
                    ["response_type"] = "code",
                    ["redirect_uri"] = redirectUri.AbsoluteUri,
                    ["response_mode"] = "query",
                    ["scope"] = Scope,
                    ["state"] = state,
                    ["code_challenge"] = challenge,
                    ["code_challenge_method"] = "S256",
                }),
            };

            var timeout = options.LoginTimeout > TimeSpan.Zero ? options.LoginTimeout : TimeSpan.FromMinutes(5);
            var session = new MicrosoftLoginSession(
                Guid.NewGuid(),
                redirectUri,
                authorize.Uri,
                state,
                verifier,
                timeout,
                cancellationToken);

            _logger?.LogInformation(
                "开始 Microsoft 登录会话 {SessionId}，回调地址 {RedirectUri}。",
                session.Id,
                SecretRedaction.OAuthUrl(redirectUri));

            // 登录流程与调用方解耦：调用方只观察 Completion，因此 BeginSignIn 能立即返回可用于取消的会话。
            _ = RunAsync(session, server, options, openBrowser);
            return Result<IMicrosoftLoginSession>.Ok(session);
        }
        catch (Exception exception)
        {
            _ = server.DisposeAsync().AsTask();
            _logger?.LogWarning("创建 Microsoft 登录会话失败：{ErrorType}。", exception.GetType().Name);
            return Result<IMicrosoftLoginSession>.Fail("auth.login_failed", "无法开始 Microsoft 登录。");
        }
    }

    public async Task<Result<Account>> SignInAsync(
        MicrosoftAuthenticationOptions options,
        Func<Uri, Task> openBrowser,
        CancellationToken cancellationToken = default)
    {
        var started = BeginSignIn(options, openBrowser, cancellationToken);
        if (!started.IsSuccess)
        {
            return Result<Account>.Fail(started.Error!);
        }

        using var session = started.Value!;
        MicrosoftLoginOutcome outcome;
        try
        {
            outcome = await session.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Completion 正常只会以结果结束；保留兜底以兼容旧调用方对取消异常的预期。
            return Result<Account>.Fail("auth.login_cancelled", "Microsoft 登录已取消。");
        }

        // 提交是唯一的成功出口：取消/超时发生在“令牌已取得、账户未写”窗口时，这里会失败并丢弃账户。
        if (!session.TryCommit() || outcome.Account is null)
        {
            return Result<Account>.Fail(MapTermination(outcome));
        }

        _logger?.LogInformation(
            "Microsoft 登录会话 {SessionId} 已提交：phase={Phase} stop={StopReason}。",
            session.Id,
            session.Phase,
            session.StopReason);
        return Result<Account>.Ok(outcome.Account);
    }

    public async Task<Result<AccountCredentials>> RefreshAsync(
        MicrosoftAuthenticationOptions options,
        string microsoftRefreshToken,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(options.ClientId, out _))
        {
            return Result<AccountCredentials>.Fail("auth.invalid_client_id", "Microsoft Client ID 无效。");
        }

        if (string.IsNullOrWhiteSpace(microsoftRefreshToken))
        {
            return Result<AccountCredentials>.Fail("auth.credentials_missing", "Microsoft refresh token 为空，需要重新登录。");
        }

        var token = await RefreshMicrosoftTokenAsync(options, microsoftRefreshToken, cancellationToken).ConfigureAwait(false);
        if (!token.IsSuccess)
        {
            return Result<AccountCredentials>.Fail(token.Error!);
        }

        var minecraft = await SignInToMinecraftAsync(token.Value!, cancellationToken).ConfigureAwait(false);
        if (!minecraft.IsSuccess)
        {
            return Result<AccountCredentials>.Fail(minecraft.Error!);
        }

        return Result<AccountCredentials>.Ok(new AccountCredentials
        {
            MinecraftAccessToken = minecraft.Value!.AccessToken,
            // Microsoft 会轮换 refresh token：返回新的就必须保存新的，否则下次刷新会失效。
            MicrosoftRefreshToken = string.IsNullOrWhiteSpace(token.Value!.RefreshToken)
                ? microsoftRefreshToken
                : token.Value!.RefreshToken,
            MinecraftTokenExpiresAt = GetTokenExpiry(minecraft.Value!),
        });
    }

    /// <summary>
    /// 后台登录流程。顺序是安全契约的一部分：等待回调 → **立即关闭监听** → 校验 state →
    /// 校验 error/code → 用 PKCE verifier 兑换 → 组装账户 → 暂存待提交。
    /// 每一步都先看会话是否已终止，终止后任何迟到结果都会被丢弃。
    /// </summary>
    private async Task RunAsync(
        MicrosoftLoginSession session,
        IMicrosoftLoopbackServer server,
        MicrosoftAuthenticationOptions options,
        Func<Uri, Task> openBrowser)
    {
        try
        {
            if (!session.TryBeginWaiting())
            {
                return;
            }

            _logger?.LogDebug("Microsoft 登录会话 {SessionId} 进入等待回调。", session.Id);

            try
            {
                await openBrowser(session.AuthorizeUri).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(
                    "Microsoft 登录会话 {SessionId} 打开浏览器失败：{ErrorType}。",
                    session.Id,
                    exception.GetType().Name);
                session.Terminate(
                    MicrosoftLoginStopReason.ProtocolViolation,
                    new EclError("auth.browser_open_failed", "无法打开系统浏览器。"));
                return;
            }

            if (session.Phase != MicrosoftLoginPhase.WaitingForCallback)
            {
                return;
            }

            var callback = await server.WaitForCallbackAsync(session.CancellationToken).ConfigureAwait(false);
            // 授权码已经到手（或等待被终止）：先关闭监听，保证 state 校验与兑换期间没有第二个回调入口。
            await server.DisposeAsync().ConfigureAwait(false);

            if (callback is null || session.Phase != MicrosoftLoginPhase.WaitingForCallback)
            {
                return;
            }

            callback.Query.TryGetValue("state", out var returnedState);
            if (!session.TryAcceptCallback(returnedState))
            {
                if (session.Phase == MicrosoftLoginPhase.WaitingForCallback)
                {
                    _logger?.LogWarning("Microsoft 登录会话 {SessionId} state 校验失败，不兑换授权码。", session.Id);
                    session.Terminate(
                        MicrosoftLoginStopReason.StateMismatch,
                        new EclError("auth.state_mismatch", "Microsoft 登录状态校验失败。"));
                }

                return;
            }

            _logger?.LogDebug("Microsoft 登录会话 {SessionId} 通过 state 校验，开始兑换授权码。", session.Id);

            if (callback.Query.TryGetValue("error", out var callbackError)
                && !string.IsNullOrWhiteSpace(callbackError))
            {
                _logger?.LogWarning(
                    "Microsoft 登录会话 {SessionId} 被授权服务器拒绝：{OAuthError}。",
                    session.Id,
                    SanitizeOAuthError(callbackError));
                session.Terminate(
                    MicrosoftLoginStopReason.CallbackRejected,
                    new EclError("auth.microsoft_denied", "Microsoft 未授权本次登录。"));
                return;
            }

            if (!callback.Query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                _logger?.LogWarning("Microsoft 登录会话 {SessionId} 的回调缺少授权码。", session.Id);
                session.Terminate(
                    MicrosoftLoginStopReason.ProtocolViolation,
                    new EclError("auth.microsoft_denied", "Microsoft 未返回授权码。"));
                return;
            }

            var verifier = session.CopyVerifier();
            Result<OAuthToken> microsoftToken;
            try
            {
                microsoftToken = await ExchangeCodeAsync(
                    options,
                    code,
                    session.RedirectUri,
                    verifier,
                    session.CancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(verifier);
            }

            if (session.Phase != MicrosoftLoginPhase.RedeemingCode)
            {
                return;
            }

            if (!microsoftToken.IsSuccess)
            {
                session.Terminate(MicrosoftLoginStopReason.TokenExchangeFailed, microsoftToken.Error);
                return;
            }

            var minecraft = await SignInToMinecraftAsync(microsoftToken.Value!, session.CancellationToken).ConfigureAwait(false);
            if (session.Phase != MicrosoftLoginPhase.RedeemingCode)
            {
                return;
            }

            if (!minecraft.IsSuccess)
            {
                session.Terminate(MicrosoftLoginStopReason.TokenExchangeFailed, minecraft.Error);
                return;
            }

            var entitlements = await GetEntitlementsAsync(minecraft.Value!, session.CancellationToken).ConfigureAwait(false);
            if (session.Phase != MicrosoftLoginPhase.RedeemingCode)
            {
                return;
            }

            if (!entitlements.IsSuccess)
            {
                session.Terminate(MicrosoftLoginStopReason.TokenExchangeFailed, entitlements.Error);
                return;
            }

            var profile = await GetProfileAsync(minecraft.Value!, session.CancellationToken).ConfigureAwait(false);
            if (session.Phase != MicrosoftLoginPhase.RedeemingCode)
            {
                return;
            }

            if (!profile.IsSuccess)
            {
                session.Terminate(MicrosoftLoginStopReason.TokenExchangeFailed, profile.Error);
                return;
            }

            var account = new Account
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = AccountType.Microsoft,
                Name = profile.Value!.Name,
                Uuid = profile.Value.Id,
                AccessToken = minecraft.Value!.AccessToken,
                RefreshToken = microsoftToken.Value!.RefreshToken,
                MinecraftTokenExpiresAt = GetTokenExpiry(minecraft.Value!),
                CreatedAt = DateTimeOffset.UtcNow,
            };

            if (!session.TrySetPending(account))
            {
                _logger?.LogWarning(
                    "Microsoft 登录会话 {SessionId} 在结果提交前被终止，已丢弃取得的账户。",
                    session.Id);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消/超时已由 Terminate 记录停止原因；这里只保证异常不逃逸到线程池。
            session.Terminate(MicrosoftLoginStopReason.UserCancelled);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                "Microsoft 登录会话 {SessionId} 出现异常：{ErrorType}。",
                session.Id,
                exception.GetType().Name);
            session.Terminate(
                MicrosoftLoginStopReason.ProtocolViolation,
                new EclError("auth.exception", "Microsoft 登录出现异常。"));
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
            _logger?.LogInformation(
                "Microsoft 登录会话 {SessionId} 结束：phase={Phase} stop={StopReason}。",
                session.Id,
                session.Phase,
                session.StopReason);
        }
    }

    private static EclError MapTermination(MicrosoftLoginOutcome outcome) => outcome.StopReason switch
    {
        MicrosoftLoginStopReason.UserCancelled => new EclError("auth.login_cancelled", "Microsoft 登录已取消。"),
        MicrosoftLoginStopReason.TimedOut => new EclError("auth.login_timeout", "等待浏览器回调超时，未收到授权码。"),
        _ => new EclError(
            "auth.login_terminated",
            "Microsoft 登录已终止。",
            outcome.Error?.Code ?? outcome.StopReason.ToString()),
    };

    private static bool IsAllowedAuthority(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    /// <summary>Microsoft token → Xbox Live → XSTS → Minecraft Services。</summary>
    private async Task<Result<MinecraftToken>> SignInToMinecraftAsync(OAuthToken microsoftToken, CancellationToken cancellationToken)
    {
        var xbox = await AuthenticateXboxAsync(microsoftToken, cancellationToken).ConfigureAwait(false);
        if (!xbox.IsSuccess)
        {
            return Result<MinecraftToken>.Fail(xbox.Error!);
        }

        var xsts = await GetXstsAsync(xbox.Value!, cancellationToken).ConfigureAwait(false);
        if (!xsts.IsSuccess)
        {
            return Result<MinecraftToken>.Fail(xsts.Error!);
        }

        var minecraft = await LoginMinecraftAsync(xsts.Value!, cancellationToken).ConfigureAwait(false);
        return minecraft.IsSuccess
            ? minecraft
            : Result<MinecraftToken>.Fail(minecraft.Error!);
    }

    private async Task<Result<OAuthToken>> RefreshMicrosoftTokenAsync(
        MicrosoftAuthenticationOptions options,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        });
        return await SendJsonAsync<OAuthToken>(
            HttpMethod.Post,
            new Uri($"{options.Authority.TrimEnd('/')}/oauth2/v2.0/token"),
            content,
            "auth.microsoft_refresh",
            cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset? GetTokenExpiry(MinecraftToken token) =>
        token.ExpiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) : null;

    private async Task<Result<OAuthToken>> ExchangeCodeAsync(
        MicrosoftAuthenticationOptions options,
        string code,
        Uri redirectUri,
        byte[] verifier,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_verifier"] = Encoding.ASCII.GetString(verifier),
        });
        return await SendJsonAsync<OAuthToken>(
            HttpMethod.Post,
            new Uri($"{options.Authority.TrimEnd('/')}/oauth2/v2.0/token"),
            content,
            "auth.microsoft_token",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<XboxToken>> AuthenticateXboxAsync(OAuthToken token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, XboxUserAuthEndpoint)
        {
            Content = JsonContent.Create(new
            {
                Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = $"d={token.AccessToken}" },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT",
            }, options: XboxRequestJson),
        };
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        return await SendJsonAsync<XboxToken>(request, "auth.xbox", cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<XstsToken>> GetXstsAsync(XboxToken token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, XstsEndpoint)
        {
            Content = JsonContent.Create(new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { token.Token } },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT",
            }, options: XboxRequestJson),
        };
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        return await SendJsonAsync<XstsToken>(request, "auth.xsts", cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<MinecraftToken>> LoginMinecraftAsync(XstsToken token, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(new { identityToken = $"XBL3.0 x={token.UserHash};{token.Token}" });
        return await SendJsonAsync<MinecraftToken>(HttpMethod.Post, MinecraftLoginEndpoint, content, "auth.minecraft", cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<JsonElement>> GetEntitlementsAsync(MinecraftToken token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, EntitlementsEndpoint);
        request.Headers.Authorization = new("Bearer", token.AccessToken);
        var result = await SendJsonAsync<JsonElement>(request, "auth.entitlements", cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result;
        }

        if (!result.Value!.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            return Result<JsonElement>.Fail("auth.no_entitlement", "Microsoft 账户没有 Minecraft Java Edition 权益。");
        }

        return result;
    }

    private async Task<Result<MinecraftProfile>> GetProfileAsync(MinecraftToken token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileEndpoint);
        request.Headers.Authorization = new("Bearer", token.AccessToken);
        return await SendJsonAsync<MinecraftProfile>(request, "auth.profile", cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<T>> SendJsonAsync<T>(HttpMethod method, Uri uri, HttpContent? content, string errorCode, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        return await SendJsonAsync<T>(request, errorCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<T>> SendJsonAsync<T>(HttpRequestMessage request, string errorCode, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var oauthError = ReadOAuthError(body);

                // 响应体可能包含 token 片段或账户标识：只把稳定的 error 代码写日志，
                // 用户可见消息也只保留状态码与错误代码，绝不拼接响应体。
                _logger?.LogWarning(
                    "Microsoft OAuth 请求失败：{StatusCode} {OAuthError}。",
                    (int)response.StatusCode,
                    oauthError ?? "unknown");
                return Result<T>.Fail(errorCode, $"HTTP {(int)response.StatusCode}（{oauthError ?? "unknown"}）");
            }

            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false);
            return value is null ? Result<T>.Fail(errorCode, "服务器返回空响应。") : Result<T>.Ok(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning("Microsoft OAuth 请求异常：{ErrorType}。", exception.GetType().Name);
            return Result<T>.Fail(errorCode, exception.Message);
        }
    }

    private static string? ReadOAuthError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? SanitizeOAuthError(error.GetString())
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// OAuth error 代码会进入日志与界面，只保留安全字符并截断，避免把服务端返回的任意文本带进日志。
    /// </summary>
    private static string SanitizeOAuthError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var safe = new string(value
            .Take(64)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            .ToArray());
        return safe.Length == 0 ? "unknown" : safe;
    }

    private static string QueryString(IReadOnlyDictionary<string, string> values) =>
        string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record OAuthToken(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);

    private sealed record XboxToken(
        [property: JsonPropertyName("Token")] string Token,
        [property: JsonPropertyName("DisplayClaims")] XboxDisplayClaims DisplayClaims);

    private sealed record XboxDisplayClaims(
        [property: JsonPropertyName("xui")] XboxXui[] Xui);

    private sealed record XboxXui(
        [property: JsonPropertyName("uhs")] string Uhs);

    private sealed record XstsToken(
        [property: JsonPropertyName("Token")] string Token,
        [property: JsonPropertyName("DisplayClaims")] XboxDisplayClaims DisplayClaims)
    {
        public string UserHash => DisplayClaims.Xui.FirstOrDefault()?.Uhs ?? string.Empty;
    }

    private sealed record MinecraftToken(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record MinecraftProfile(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);
}
