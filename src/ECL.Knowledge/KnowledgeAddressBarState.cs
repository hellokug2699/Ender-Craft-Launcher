// SPDX-License-Identifier: GPL-3.0-or-later

namespace ECL.Knowledge;

/// <summary>
/// 受限知识浏览器地址栏的只读展示契约。
/// </summary>
/// <remarks>
/// 地址栏只展示当前 URL 并提供完整地址的复制文本，不提供编辑、粘贴、搜索、
/// 拖放或按 Enter 导航的入口。该类型没有导航方法，也不会接受任意输入后提交；
/// 页面导航只能通过允许站点内的链接和受控历史操作触发。
/// </remarks>
public sealed record KnowledgeAddressBarState
{
    /// <summary>地址栏视觉截断长度；仅影响展示，不影响 <see cref="Url"/> 与 <see cref="CopyText"/>。</summary>
    public const int MaxDisplayLength = 180;

    private KnowledgeAddressBarState(string url)
    {
        Url = url;
        DisplayUrl = url.Length <= MaxDisplayLength
            ? url
            : string.Concat(url.AsSpan(0, MaxDisplayLength), "…");
    }

    /// <summary>当前页面的完整 URL。</summary>
    public string Url { get; }

    /// <summary>用于界面展示的 URL；过长时可能被视觉截断。</summary>
    public string DisplayUrl { get; }

    /// <summary>地址栏不可编辑。</summary>
    public bool CanEdit => false;

    /// <summary>允许复制完整 URL。</summary>
    public bool CanCopy => true;

    /// <summary>复制操作应取得的完整地址，不受视觉截断影响。</summary>
    public string CopyText => Url;

    /// <summary>为已允许的导航目标创建只读地址栏状态。</summary>
    public static KnowledgeAddressBarState FromTarget(KnowledgeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new KnowledgeAddressBarState(target.AbsoluteUrl);
    }

    /// <summary>
    /// 为展示用途创建地址栏状态；调用方应传入当前受控页面地址。
    /// 该工厂方法不代表地址已通过白名单校验，也不会触发导航。
    /// </summary>
    public static KnowledgeAddressBarState FromUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return new KnowledgeAddressBarState(url);
    }
}
