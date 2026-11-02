// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace ECL.Knowledge;

/// <summary>
/// 一次顶层导航或子资源请求的策略判定结果。
/// </summary>
/// <remarks>
/// 判定成功时 <see cref="Outcome"/> 携带规范化后的 <see cref="KnowledgeTarget"/>；
/// 判定失败时携带稳定的 <see cref="KnowledgeErrorCodes"/> 错误代码。原始输入的 URL
/// 始终保留在 <see cref="RequestedUrl"/> / <see cref="RejectedTarget"/> 中，供本地
/// 错误页原样（转义后）展示，而不是依赖最终页面地址。
/// </remarks>
public sealed record KnowledgeNavigationDecision
{
    /// <summary>本次尝试访问的原始地址（未经规范化，仅用于展示和审计）。</summary>
    public required string RequestedUrl { get; init; }

    /// <summary>策略结果；成功值为已校验的目标，失败值为稳定错误。</summary>
    public required Result<KnowledgeTarget> Outcome { get; init; }

    /// <summary>被拒绝的目标地址（如存在），用于本地错误页展示。</summary>
    public string? RejectedTarget { get; init; }

    /// <summary>是否允许该请求。</summary>
    public bool IsAllowed => Outcome.IsSuccess && Outcome.Value is not null;

    /// <summary>允许时的规范化目标；拒绝时为 <see langword="null"/>。</summary>
    public KnowledgeTarget? Target => Outcome.Value;

    /// <summary>拒绝原因；允许时为 <see langword="null"/>。</summary>
    public EclError? Error => Outcome.Error;

    internal static KnowledgeNavigationDecision Allow(string requestedUrl, KnowledgeTarget target) =>
        new()
        {
            RequestedUrl = requestedUrl,
            Outcome = Result<KnowledgeTarget>.Ok(target),
        };

    internal static KnowledgeNavigationDecision Deny(string requestedUrl, EclError error) =>
        new()
        {
            RequestedUrl = requestedUrl,
            Outcome = Result<KnowledgeTarget>.Fail(error),
            RejectedTarget = string.IsNullOrWhiteSpace(requestedUrl) ? null : requestedUrl,
        };
}
