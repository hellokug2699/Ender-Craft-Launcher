// SPDX-License-Identifier: GPL-3.0-or-later

namespace ECL.Knowledge;

/// <summary>
/// 已通过 <see cref="KnowledgeSitePolicy"/> 校验的导航目标。
/// </summary>
/// <remarks>
/// 只有策略内部在校验成功后才会创建该类型。调用方不应自行构造“已允许”目标，
/// 否则会绕过 HTTPS、主机名规范化和域名树边界检查。
/// </remarks>
public sealed record KnowledgeTarget
{
    internal KnowledgeTarget(Uri uri, string host)
    {
        Uri = uri;
        Host = host;
    }

    /// <summary>解析后的绝对 URI。</summary>
    public Uri Uri { get; }

    /// <summary>规范化后的 ASCII 主机名（IDN 已转为 punycode、大小写已统一、已去除根点）。</summary>
    public string Host { get; }

    /// <summary>目标端口。端口不参与域名归属判断，因此允许显式端口。</summary>
    public int Port => Uri.Port;

    /// <summary>可直接交给 WebView 导航的完整绝对地址。</summary>
    public string AbsoluteUrl => Uri.AbsoluteUri;
}
