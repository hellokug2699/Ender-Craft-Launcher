// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using Ecl.Core;

namespace ECL.Knowledge;

/// <summary>
/// 生成受限知识浏览器错误页所需的本地数据。
/// </summary>
public sealed record KnowledgeErrorPageRequest
{
    /// <summary>策略返回的稳定错误。</summary>
    public required EclError Error { get; init; }

    /// <summary>原始请求地址（不做任何信任处理，仅用于转义后展示）。</summary>
    public string? RequestedUrl { get; init; }

    /// <summary>被拒绝的目标地址（如存在）。</summary>
    public string? RejectedTarget { get; init; }

    /// <summary>真实 HTTP 3xx 状态码（如适用），用于展示例如“HTTP 302”。</summary>
    public int? RedirectStatusCode { get; init; }
}

/// <summary>
/// 由 ECL 本地生成的错误页。
/// </summary>
/// <remarks>
/// <para>
/// 页面不引用任何外部 http(s) 资源，不包含脚本、iframe、表单、自动跳转或
/// meta refresh；样式全部内联，并使用 CSP 进一步禁止网络加载。所有动态文本
/// （原始地址、被拒目标、错误消息与详情）都先截断到固定长度，再进行 HTML
/// 编码，因此 <c>&lt;script&gt;</c> 之类的输入只会显示为文本。
/// </para>
/// <para>
/// 错误页只负责展示，不执行导航，也不会自动打开系统浏览器。WebView 宿主缺失
/// 仍是本 ADR 的已知阻塞项：错误页可以在导航被策略拒绝后展示，但“在跟随前
/// 拦截 3xx”必须由宿主在网络层完成。
/// </para>
/// </remarks>
public static class KnowledgeErrorPage
{
    /// <summary>单个动态文本的最大展示长度；超出部分以“...”截断。</summary>
    public const int MaxDynamicTextLength = 512;

    private const string ProductName = "ECL · 受限 Minecraft 知识浏览器";

    /// <summary>由导航拒绝决定生成错误页。</summary>
    public static string Render(KnowledgeNavigationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.IsAllowed)
        {
            throw new ArgumentException("只有被拒绝的导航决策才能生成错误页。", nameof(decision));
        }

        return Render(new KnowledgeErrorPageRequest
        {
            Error = decision.Error!,
            RequestedUrl = decision.RequestedUrl,
            RejectedTarget = decision.RejectedTarget,
        });
    }

    /// <summary>由重定向拒绝决定生成错误页，并保留真实 3xx 状态码。</summary>
    public static string Render(KnowledgeRedirectDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.IsAllowed)
        {
            throw new ArgumentException("只有被拒绝的重定向决策才能生成错误页。", nameof(decision));
        }

        return Render(new KnowledgeErrorPageRequest
        {
            Error = decision.Error!,
            RequestedUrl = decision.RequestedUrl,
            RejectedTarget = decision.RejectedTarget,
            RedirectStatusCode = decision.StatusCode,
        });
    }

    /// <summary>渲染本地错误页 HTML。</summary>
    public static string Render(KnowledgeErrorPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kind = Classify(request);
        var title = TitleOf(kind);
        var description = DescriptionOf(kind);
        var showStatus = request.RedirectStatusCode is >= 300 and <= 399;
        var requestedUrl = string.IsNullOrWhiteSpace(request.RequestedUrl)
            ? "（未提供）"
            : Encoded(request.RequestedUrl);
        var rejectedTarget = string.IsNullOrWhiteSpace(request.RejectedTarget)
            ? "（无）"
            : Encoded(request.RejectedTarget);

        var html = new StringBuilder(4096);
        html.Append("<!DOCTYPE html>\n");
        html.Append("<html lang=\"zh-CN\">\n<head>\n");
        html.Append("<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">\n");
        html.Append("<title>");
        html.Append(Encoded(title));
        html.Append("</title>\n<style>\n");
        html.Append(":root { color-scheme: light dark; }\n");
        html.Append("body { margin: 0; padding: 2rem 1rem; font-family: system-ui, -apple-system, 'Segoe UI', sans-serif; line-height: 1.55; }\n");
        html.Append("main { max-width: 52rem; margin: 0 auto; }\n");
        html.Append(".product { opacity: .7; font-size: .85rem; letter-spacing: .02em; }\n");
        html.Append("h1 { font-size: 1.5rem; margin: .3rem 0 1rem; }\n");
        html.Append(".rejected { display: inline-block; padding: .2rem .6rem; border: 1px solid currentColor; border-radius: .35rem; font-weight: 600; }\n");
        html.Append(".summary { margin: 1rem 0 1.5rem; }\n");
        html.Append("dl { display: grid; grid-template-columns: minmax(7rem, max-content) 1fr; gap: .45rem 1rem; }\n");
        html.Append("dt { font-weight: 600; }\n");
        html.Append("dd { margin: 0; overflow-wrap: anywhere; }\n");
        html.Append("code { font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace; font-size: .9em; }\n");
        html.Append(".footnote { margin-top: 2rem; padding-top: 1rem; border-top: 1px solid color-mix(in srgb, currentColor 25%, transparent); font-size: .85rem; opacity: .8; }\n");
        html.Append("</style>\n</head>\n<body>\n<main>\n");
        html.Append("<p class=\"product\">");
        html.Append(Encoded(ProductName));
        html.Append("</p>\n<h1>");
        html.Append(Encoded(title));
        html.Append("</h1>\n<p class=\"rejected\">连接已被 ECL 拒绝</p>\n<p class=\"summary\">");
        html.Append(Encoded(description));
        html.Append("</p>\n<dl>\n");

        AppendRow(html, "错误类型", title);
        AppendRow(html, "错误代码", request.Error.Code);

        if (showStatus)
        {
            AppendRow(html, "重定向状态码", $"HTTP {request.RedirectStatusCode}");
        }

        AppendRow(html, "原始请求地址", requestedUrl, encode: false);
        AppendRow(html, "被拒绝的目标", rejectedTarget, encode: false);

        if (!string.IsNullOrWhiteSpace(request.Error.Message))
        {
            AppendRow(html, "原因", request.Error.Message);
        }

        if (!string.IsNullOrWhiteSpace(request.Error.Detail))
        {
            AppendRow(html, "详情", request.Error.Detail);
        }

        html.Append("</dl>\n");
        html.Append("<p class=\"footnote\">此页面由 ECL 本地生成，没有从被拦截的站点加载任何资源，也不会自动跳转。");
        html.Append("如果你认为该地址应当被允许，请通过 ECL 的正式反馈渠道报告；不要在此页面输入任何凭据。</p>\n");
        html.Append("</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static void AppendRow(StringBuilder html, string label, string value, bool encode = true)
    {
        html.Append("<dt>");
        html.Append(Encoded(label));
        html.Append("</dt><dd><code>");
        html.Append(encode ? Encoded(value) : value);
        html.Append("</code></dd>\n");
    }

    private static ErrorKind Classify(KnowledgeErrorPageRequest request)
    {
        if (request.RedirectStatusCode is >= 300 and <= 399 ||
            string.Equals(request.Error.Code, KnowledgeErrorCodes.RedirectBlocked, StringComparison.Ordinal))
        {
            return ErrorKind.RedirectBlocked;
        }

        return request.Error.Code switch
        {
            KnowledgeErrorCodes.SiteNotAllowed => ErrorKind.SiteNotAllowed,
            KnowledgeErrorCodes.SchemeUnsupported => ErrorKind.SchemeUnsupported,
            KnowledgeErrorCodes.UrlInvalid => ErrorKind.UrlInvalid,
            _ => ErrorKind.AccessDenied,
        };
    }

    private static string TitleOf(ErrorKind kind) => kind switch
    {
        ErrorKind.SiteNotAllowed => "站点不在允许范围",
        ErrorKind.SchemeUnsupported => "协议不受支持",
        ErrorKind.RedirectBlocked => "重定向被拒绝",
        ErrorKind.UrlInvalid => "地址无效",
        _ => "访问被拒绝",
    };

    private static string DescriptionOf(ErrorKind kind) => kind switch
    {
        ErrorKind.SiteNotAllowed =>
            "目标站点不属于 ECL 允许访问的 zh.minecraft.wiki 或 mcmod.cn 域名树，因此没有发起请求。",
        ErrorKind.SchemeUnsupported =>
            "ECL 知识浏览器只允许 HTTPS。HTTP、file:、data:、javascript:、ftp: 及自定义协议均被拒绝，且不会自动升级后放行。",
        ErrorKind.RedirectBlocked =>
            "服务器返回的重定向目标未通过 ECL 的白名单与协议校验，ECL 在跟随之前阻止了该跳转。",
        ErrorKind.UrlInvalid =>
            "该地址为空、无法安全解析，或包含用户信息段等不允许的内容，因此没有被导航。",
        _ =>
            "该请求未通过 ECL 知识浏览器的安全策略，因此没有发起网络请求。",
    };

    /// <summary>截断动态文本并做 HTML 编码，确保页面不会被输入注入。</summary>
    private static string Encoded(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var truncated = value.Length <= MaxDynamicTextLength
            ? value
            : string.Concat(value.AsSpan(0, MaxDynamicTextLength), "...");

        return WebUtility.HtmlEncode(truncated);
    }

    private enum ErrorKind
    {
        SiteNotAllowed,
        SchemeUnsupported,
        RedirectBlocked,
        UrlInvalid,
        AccessDenied,
    }
}
