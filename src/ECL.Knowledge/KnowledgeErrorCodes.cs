// SPDX-License-Identifier: GPL-3.0-or-later

namespace ECL.Knowledge;

/// <summary>
/// 知识浏览器策略对外的稳定错误代码。
/// </summary>
/// <remarks>
/// 这些代码是宿主、界面和测试之间的契约：文案可以调整，但代码不得随意变化，
/// 否则调用方无法可靠地区分“站点不允许”“协议不支持”和“重定向被拒绝”。
/// </remarks>
public static class KnowledgeErrorCodes
{
    /// <summary>目标主机不属于允许的域名树。</summary>
    public const string SiteNotAllowed = "knowledge.site_not_allowed";

    /// <summary>目标协议不是 HTTPS（包括 HTTP；策略不会自动升级后放行）。</summary>
    public const string SchemeUnsupported = "knowledge.scheme_unsupported";

    /// <summary>3xx 重定向未通过校验，ECL 在跟随之前拒绝。</summary>
    public const string RedirectBlocked = "knowledge.redirect_blocked";

    /// <summary>地址为空、无法解析或包含用户信息等无法安全规范化的内容。</summary>
    public const string UrlInvalid = "knowledge.url_invalid";
}
