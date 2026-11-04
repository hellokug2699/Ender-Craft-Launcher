// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Core;

namespace ECL.Knowledge;

/// <summary>
/// ADR 0036 的受限 Minecraft 知识浏览器站点策略。
/// </summary>
/// <remarks>
/// <para>
/// 白名单是底层网络边界，而不是界面上的隐藏输入框：只有 HTTPS、且规范化主机名
/// 属于 <c>zh.minecraft.wiki</c> 或 <c>mcmod.cn</c> 域名树的请求才可能返回成功。
/// 主机判断基于 <see cref="Uri"/> 解析后的 hostname 与 DNS 标签边界（等于域名或以
/// “.域名”结尾），不使用字符串包含；用户信息段、端口和路径都不能改变归属判断。
/// </para>
/// <para>
/// 本类型只实现可测试的策略核心，不包含 WebView 宿主。宿主缺失是已知阻塞项：
/// 要满足 ADR 0036 的“在跟随重定向前拒绝”，宿主必须在网络响应层取得原始 3xx
/// 状态码和 Location 并调用本策略；仅在导航完成后检查最终 URL 不满足要求。
/// 在宿主补齐之前，不得宣称已完成 WebView 集成。
/// </para>
/// </remarks>
public static class KnowledgeSitePolicy
{
    private const string HttpsScheme = "https";

    private static readonly string[] DomainTrees = ["zh.minecraft.wiki", "mcmod.cn"];

    /// <summary>允许访问的域名树；仅用于展示/审计，运行时不得据此放宽白名单。</summary>
    public static IReadOnlyList<string> AllowedDomainTrees { get; } = Array.AsReadOnly(DomainTrees);

    /// <summary>
    /// 判断一个主机名是否属于允许的域名树。输入会按 IDN、大小写和尾随根点规范化。
    /// </summary>
    /// <remarks>该重载不解析 URL，因此不接受包含端口、路径或用户信息的主机字符串。</remarks>
    public static bool IsAllowedHost(string? hostname) =>
        TryNormalizeHost(hostname, out var normalizedHost, out _) && IsAllowedNormalizedHost(normalizedHost);

    /// <summary>
    /// 校验一次顶层导航。返回的 <see cref="Result{T}"/> 成功值即规范化后的目标，
    /// 失败值为稳定错误代码。
    /// </summary>
    /// <remarks>
    /// 仅接受绝对 HTTPS 地址；HTTP 不会被自动升级后放行。空地址、解析失败、
    /// 含用户信息段、无法完成 IDN/DNS 规范化的地址均拒绝。
    /// </remarks>
    public static Result<KnowledgeTarget> ValidateNavigation(string? absoluteUrl)
    {
        if (!TryCreateAbsoluteUri(absoluteUrl, out var uri, out var error))
        {
            return Result<KnowledgeTarget>.Fail(error!);
        }

        return ValidateParsedUri(uri, absoluteUrl!);
    }

    /// <summary>
    /// ADR 0036 要求的导航入口：校验绝对 URL，并通过 <paramref name="decision"/>
    /// 返回完整判定（含被拒绝的原始目标）。
    /// </summary>
    /// <returns>允许导航时为 <see langword="true"/>；否则为 <see langword="false"/>。</returns>
    public static bool TryValidateNavigation(string absoluteUrl, out KnowledgeNavigationDecision decision)
    {
        var requested = absoluteUrl ?? string.Empty;
        var result = ValidateNavigation(requested);
        decision = result.IsSuccess
            ? KnowledgeNavigationDecision.Allow(requested, result.Value!)
            : KnowledgeNavigationDecision.Deny(requested, result.Error!);

        return decision.IsAllowed;
    }

    /// <summary>
    /// 校验图片、脚本、样式、字体、XHR/fetch、iframe 等子资源请求。
    /// </summary>
    /// <remarks>
    /// 子资源与顶层导航使用完全相同的 HTTPS + 域名树策略；不因为页面引用了 CDN、
    /// 统计服务或第三方图片站而放宽。相对地址必须先由调用方解析为绝对地址。
    /// </remarks>
    public static Result<KnowledgeTarget> ValidateSubresource(string? url) => ValidateNavigation(url);

    /// <summary>子资源/iframe 白名单判断的布尔便捷入口。</summary>
    public static bool IsAllowedSubresource(string? url) => ValidateSubresource(url).IsSuccess;

    /// <summary>
    /// 校验一个 HTTP 3xx 重定向响应，决定是否允许跟随。
    /// </summary>
    /// <param name="statusCode">真实 HTTP 状态码；仅处理 301、302、303、307、308。</param>
    /// <param name="location">原始 Location 头；相对地址会相对 <paramref name="requestUrl"/> 解析。</param>
    /// <param name="requestUrl">收到该响应的、已经允许的原始请求地址。</param>
    /// <remarks>
    /// <para>
    /// 每一跳都必须单独调用本方法。只有 <see cref="KnowledgeRedirectDecision.IsAllowed"/>
    /// 为真时，才能把 <see cref="KnowledgeRedirectDecision.Target"/> 的绝对地址作为下一跳；
    /// 连续重定向不得绕过检查。
    /// </para>
    /// <para>
    /// 拒绝时保留真实 3xx 状态码、原始请求地址和被拒绝的目标。缺失、含控制字符或无法
    /// 解析的 Location 一律拒绝；重定向到 HTTP 或白名单外主机同样拒绝，不做自动升级。
    /// 注意：本方法不会发起网络请求，宿主必须在跟随之前调用它。
    /// </para>
    /// </remarks>
    public static KnowledgeRedirectDecision EvaluateRedirect(int statusCode, string? location, string requestUrl)
    {
        var requested = requestUrl ?? string.Empty;

        if (!IsRedirectStatusCode(statusCode))
        {
            return KnowledgeRedirectDecision.Deny(
                statusCode,
                requested,
                location,
                null,
                new EclError(
                    KnowledgeErrorCodes.RedirectBlocked,
                    "重定向被拒绝：不支持的 HTTP 状态码。",
                    $"仅处理 301、302、303、307、308；实际为 {statusCode}。"));
        }

        if (location is not null && location.Any(char.IsControl))
        {
            return KnowledgeRedirectDecision.Deny(
                statusCode,
                requested,
                location,
                string.IsNullOrEmpty(location) ? null : location,
                new EclError(
                    KnowledgeErrorCodes.RedirectBlocked,
                    "重定向被拒绝：Location 头包含控制字符。",
                    requested));
        }

        var trimmedLocation = location?.Trim();
        if (string.IsNullOrEmpty(trimmedLocation))
        {
            return KnowledgeRedirectDecision.Deny(
                statusCode,
                requested,
                location,
                null,
                new EclError(
                    KnowledgeErrorCodes.RedirectBlocked,
                    "重定向被拒绝：响应缺少 Location 头。",
                    $"HTTP {statusCode} 来自 {requested}。"));
        }

        var baseValidation = ValidateNavigation(requested);
        if (!baseValidation.IsSuccess)
        {
            return KnowledgeRedirectDecision.Deny(
                statusCode,
                requested,
                trimmedLocation,
                trimmedLocation,
                new EclError(
                    KnowledgeErrorCodes.RedirectBlocked,
                    "重定向被拒绝：原始请求地址未通过策略校验。",
                    $"{baseValidation.Error!.Code}: {baseValidation.Error.Message}"));
        }

        if (!Uri.TryCreate(requested, UriKind.Absolute, out var baseUri) ||
            !Uri.TryCreate(baseUri, trimmedLocation, out var targetUri))
        {
            return KnowledgeRedirectDecision.Deny(
                statusCode,
                requested,
                trimmedLocation,
                trimmedLocation,
                new EclError(
                    KnowledgeErrorCodes.RedirectBlocked,
                    "重定向被拒绝：Location 无法安全解析。",
                    $"原始请求：{requested}"));
        }

        // 保留解析后的完整目标地址，即使它使用非 HTTPS 协议或外部域名；
        // 错误页需要展示真实目标，而不是因为拒绝而丢失信息。
        var rejectedTarget = targetUri.AbsoluteUri;
        var targetValidation = ValidateParsedUri(targetUri, trimmedLocation);
        if (!targetValidation.IsSuccess)
        {
            var cause = targetValidation.Error!;
            return KnowledgeRedirectDecision.Deny(
                statusCode,
                requested,
                trimmedLocation,
                rejectedTarget,
                new EclError(
                    KnowledgeErrorCodes.RedirectBlocked,
                    "重定向被拒绝：目标地址未通过 ECL 站点与协议校验。",
                    $"目标 {rejectedTarget} 未通过校验（{cause.Code}）：{cause.Message}"));
        }

        return KnowledgeRedirectDecision.Allow(statusCode, requested, location, targetValidation.Value!);
    }

    /// <summary>
    /// 把原始输入解析为绝对 URI；这里只做语法处理，协议和主机校验随后进行。
    /// </summary>
    private static bool TryCreateAbsoluteUri(string? url, out Uri uri, out EclError? error)
    {
        uri = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = InvalidUrl("地址为空或只包含空白字符。");
            return false;
        }

        // CR/LF、制表符等控制字符不允许出现在网络地址中，避免日志注入和
        // 不同解析器之间的歧义；首尾空白也不接受，不做“猜测式”修剪。
        if (url.Any(char.IsControl))
        {
            error = InvalidUrl("地址包含控制字符。", url);
            return false;
        }

        if (!string.Equals(url, url.Trim(), StringComparison.Ordinal))
        {
            error = InvalidUrl("地址首尾包含空白字符。", url);
            return false;
        }

        // 单斜杠开头的站内相对路径不是绝对 URL。部分平台的 Uri 实现会将
        // /path 解释为 file: URI，随后错误地报告为“不支持的协议”；在 URI
        // 解析前明确归类为 URL 语法错误。双斜杠协议相对地址仍交由协议校验拒绝。
        if (url.StartsWith("/", StringComparison.Ordinal) &&
            !url.StartsWith("//", StringComparison.Ordinal))
        {
            error = InvalidUrl("不接受相对 URL；必须提供绝对 HTTPS 地址。", url);
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = InvalidUrl("地址不是可解析的绝对 URL。", url);
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>
    /// 对已解析 URI 执行完整策略：HTTPS、无 userinfo、DNS 主机名、白名单域名树。
    /// </summary>
    private static Result<KnowledgeTarget> ValidateParsedUri(Uri uri, string displayUrl)
    {
        if (!string.Equals(uri.Scheme, HttpsScheme, StringComparison.OrdinalIgnoreCase))
        {
            return Result<KnowledgeTarget>.Fail(
                KnowledgeErrorCodes.SchemeUnsupported,
                "协议不受支持：ECL 知识浏览器仅允许 HTTPS。",
                $"scheme={uri.Scheme}");
        }

        if (HasUserInfo(uri))
        {
            // userinfo 不参与 hostname 归属判断，也不允许通过“可信主机@外部主机”的
            // 形式伪装目标；直接视为非法地址。
            return Result<KnowledgeTarget>.Fail(
                KnowledgeErrorCodes.UrlInvalid,
                "地址包含用户信息段（userinfo），已拒绝。",
                displayUrl);
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return Result<KnowledgeTarget>.Fail(
                KnowledgeErrorCodes.SiteNotAllowed,
                "站点不在允许范围：IP 地址不属于允许的域名树。",
                uri.Host);
        }

        if (uri.HostNameType == UriHostNameType.Unknown || string.IsNullOrEmpty(uri.Host))
        {
            return Result<KnowledgeTarget>.Fail(
                KnowledgeErrorCodes.UrlInvalid,
                "地址缺少可规范化的主机名。",
                displayUrl);
        }

        if (!TryNormalizeHost(uri.Host, out var normalizedHost, out var hostError))
        {
            return Result<KnowledgeTarget>.Fail(hostError!);
        }

        if (!IsAllowedNormalizedHost(normalizedHost))
        {
            return Result<KnowledgeTarget>.Fail(
                KnowledgeErrorCodes.SiteNotAllowed,
                "站点不在允许范围：仅允许 zh.minecraft.wiki 与 mcmod.cn 域名树。",
                normalizedHost);
        }

        if (uri.Port is <= 0 or > 65535)
        {
            return Result<KnowledgeTarget>.Fail(
                KnowledgeErrorCodes.UrlInvalid,
                "地址端口无效。",
                displayUrl);
        }

        return Result<KnowledgeTarget>.Ok(new KnowledgeTarget(uri, normalizedHost));
    }

    /// <summary>
    /// 判断原始 URL 的 authority 是否出现 userinfo 标记，包括空 userinfo 的
    /// <c>https://@host</c> 形式（<see cref="Uri.UserInfo"/> 为空但标记存在）。
    /// </summary>
    private static bool HasUserInfo(Uri uri)
    {
        if (uri.UserInfo.Length > 0)
        {
            return true;
        }

        var original = uri.OriginalString;
        var schemeSeparator = original.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return false;
        }

        var start = schemeSeparator + 3;
        var end = original.IndexOfAny(['/', '\\', '?', '#'], start);
        if (end < 0)
        {
            end = original.Length;
        }

        return original.AsSpan(start, end - start).Contains('@');
    }

    /// <summary>
    /// 规范化主机名：拒绝空值/空白/非法字符，处理大小写、尾随根点与 IDN。
    /// </summary>
    private static bool TryNormalizeHost(string? rawHost, out string normalizedHost, out EclError? error)
    {
        normalizedHost = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(rawHost))
        {
            error = InvalidUrl("主机名为空。");
            return false;
        }

        foreach (var character in rawHost)
        {
            if (char.IsWhiteSpace(character) ||
                char.IsControl(character) ||
                character is '/' or '\\' or '@' or ':' or '?' or '#' or '%')
            {
                error = InvalidUrl("主机名包含不允许的字符。", rawHost);
                return false;
            }
        }

        var candidate = rawHost;

        // DNS 根点（FQDN 结尾的单个 "."）不改变主机归属；多于一个尾随点意味着
        // 空标签，属于无法安全规范化的输入，直接拒绝。
        if (candidate.EndsWith('.'))
        {
            candidate = candidate[..^1];
        }

        if (candidate.Length == 0 ||
            candidate[0] == '.' ||
            candidate.EndsWith('.') ||
            candidate.Contains("..", StringComparison.Ordinal))
        {
            error = InvalidUrl("主机名的 DNS 标签边界无效。", rawHost);
            return false;
        }

        string asciiHost;
        try
        {
            // UseStd3AsciiRules 会拒绝下划线、空格等非 DNS 字符；AllowUnassigned=false
            // 避免把未分配的 Unicode 码点当作可信主机。
            var idnMapping = new IdnMapping
            {
                AllowUnassigned = false,
                UseStd3AsciiRules = true,
            };
            asciiHost = idnMapping.GetAscii(candidate);
        }
        catch (ArgumentException)
        {
            error = InvalidUrl("主机名无法按 IDN 规则规范化。", rawHost);
            return false;
        }

        asciiHost = asciiHost.ToLowerInvariant();

        // 兼容 Unicode 句点（例如 U+3002）被 IDN 映射为标准 "." 后留下的根点。
        if (asciiHost.EndsWith('.'))
        {
            asciiHost = asciiHost[..^1];
        }

        if (!IsValidDnsName(asciiHost))
        {
            error = InvalidUrl("主机名不是合法的 DNS 名称。", rawHost);
            return false;
        }

        normalizedHost = asciiHost;
        return true;
    }

    /// <summary>按 DNS 标签规则校验 ASCII 主机名（长度、空标签、连字符边界、字符集）。</summary>
    private static bool IsValidDnsName(string host)
    {
        if (host.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63)
            {
                return false;
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var character in label)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 白名单判定：使用 DNS 标签边界，逻辑等价于“等于域名或以 .域名 结尾”，
    /// 不使用字符串 Contains，避免 <c>evil-zh.minecraft.wiki</c> 之类被误放行。
    /// </summary>
    private static bool IsAllowedNormalizedHost(string normalizedHost)
    {
        foreach (var domain in DomainTrees)
        {
            if (normalizedHost.Equals(domain, StringComparison.Ordinal) ||
                normalizedHost.EndsWith($".{domain}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRedirectStatusCode(int statusCode) =>
        statusCode is 301 or 302 or 303 or 307 or 308;

    private static EclError InvalidUrl(string message, string? detail = null) =>
        new(KnowledgeErrorCodes.UrlInvalid, message, detail);
}
