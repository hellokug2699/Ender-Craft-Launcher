// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;

namespace Ecl.Auth;

/// <summary>Yggdrasil 认证结果：令牌 + 玩家档案。令牌只留在内存与安全凭据存储中。</summary>
public sealed record AuthlibSession
{
    public required string AccessToken { get; init; }

    public required string ClientToken { get; init; }

    public required string PlayerName { get; init; }

    /// <summary>已规范化为带连字符的 UUID。</summary>
    public required string Uuid { get; init; }

    /// <summary>服务器返回的可用角色数量，用于诊断“有账号但没有角色”。</summary>
    public int AvailableProfiles { get; init; }
}

/// <summary>
/// Authlib-Injector / Yggdrasil 认证 API 客户端。
/// <para>
/// 只负责协议本身；账号持久化与凭据加密分别由 <see cref="IAccountService"/> 和
/// <see cref="ISecureCredentialStore"/> 处理。密码只在登录调用中出现，不会被保存。
/// </para>
/// </summary>
public interface IAuthlibAuthenticationService
{
    Task<Result<AuthServerMetadata>> FetchMetadataAsync(string url, CancellationToken cancellationToken = default);

    Task<Result<AuthlibSession>> AuthenticateAsync(
        AuthServer server,
        string username,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>校验现有令牌是否仍然有效。</summary>
    Task<Result<bool>> ValidateAsync(AuthServer server, string accessToken, string clientToken, CancellationToken cancellationToken = default);

    /// <summary>用现有令牌换新令牌；返回新的会话（服务器有权拒绝并要求重新登录）。</summary>
    Task<Result<AuthlibSession>> RefreshAsync(
        AuthServer server,
        string accessToken,
        string clientToken,
        CancellationToken cancellationToken = default);
}

public sealed class AuthlibAuthenticationService : IAuthlibAuthenticationService
{
    /// <summary>认证会携带密码或令牌，因此非本机地址必须使用 HTTPS。</summary>
    public const string HttpsRequiredMessage = "认证服务器地址必须是 HTTPS 绝对地址（localhost 可使用 HTTP）。";

    private readonly HttpClient _httpClient;

    public AuthlibAuthenticationService(HttpMessageHandler? handler = null)
    {
        // 认证请求绝不跟随重定向，避免密码/令牌被 307/308 转发到其它主机。
        _httpClient = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler);
    }

    public async Task<Result<AuthServerMetadata>> FetchMetadataAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeUrl(url, out var normalized))
        {
            return Result<AuthServerMetadata>.Fail("authserver.invalid_url", HttpsRequiredMessage);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, normalized);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<AuthServerMetadata>.Fail(
                    "authserver.unreachable",
                    $"认证服务器返回 HTTP {(int)response.StatusCode}，请确认地址是认证服务器的 API 根地址。");
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseMetadata(normalized, payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return Result<AuthServerMetadata>.Fail("authserver.unreachable", $"无法访问认证服务器：{exception.Message}");
        }
    }

    public async Task<Result<AuthlibSession>> AuthenticateAsync(
        AuthServer server,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return Result<AuthlibSession>.Fail("auth.thirdparty_credentials_required", "请输入用户名与密码。");
        }

        var clientToken = Guid.NewGuid().ToString("N");
        var request = new AuthenticateRequest
        {
            Username = username.Trim(),
            Password = password,
            ClientToken = clientToken,
            Agent = new AgentRequest(),
            RequestUser = false,
        };

        var result = await PostJsonAsync<AuthenticateExchange>(server, "authserver/authenticate", request, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Result<AuthlibSession>.Fail(result.Error!);
        }

        var exchange = result.Value!;
        var profile = exchange.SelectedProfile;
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
        {
            return Result<AuthlibSession>.Fail(
                "auth.thirdparty_no_profile",
                "认证成功，但该账号没有可用的 Minecraft 角色（Profile）。");
        }

        return Result<AuthlibSession>.Ok(new AuthlibSession
        {
            AccessToken = exchange.AccessToken,
            ClientToken = string.IsNullOrWhiteSpace(exchange.ClientToken) ? clientToken : exchange.ClientToken,
            PlayerName = profile.Name,
            Uuid = NormalizeUuid(profile.Id),
            AvailableProfiles = exchange.AvailableProfiles?.Count ?? 0,
        });
    }

    public async Task<Result<bool>> ValidateAsync(
        AuthServer server,
        string accessToken,
        string clientToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Result<bool>.Ok(false);
        }

        if (!TryNormalizeUrl(server.Url, out var baseUrl))
        {
            return Result<bool>.Fail("authserver.insecure_url", HttpsRequiredMessage);
        }

        try
        {
            using var content = JsonContent.Create(new TokenRequest { AccessToken = accessToken, ClientToken = clientToken });
            using var response = await _httpClient
                .PostAsync($"{baseUrl}/authserver/validate", content, cancellationToken)
                .ConfigureAwait(false);

            // Yggdrasil 规范：204/200 表示令牌有效，403 表示无效。响应体为空，不能按 JSON 解析。
            if (response.IsSuccessStatusCode)
            {
                return Result<bool>.Ok(true);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                return Result<bool>.Ok(false);
            }

            return Result<bool>.Fail("authserver.unreachable", $"认证服务器返回 HTTP {(int)response.StatusCode}。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return Result<bool>.Fail("authserver.unreachable", $"无法连接认证服务器：{exception.Message}");
        }
    }

    public async Task<Result<AuthlibSession>> RefreshAsync(
        AuthServer server,
        string accessToken,
        string clientToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Result<AuthlibSession>.Fail("authserver.invalid_token", "登录凭据已失效，请重新登录。");
        }

        var result = await PostJsonAsync<AuthenticateExchange>(
            server,
            "authserver/refresh",
            new TokenRequest { AccessToken = accessToken, ClientToken = clientToken },
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Result<AuthlibSession>.Fail(result.Error!);
        }

        var exchange = result.Value!;
        var profile = exchange.SelectedProfile;
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
        {
            return Result<AuthlibSession>.Fail(
                "auth.thirdparty_no_profile",
                "令牌刷新成功，但服务器没有返回角色信息，请重新登录。");
        }

        return Result<AuthlibSession>.Ok(new AuthlibSession
        {
            AccessToken = exchange.AccessToken,
            ClientToken = string.IsNullOrWhiteSpace(exchange.ClientToken)
                ? clientToken ?? string.Empty
                : exchange.ClientToken,
            PlayerName = profile.Name,
            Uuid = NormalizeUuid(profile.Id),
            AvailableProfiles = exchange.AvailableProfiles?.Count ?? 0,
        });
    }

    public static Result<AuthServerMetadata> ParseMetadata(string normalizedUrl, string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return Result<AuthServerMetadata>.Fail(
                "authserver.invalid_metadata",
                "该地址没有返回 Authlib-Injector 元数据，请填写认证服务器的 API 根地址。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Result<AuthServerMetadata>.Fail("authserver.invalid_metadata", "认证服务器元数据格式不正确。");
            }

            root.TryGetProperty("meta", out var meta);
            root.TryGetProperty("skinDomains", out var skinDomains);

            if (meta.ValueKind != JsonValueKind.Object && skinDomains.ValueKind != JsonValueKind.Array)
            {
                return Result<AuthServerMetadata>.Fail(
                    "authserver.invalid_metadata",
                    "该地址不像是 Authlib-Injector 认证服务器（缺少 meta 与 skinDomains）。");
            }

            var serverName = ReadString(meta, "serverName");
            if (string.IsNullOrWhiteSpace(serverName))
            {
                serverName = new Uri(normalizedUrl).Host;
            }

            // 部分实现把注册链接放在 meta.links.register，用户没填时作为默认值。
            string? registerUrl = null;
            if (meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty("links", out var links) &&
                links.ValueKind == JsonValueKind.Object)
            {
                registerUrl = ReadString(links, "register");
            }

            var domains = new List<string>();
            if (skinDomains.ValueKind == JsonValueKind.Array)
            {
                foreach (var domain in skinDomains.EnumerateArray())
                {
                    if (domain.ValueKind == JsonValueKind.String && domain.GetString() is { Length: > 0 } value)
                    {
                        domains.Add(value);
                    }
                }
            }

            return Result<AuthServerMetadata>.Ok(new AuthServerMetadata
            {
                Url = normalizedUrl,
                ServerName = serverName.Trim(),
                ImplementationName = ReadString(meta, "implementationName"),
                ImplementationVersion = ReadString(meta, "implementationVersion"),
                RegisterUrl = registerUrl,
                SkinDomains = domains,
            });
        }
    }

    public static string NormalizeUuid(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 32 && value.All(Uri.IsHexDigit))
        {
            return $"{value[..8]}-{value[8..12]}-{value[12..16]}-{value[16..20]}-{value[20..]}";
        }

        return value;
    }

    public static bool TryNormalizeUrl(string? url, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(uri)))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    private static bool IsLoopbackHost(Uri uri) =>
        uri.IsLoopback ||
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private async Task<Result<T>> PostJsonAsync<T>(
        AuthServer server,
        string relativePath,
        object payload,
        CancellationToken cancellationToken,
        bool treatForbiddenAsResult = false)
    {
        if (!TryNormalizeUrl(server.Url, out var baseUrl))
        {
            return Result<T>.Fail("authserver.insecure_url", HttpsRequiredMessage);
        }

        try
        {
            using var content = JsonContent.Create(payload);
            using var response = await _httpClient
                .PostAsync($"{baseUrl}/{relativePath}", content, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false);
                return value is null
                    ? Result<T>.Fail("auth.thirdparty_invalid_response", "认证服务器返回了空响应。")
                    : Result<T>.Ok(value);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return MapFailure<T>(response.StatusCode, body, treatForbiddenAsResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return Result<T>.Fail("authserver.unreachable", $"无法连接认证服务器：{exception.Message}");
        }
        catch (JsonException)
        {
            return Result<T>.Fail("auth.thirdparty_invalid_response", "认证服务器返回了无法解析的响应。");
        }
    }

    internal static Result<T> MapFailure<T>(HttpStatusCode statusCode, string body, bool treatForbiddenAsResult = false)
    {
        string? error = null;
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                error = ReadString(root, "error");
                message = ReadString(root, "errorMessage") ?? ReadString(root, "message");
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体：只保留状态码。
        }

        var code = statusCode switch
        {
            HttpStatusCode.Forbidden => "auth.thirdparty_rejected",
            HttpStatusCode.Unauthorized => "auth.thirdparty_rejected",
            HttpStatusCode.BadRequest => "auth.thirdparty_rejected",
            _ => "authserver.unreachable",
        };

        if (treatForbiddenAsResult &&
            (statusCode == HttpStatusCode.Forbidden ||
             (statusCode == HttpStatusCode.BadRequest && string.Equals(error, "ForbiddenOperationException", StringComparison.Ordinal))))
        {
            code = "authserver.invalid_token";
        }

        // 不回显服务器返回的细节之外的内容：不会包含密码或令牌。
        var detail = message ?? error ?? $"HTTP {(int)statusCode}";
        return Result<T>.Fail(code, $"认证失败：{detail}");
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record AgentRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "Minecraft";

        [JsonPropertyName("version")]
        public int Version { get; init; } = 1;
    }

    private sealed record AuthenticateRequest
    {
        [JsonPropertyName("username")]
        public required string Username { get; init; }

        [JsonPropertyName("password")]
        public required string Password { get; init; }

        [JsonPropertyName("clientToken")]
        public required string ClientToken { get; init; }

        [JsonPropertyName("agent")]
        public required AgentRequest Agent { get; init; }

        [JsonPropertyName("requestUser")]
        public bool RequestUser { get; init; }
    }

    private sealed record TokenRequest
    {
        [JsonPropertyName("accessToken")]
        public required string AccessToken { get; init; }

        [JsonPropertyName("clientToken")]
        public string? ClientToken { get; init; }
    }

    private sealed record AuthenticateExchange
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; init; } = string.Empty;

        [JsonPropertyName("clientToken")]
        public string? ClientToken { get; init; }

        [JsonPropertyName("selectedProfile")]
        public ProfilePayload? SelectedProfile { get; init; }

        [JsonPropertyName("availableProfiles")]
        public List<ProfilePayload>? AvailableProfiles { get; init; }
    }

    private sealed record ProfilePayload
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }
}
