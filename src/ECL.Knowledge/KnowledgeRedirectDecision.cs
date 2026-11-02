// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace ECL.Knowledge;

/// <summary>
/// 单个 HTTP 3xx 重定向响应的策略判定结果。
/// </summary>
/// <remarks>
/// 该类型只评估“这一跳”。调用方必须在收到 <see cref="IsAllowed"/> 后，才可以把
/// <see cref="KnowledgeTarget.AbsoluteUrl"/> 作为下一次请求地址；连续重定向必须
/// 逐跳重新调用策略，不能只看最终 URL。拒绝时 <see cref="StatusCode"/>、
/// <see cref="RequestedUrl"/> 和 <see cref="RejectedTarget"/> 均保留真实值，
/// 以便本地错误页显示原始 3xx，而不是笼统的网络错误。
/// </remarks>
public sealed record KnowledgeRedirectDecision
{
    /// <summary>真实的 HTTP 状态码（例如 301、302、303、307、308）。</summary>
    public required int StatusCode { get; init; }

    /// <summary>收到该重定向响应的原始请求地址。</summary>
    public required string RequestedUrl { get; init; }

    /// <summary>响应中的原始 Location 值（可能为相对地址或缺失）。</summary>
    public required string? Location { get; init; }

    /// <summary>策略结果；成功值为已校验的下一跳目标，失败值为稳定错误。</summary>
    public required Result<KnowledgeTarget> Outcome { get; init; }

    /// <summary>被拒绝的重定向目标（已尽量解析为绝对地址）；用于错误页展示。</summary>
    public string? RejectedTarget { get; init; }

    /// <summary>是否允许跟随该重定向。</summary>
    public bool IsAllowed => Outcome.IsSuccess && Outcome.Value is not null;

    /// <summary>允许时的下一跳规范化目标；拒绝时为 <see langword="null"/>。</summary>
    public KnowledgeTarget? Target => Outcome.Value;

    /// <summary>拒绝原因；允许时为 <see langword="null"/>。</summary>
    public EclError? Error => Outcome.Error;

    internal static KnowledgeRedirectDecision Allow(
        int statusCode,
        string requestedUrl,
        string? location,
        KnowledgeTarget target) =>
        new()
        {
            StatusCode = statusCode,
            RequestedUrl = requestedUrl,
            Location = location,
            Outcome = Result<KnowledgeTarget>.Ok(target),
        };

    internal static KnowledgeRedirectDecision Deny(
        int statusCode,
        string requestedUrl,
        string? location,
        string? rejectedTarget,
        EclError error) =>
        new()
        {
            StatusCode = statusCode,
            RequestedUrl = requestedUrl,
            Location = location,
            Outcome = Result<KnowledgeTarget>.Fail(error),
            RejectedTarget = rejectedTarget,
        };
}
