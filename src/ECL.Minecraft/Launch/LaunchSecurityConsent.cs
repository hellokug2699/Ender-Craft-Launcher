// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Security;

namespace Ecl.Minecraft.Launch;

/// <summary>
/// 启动前安全门控需要用户作出的风险决定（ADR 0043 §6）。领域层只描述“本次启动被哪些
/// 尚未接受的风险挡住”，不弹窗、也不推断用户意图；交互层（GUI 风险确认弹窗，或 CLI 的
/// 显式标志）负责给出决定。
/// </summary>
public sealed record SecurityRiskConsentRequest(
    string InstanceId,
    string? InstanceName,
    IReadOnlyList<ThreatFinding> Findings,
    string Summary);

/// <summary>
/// 把风险披露呈现给用户并返回是否接受。取消、关闭弹窗或交互宿主不可用都必须返回
/// <c>false</c>：没有用户的明确选择时不得放行启动。
/// </summary>
public delegate Task<bool> SecurityRiskConsentHandler(
    SecurityRiskConsentRequest request,
    CancellationToken cancellationToken);
