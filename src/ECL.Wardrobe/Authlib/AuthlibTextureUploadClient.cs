// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ecl.Auth;
using Ecl.Core;
using Ecl.Wardrobe.Appearance;
using Ecl.Wardrobe.Png;

namespace Ecl.Wardrobe.Authlib;

/// <summary>
/// 读取角色资料的结果。除成功/失败外保留 HTTP 状态，便于区分未授权、重定向与网络故障。
/// </summary>
public sealed record AuthlibProfileReadResult
{
    public required bool IsSuccess { get; init; }

    public string? ProfileJson { get; init; }

    public AppearanceOperationStatus Status { get; init; } = AppearanceOperationStatus.Succeeded;

    public string Message { get; init; } = string.Empty;

    public string? Detail { get; init; }

    public HttpStatusCode? StatusCode { get; init; }

    public static AuthlibProfileReadResult Ok(string profileJson) => new()
    {
        IsSuccess = true,
        ProfileJson = profileJson,
    };

    public static AuthlibProfileReadResult Fail(
        AppearanceOperationStatus status,
        string message,
        string? detail = null,
        HttpStatusCode? statusCode = null) => new()
        {
            IsSuccess = false,
            Status = status,
            Message = message,
            Detail = detail,
            StatusCode = statusCode,
        };
}

/// <summary>可注入的角色资料读取器；测试可绕过网络，生产实现仍必须走 HTTPS 且不跟随重定向。</summary>
public interface IAuthlibProfileReader
{
    Task<AuthlibProfileReadResult> ReadProfileAsync(
        AuthServer server,
        string profileUuid,
        string accessToken,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 材质变更的三个可观察阶段。ADR 0038 要求分别表达，不能合并成一个模糊的“成功”。
/// </summary>
[Flags]
public enum AuthlibTextureUploadStage
{
    None = 0,

    /// <summary>服务端已接受 PUT/DELETE 请求（HTTP 2xx）。</summary>
    RequestAccepted = 1 << 0,

    /// <summary>重新读取角色资料后，目标材质的属性/模型确实发生了变化。</summary>
    ProfileUpdated = 1 << 1,

    /// <summary>游戏内是否实际显示仍需启动客户端/服务端验证；协议层无法确认。</summary>
    GameVerificationPending = 1 << 2,
}

/// <summary>材质上传/清除操作结果。</summary>
public sealed record AuthlibTextureUploadResult
{
    public required AppearanceOperationStatus Status { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? Detail { get; init; }

    /// <summary>服务端返回的原始 HTTP 状态（包括被拒绝跟随的 3xx），便于诊断。</summary>
    public HttpStatusCode? StatusCode { get; init; }

    public AuthlibTextureUploadStage Stages { get; init; }

    public bool IsSuccess => Status == AppearanceOperationStatus.Succeeded;

    public bool RequestAccepted => (Stages & AuthlibTextureUploadStage.RequestAccepted) != 0;

    public bool ProfileUpdated => (Stages & AuthlibTextureUploadStage.ProfileUpdated) != 0;

    public bool GameVerificationPending => (Stages & AuthlibTextureUploadStage.GameVerificationPending) != 0;
}

/// <summary>
/// Authlib-Injector 材质上传协议客户端（ADR 0038）。
/// <para>
/// 安全约束：
/// <list type="bullet">
/// <item>仅允许 HTTPS，且只把 Bearer 令牌发送到当前认证服务器主机；不跟随重定向。</item>
/// <item>路径严格为 <c>/api/user/profile/{unhyphenated-uuid}/skin|cape</c>，不做端点猜测。</item>
/// <item>上传前使用本地 PNG 校验；上传后重新读取角色资料，分别报告请求接受、资料更新和游戏内待验证。</item>
/// <item>错误消息与异常细节会脱敏，绝不包含 access token 或完整 Authorization 头。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AuthlibTextureUploadClient
{
    public const string HttpsRequiredMessage = "材质上传只允许 HTTPS 认证服务器地址。";

    private readonly HttpClient _httpClient;
    private readonly IAuthlibProfileReader? _profileReader;

    /// <summary>
    /// 使用注入的 <see cref="HttpMessageHandler"/> 创建客户端（测试/特殊传输层）。
    /// 如果传入 <see cref="HttpClientHandler"/>，会强制关闭自动重定向。
    /// </summary>
    public AuthlibTextureUploadClient(HttpMessageHandler handler, IAuthlibProfileReader? profileReader = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is HttpClientHandler httpClientHandler)
        {
            httpClientHandler.AllowAutoRedirect = false;
        }

        _httpClient = new HttpClient(handler, disposeHandler: false);
        _profileReader = profileReader;
    }

    /// <summary>
    /// 使用现成 HttpClient 创建客户端。调用方必须确保该客户端没有开启自动重定向，
    /// 否则 3xx 可能把 Authorization 头转发到其他主机。
    /// </summary>
    public AuthlibTextureUploadClient(HttpClient httpClient, IAuthlibProfileReader? profileReader = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _profileReader = profileReader;
    }

    /// <summary>供生产环境使用的默认 Handler：明确关闭自动重定向。</summary>
    public static HttpClientHandler CreateDefaultHandler() => new() { AllowAutoRedirect = false };

    /// <summary>只读取角色资料中的 uploadableTextures 能力，不发送任何写入请求。</summary>
    public async Task<Result<AuthlibTextureCapabilities>> GetCapabilitiesAsync(
        AuthServer server,
        string accessToken,
        string profileUuid,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeHttpsBaseUrl(server.Url, out _))
        {
            return Result<AuthlibTextureCapabilities>.Fail("authlib.insecure_url", HttpsRequiredMessage);
        }

        if (!TryNormalizeProfileUuid(profileUuid, out _, out var dashedUuid))
        {
            return Result<AuthlibTextureCapabilities>.Fail(
                "authlib.invalid_uuid",
                "角色 UUID 格式不正确，无法查询材质上传能力。");
        }

        var profile = await ReadProfileAsync(server, dashedUuid, accessToken, cancellationToken)
            .ConfigureAwait(false);
        if (!profile.IsSuccess)
        {
            return Result<AuthlibTextureCapabilities>.Fail(
                ErrorCode(profile.Status),
                profile.Message,
                profile.Detail);
        }

        return AuthlibTextureCapabilities.Parse(profile.ProfileJson);
    }

    /// <summary>上传/设置皮肤或披风。</summary>
    public Task<AuthlibTextureUploadResult> UploadAsync(
        AuthServer server,
        string accessToken,
        string profileUuid,
        WardrobeTextureType textureType,
        byte[] pngBytes,
        SkinModel skinModel = SkinModel.Unknown,
        bool supportsSkinModelParameter = true,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            HttpMethod.Put,
            server,
            accessToken,
            profileUuid,
            textureType,
            pngBytes,
            skinModel,
            supportsSkinModelParameter,
            cancellationToken);

    /// <summary>清除皮肤或披风（DELETE），不发送文件与 model 参数。</summary>
    public Task<AuthlibTextureUploadResult> ClearAsync(
        AuthServer server,
        string accessToken,
        string profileUuid,
        WardrobeTextureType textureType,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            HttpMethod.Delete,
            server,
            accessToken,
            profileUuid,
            textureType,
            pngBytes: null,
            skinModel: SkinModel.Unknown,
            supportsSkinModelParameter: false,
            cancellationToken);

    private async Task<AuthlibTextureUploadResult> MutateAsync(
        HttpMethod method,
        AuthServer server,
        string accessToken,
        string profileUuid,
        WardrobeTextureType textureType,
        byte[]? pngBytes,
        SkinModel skinModel,
        bool supportsSkinModelParameter,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeHttpsBaseUrl(server.Url, out var baseUrl))
        {
            return Fail(AppearanceOperationStatus.InvalidInput, HttpsRequiredMessage);
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Fail(
                AppearanceOperationStatus.Unauthorized,
                "访问令牌为空或已失效，请通过账户模块重新登录第三方账户。");
        }

        if (!TryNormalizeProfileUuid(profileUuid, out var unhyphenatedUuid, out var dashedUuid))
        {
            return Fail(AppearanceOperationStatus.InvalidInput, "角色 UUID 格式不正确，已拒绝发送请求。");
        }

        if (!Enum.IsDefined(textureType))
        {
            return Fail(AppearanceOperationStatus.InvalidInput, "未知的材质类型。");
        }

        if (method == HttpMethod.Put)
        {
            if (pngBytes is null || pngBytes.Length == 0)
            {
                return Fail(AppearanceOperationStatus.InvalidInput, "上传的材质文件为空。");
            }

            var validation = PngValidator.Validate(pngBytes, PngValidationLimits.Default);
            if (!validation.IsValid)
            {
                return Fail(
                    AppearanceOperationStatus.InvalidInput,
                    $"材质 PNG 校验失败：{validation.Message}",
                    $"PNG 校验状态：{validation.Status}");
            }
        }

        // 先读取当前角色资料：既用于能力声明，也作为上传后复查的基线。
        var before = await ReadProfileAsync(server, dashedUuid, accessToken, cancellationToken)
            .ConfigureAwait(false);
        if (!before.IsSuccess)
        {
            return Fail(before.Status, before.Message, before.Detail, before.StatusCode);
        }

        var capabilityResult = AuthlibTextureCapabilities.Parse(before.ProfileJson);
        if (!capabilityResult.IsSuccess)
        {
            return Fail(
                AppearanceOperationStatus.Unsupported,
                capabilityResult.Error?.Message ?? "无法解析角色资料中的材质上传能力。",
                capabilityResult.Error?.Detail);
        }

        var capabilities = capabilityResult.Value!;
        if (!capabilities.CanUpload(textureType))
        {
            var textureName = textureType == WardrobeTextureType.Skin ? "皮肤" : "披风";
            return Fail(
                AppearanceOperationStatus.Unsupported,
                $"服务端没有声明可上传{textureName}（uploadableTextures 缺失或不包含对应值）。",
                capabilities.Reason);
        }

        var origin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        var typeSegment = textureType == WardrobeTextureType.Skin ? "skin" : "cape";
        var requestUri = new Uri(
            $"{origin}/api/user/profile/{unhyphenatedUuid}/{typeSegment}",
            UriKind.Absolute);

        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");

        if (method == HttpMethod.Put)
        {
            var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(pngBytes!);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(
                fileContent,
                "file",
                textureType == WardrobeTextureType.Skin ? "skin.png" : "cape.png");

            // model 只用于皮肤，且仅在用户明确选择 Classic/Slim、协议支持时发送；
            // 披风绝不携带该参数。
            if (textureType == WardrobeTextureType.Skin &&
                supportsSkinModelParameter &&
                skinModel is SkinModel.Classic or SkinModel.Slim)
            {
                form.Add(
                    new StringContent(skinModel == SkinModel.Slim ? "slim" : string.Empty, Encoding.UTF8),
                    "model");
            }

            request.Content = form;
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return Fail(
                AppearanceOperationStatus.NetworkFailed,
                "材质请求超时，未确认服务端是否处理。",
                Redact(exception.Message, accessToken));
        }
        catch (HttpRequestException exception)
        {
            return Fail(
                AppearanceOperationStatus.NetworkFailed,
                "无法连接认证服务器，材质请求未完成。",
                Redact(exception.Message, accessToken));
        }
        catch (IOException exception)
        {
            return Fail(
                AppearanceOperationStatus.NetworkFailed,
                "读取认证服务器响应失败。",
                Redact(exception.Message, accessToken));
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode))
            {
                return Fail(
                    AppearanceOperationStatus.RedirectRejected,
                    "认证服务器返回重定向；为避免访问令牌泄露，已拒绝跟随。",
                    $"HTTP {(int)response.StatusCode}",
                    response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail(
                    MapHttpStatus(response.StatusCode),
                    FailureMessage(response.StatusCode),
                    $"HTTP {(int)response.StatusCode}",
                    response.StatusCode);
            }

            var stages = AuthlibTextureUploadStage.RequestAccepted |
                         AuthlibTextureUploadStage.GameVerificationPending;
            var message = "服务端已接受材质请求，但尚未确认角色资料已更新；游戏内显示仍待验证。";

            var after = await ReadProfileAsync(server, dashedUuid, accessToken, cancellationToken)
                .ConfigureAwait(false);
            if (after.IsSuccess &&
                TryGetTextureState(before.ProfileJson, textureType, out var beforeState) &&
                TryGetTextureState(after.ProfileJson, textureType, out var afterState) &&
                !string.Equals(beforeState, afterState, StringComparison.Ordinal))
            {
                stages |= AuthlibTextureUploadStage.ProfileUpdated;
                message = "服务端已接受请求，重新读取角色资料确认材质已更新；游戏内显示仍待验证。";
            }
            else if (!after.IsSuccess)
            {
                message = "服务端已接受请求，但无法重新读取角色资料；游戏内显示仍待验证。";
            }

            return new AuthlibTextureUploadResult
            {
                Status = AppearanceOperationStatus.Succeeded,
                Stages = stages,
                Message = message,
                StatusCode = response.StatusCode,
            };
        }
    }

    private async Task<AuthlibProfileReadResult> ReadProfileAsync(
        AuthServer server,
        string dashedUuid,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (_profileReader is not null)
        {
            return await _profileReader
                .ReadProfileAsync(server, dashedUuid, accessToken, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!TryNormalizeHttpsBaseUrl(server.Url, out var baseUrl))
        {
            return AuthlibProfileReadResult.Fail(
                AppearanceOperationStatus.InvalidInput,
                HttpsRequiredMessage);
        }

        if (!TryNormalizeProfileUuid(dashedUuid, out var unhyphenatedUuid, out _))
        {
            return AuthlibProfileReadResult.Fail(
                AppearanceOperationStatus.InvalidInput,
                "角色 UUID 格式不正确。");
        }

        var uri = new Uri(
            $"{baseUrl}/sessionserver/session/minecraft/profile/{unhyphenatedUuid}",
            UriKind.Absolute);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return AuthlibProfileReadResult.Fail(
                AppearanceOperationStatus.NetworkFailed,
                "读取角色资料超时。",
                Redact(exception.Message, accessToken));
        }
        catch (HttpRequestException exception)
        {
            return AuthlibProfileReadResult.Fail(
                AppearanceOperationStatus.NetworkFailed,
                "无法读取角色资料。",
                Redact(exception.Message, accessToken));
        }
        catch (IOException exception)
        {
            return AuthlibProfileReadResult.Fail(
                AppearanceOperationStatus.NetworkFailed,
                "读取角色资料响应失败。",
                Redact(exception.Message, accessToken));
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode))
            {
                return AuthlibProfileReadResult.Fail(
                    AppearanceOperationStatus.RedirectRejected,
                    "读取角色资料时服务器返回重定向；已拒绝跟随。",
                    $"HTTP {(int)response.StatusCode}",
                    response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return AuthlibProfileReadResult.Fail(
                    AppearanceOperationStatus.Unauthorized,
                    "读取角色资料被拒绝，账户凭据可能已失效。",
                    $"HTTP {(int)response.StatusCode}",
                    response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
            {
                return AuthlibProfileReadResult.Fail(
                    AppearanceOperationStatus.Unsupported,
                    "服务端没有返回该角色的资料。",
                    $"HTTP {(int)response.StatusCode}",
                    response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return AuthlibProfileReadResult.Fail(
                    AppearanceOperationStatus.ServerRejected,
                    "读取角色资料失败。",
                    $"HTTP {(int)response.StatusCode}",
                    response.StatusCode);
            }

            string profileJson;
            try
            {
                profileJson = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                return AuthlibProfileReadResult.Fail(
                    AppearanceOperationStatus.NetworkFailed,
                    "读取角色资料内容失败。",
                    Redact(exception.Message, accessToken));
            }

            if (string.IsNullOrWhiteSpace(profileJson))
            {
                return AuthlibProfileReadResult.Fail(
                    AppearanceOperationStatus.Unsupported,
                    "服务端返回了空的角色资料。");
            }

            return AuthlibProfileReadResult.Ok(profileJson);
        }
    }

    /// <summary>解析 HTTPS 根地址；仅接受绝对 HTTPS URL，且拒绝 userinfo/query/fragment。</summary>
    public static bool TryNormalizeHttpsBaseUrl(string? url, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    /// <summary>接受带连字符或不带连字符的 UUID，并同时给出两种规范形式。</summary>
    public static bool TryNormalizeProfileUuid(string? uuid, out string unhyphenated, out string dashed)
    {
        unhyphenated = string.Empty;
        dashed = string.Empty;

        if (string.IsNullOrWhiteSpace(uuid))
        {
            return false;
        }

        var raw = uuid.Trim();
        string hex;
        if (raw.Length == 32)
        {
            hex = raw;
        }
        else if (raw.Length == 36 &&
                 raw[8] == '-' &&
                 raw[13] == '-' &&
                 raw[18] == '-' &&
                 raw[23] == '-')
        {
            hex = raw.Replace("-", string.Empty, StringComparison.Ordinal);
        }
        else
        {
            return false;
        }

        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit))
        {
            return false;
        }

        hex = hex.ToLowerInvariant();
        unhyphenated = hex;
        dashed = $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
        return true;
    }

    private static bool TryGetTextureState(
        string? profileJson,
        WardrobeTextureType textureType,
        out string state)
    {
        state = string.Empty;
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(profileJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Array)
            {
                state = "<no-properties>";
                return true;
            }

            JsonElement? textureProperty = null;
            foreach (var property in properties.EnumerateArray())
            {
                if (property.ValueKind == JsonValueKind.Object &&
                    string.Equals(ReadString(property, "name"), "textures", StringComparison.Ordinal))
                {
                    textureProperty = property;
                    break;
                }
            }

            if (textureProperty is null)
            {
                state = "<no-textures-property>";
                return true;
            }

            var value = ReadString(textureProperty.Value, "value");
            var signature = ReadString(textureProperty.Value, "signature") ?? string.Empty;
            if (value is null)
            {
                state = $"<no-value>|{signature}";
                return true;
            }

            if (TryDecodeTextureValue(value, textureType, out var decoded))
            {
                state = decoded;
                return true;
            }

            // 非标准服务端可能返回非 Base64 值：退化为原始 value + signature 比较。
            state = $"raw:{value}|{signature}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryDecodeTextureValue(
        string value,
        WardrobeTextureType textureType,
        out string state)
    {
        state = string.Empty;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("textures", out var textures) ||
                textures.ValueKind != JsonValueKind.Object)
            {
                state = "<no-textures-object>";
                return true;
            }

            var key = textureType == WardrobeTextureType.Skin ? "SKIN" : "CAPE";
            if (!textures.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object)
            {
                state = "<absent>";
                return true;
            }

            var url = ReadString(entry, "url") ?? string.Empty;
            var model = string.Empty;
            if (entry.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                model = ReadString(metadata, "model") ?? string.Empty;
            }

            state = $"{url}|{model}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsRedirect(HttpStatusCode statusCode) => (int)statusCode is >= 300 and < 400;

    private static AppearanceOperationStatus MapHttpStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AppearanceOperationStatus.Unauthorized,
        HttpStatusCode.BadRequest or
            HttpStatusCode.RequestEntityTooLarge or
            HttpStatusCode.UnsupportedMediaType or
            HttpStatusCode.UnprocessableEntity => AppearanceOperationStatus.InvalidInput,
        HttpStatusCode.NotFound or
            HttpStatusCode.MethodNotAllowed or
            HttpStatusCode.NotImplemented => AppearanceOperationStatus.Unsupported,
        _ => AppearanceOperationStatus.ServerRejected,
    };

    private static string FailureMessage(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "认证失败或凭据已失效，请通过账户模块重新登录第三方账户。",
        HttpStatusCode.BadRequest or
            HttpStatusCode.RequestEntityTooLarge or
            HttpStatusCode.UnsupportedMediaType or
            HttpStatusCode.UnprocessableEntity =>
            "服务端拒绝了材质内容（文件格式、尺寸或请求字段不符合要求）。",
        HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
            "服务端没有实现该材质上传接口，当前账户仍可使用本地衣柜。",
        _ => "认证服务器返回错误，材质未被确认写入。",
    };

    private static AuthlibTextureUploadResult Fail(
        AppearanceOperationStatus status,
        string message,
        string? detail = null,
        HttpStatusCode? statusCode = null) => new()
        {
            Status = status,
            Message = message,
            Detail = detail,
            StatusCode = statusCode,
            Stages = AuthlibTextureUploadStage.None,
        };

    private static string ErrorCode(AppearanceOperationStatus status) => status switch
    {
        AppearanceOperationStatus.Unauthorized => "authlib.unauthorized",
        AppearanceOperationStatus.NetworkFailed => "authlib.network",
        AppearanceOperationStatus.RedirectRejected => "authlib.redirect",
        AppearanceOperationStatus.InvalidInput => "authlib.invalid_input",
        AppearanceOperationStatus.Unsupported => "authlib.unsupported",
        AppearanceOperationStatus.ServerRejected => "authlib.server_error",
        _ => "authlib.failed",
    };

    /// <summary>错误文本脱敏：先移除本次令牌，再交给通用的 Bearer/token 脱敏规则。</summary>
    private static string Redact(string? text, string accessToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = string.IsNullOrEmpty(accessToken)
            ? text
            : text.Replace(accessToken, "***", StringComparison.Ordinal);
        return SecretRedaction.Text(redacted);
    }
}
