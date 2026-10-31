// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;

namespace Ecl.Auth;

public sealed record MicrosoftAuthenticationOptions
{
    public required string ClientId { get; init; }
    public string Authority { get; init; } = "https://login.microsoftonline.com/consumers";
    public string RedirectHost { get; init; } = "127.0.0.1";
    public int RedirectPort { get; init; }
}

public interface IMicrosoftAuthenticationService
{
    Task<Result<Account>> SignInAsync(
        MicrosoftAuthenticationOptions options,
        Func<Uri, Task> openBrowser,
        CancellationToken cancellationToken = default);
}

public sealed class MicrosoftAuthenticationService : IMicrosoftAuthenticationService
{
    private const string Scope = "XboxLive.signin offline_access";
    private static readonly Uri XboxUserAuthEndpoint = new("https://user.auth.xboxlive.com/user/authenticate");
    private static readonly Uri XstsEndpoint = new("https://xsts.auth.xboxlive.com/xsts/authorize");
    private static readonly Uri MinecraftLoginEndpoint = new("https://api.minecraftservices.com/authentication/login_with_xbox");
    private static readonly Uri EntitlementsEndpoint = new("https://api.minecraftservices.com/entitlements/mcstore");
    private static readonly Uri ProfileEndpoint = new("https://api.minecraftservices.com/minecraft/profile");
    private readonly HttpClient _httpClient;

    public MicrosoftAuthenticationService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<Result<Account>> SignInAsync(
        MicrosoftAuthenticationOptions options,
        Func<Uri, Task> openBrowser,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(options.ClientId, out _))
        {
            return Result<Account>.Fail("auth.invalid_client_id", "Microsoft Client ID 无效。");
        }

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var port = options.RedirectPort > 0 ? options.RedirectPort : GetFreePort();
        var redirectUri = $"http://{options.RedirectHost}:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var authorize = new UriBuilder($"{options.Authority.TrimEnd('/')}/oauth2/v2.0/authorize");
        authorize.Query = QueryString(new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });

        var contextTask = listener.GetContextAsync().WaitAsync(cancellationToken);
        var browserTask = openBrowser(authorize.Uri);
        var context = await contextTask.ConfigureAwait(false);
        var query = context.Request.QueryString;
        var returnedState = query["state"];
        var error = query["error"];
        var code = query["code"];
        await WriteBrowserResponseAsync(context.Response, error is null ? "登录完成，请返回 ECL。" : "登录失败，请返回 ECL 查看错误。").ConfigureAwait(false);
        await browserTask.ConfigureAwait(false);

        if (!string.Equals(state, returnedState, StringComparison.Ordinal))
        {
            return Result<Account>.Fail("auth.state_mismatch", "Microsoft 登录状态校验失败。");
        }

        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            return Result<Account>.Fail("auth.microsoft_denied", error ?? "Microsoft 未返回授权码。");
        }

        var microsoftToken = await ExchangeCodeAsync(options, code, redirectUri, verifier, cancellationToken).ConfigureAwait(false);
        if (!microsoftToken.IsSuccess)
        {
            return Result<Account>.Fail(microsoftToken.Error!.Code, microsoftToken.Error.Message);
        }

        var xbox = await AuthenticateXboxAsync(microsoftToken.Value!, cancellationToken).ConfigureAwait(false);
        if (!xbox.IsSuccess)
        {
            return Result<Account>.Fail(xbox.Error!.Code, xbox.Error.Message);
        }

        var xsts = await GetXstsAsync(xbox.Value!, cancellationToken).ConfigureAwait(false);
        if (!xsts.IsSuccess)
        {
            return Result<Account>.Fail(xsts.Error!.Code, xsts.Error.Message);
        }

        var minecraft = await LoginMinecraftAsync(xsts.Value!, cancellationToken).ConfigureAwait(false);
        if (!minecraft.IsSuccess)
        {
            return Result<Account>.Fail(minecraft.Error!.Code, minecraft.Error.Message);
        }

        var entitlements = await GetEntitlementsAsync(minecraft.Value!, cancellationToken).ConfigureAwait(false);
        if (!entitlements.IsSuccess)
        {
            return Result<Account>.Fail(entitlements.Error!.Code, entitlements.Error.Message);
        }

        var profile = await GetProfileAsync(minecraft.Value!, cancellationToken).ConfigureAwait(false);
        if (!profile.IsSuccess)
        {
            return Result<Account>.Fail(profile.Error!.Code, profile.Error.Message);
        }

        var account = new Account
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = AccountType.Microsoft,
            Name = profile.Value!.Name,
            Uuid = profile.Value.Id,
            AccessToken = minecraft.Value!.AccessToken,
            RefreshToken = microsoftToken.Value!.RefreshToken,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        return Result<Account>.Ok(account);
    }

    private async Task<Result<OAuthToken>> ExchangeCodeAsync(MicrosoftAuthenticationOptions options, string code, string redirectUri, string verifier, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        });
        return await SendJsonAsync<OAuthToken>(HttpMethod.Post, new Uri($"{options.Authority.TrimEnd('/')}/oauth2/v2.0/token"), content, "auth.microsoft_token", cancellationToken).ConfigureAwait(false);
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
            }),
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
            }),
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
                var detail = body.Length > 512 ? body[..512] : body;
                return Result<T>.Fail(errorCode, $"HTTP {(int)response.StatusCode}: {detail}");
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
            return Result<T>.Fail(errorCode, exception.Message);
        }
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string QueryString(IReadOnlyDictionary<string, string> values) => string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task WriteBrowserResponseAsync(HttpListenerResponse response, string message)
    {
        var bytes = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>ECL</title><p>{WebUtility.HtmlEncode(message)}</p>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private sealed record OAuthToken(string AccessToken, string? RefreshToken);
    private sealed record XboxToken(string Token, XboxDisplayClaims DisplayClaims);
    private sealed record XboxDisplayClaims(XboxXui[] Xui);
    private sealed record XboxXui(string Uhs);
    private sealed record XstsToken(string Token, XboxDisplayClaims DisplayClaims)
    {
        public string UserHash => DisplayClaims.Xui.FirstOrDefault()?.Uhs ?? string.Empty;
    }
    private sealed record MinecraftToken(string AccessToken, int ExpiresIn);
    private sealed record MinecraftProfile(string Id, string Name);
}
