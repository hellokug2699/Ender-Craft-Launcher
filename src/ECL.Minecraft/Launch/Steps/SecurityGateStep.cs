// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Minecraft.Security;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch.Steps;

/// <summary>
/// 启动前安全检查门（ADR 0043 §6）。必须在创建 Minecraft 进程前完成：
/// 有需确认的风险时暂停启动；用户明确接受后记录风险并继续，不把风险结论改成 Clean。
/// </summary>
internal sealed class SecurityGateStep(
    IThreatProtectionService security,
    ILocalizer localizer,
    ILogger<SecurityGateStep> logger) : ILaunchStep
{
    public string Id => "security";

    public int Order => 450;

    public async Task<LaunchStepResult> ExecuteAsync(
        LaunchContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.DryRun)
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Skipped,
                localizer.Get("launch.security.dryRun"));
        }

        var decision = await security.EvaluateLaunchAsync(
                context.Instance.Id,
                context.Command?.Arguments,
                cancellationToken)
            .ConfigureAwait(false);

        if (!decision.IsSuccess)
        {
            logger.LogWarning(
                "实例 {InstanceId} 的启动前安全检查失败：{Error}",
                context.Instance.Id,
                decision.Error);
            // 安全引擎不可用不等于威胁放行，但也不得阻断普通启动；明确报告覆盖缺口。
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Warning,
                $"安全中心不可用（{decision.Error?.Message}）；本次未完成安全检查。");
        }

        var value = decision.Value!;
        if (value.Allowed)
        {
            return new LaunchStepResult(Id, LaunchStepStatus.Success, value.Message);
        }

        if (!value.RequiresAcknowledgement)
        {
            return new LaunchStepResult(Id, LaunchStepStatus.Warning, value.Message);
        }

        if (!context.AcceptSecurityRisk)
        {
            var summary = string.Join(
                "；",
                value.Findings.Take(5).Select(finding =>
                    $"{finding.RelativePath}（{finding.Level}）"));
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                $"{value.Message} {summary}。请处理风险，或在明确接受后使用 --accept-security-risk 继续。");
        }

        var accepted = await security.AcceptRiskAsync(context.Instance.Id, value.Findings, cancellationToken)
            .ConfigureAwait(false);
        if (!accepted.IsSuccess)
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                $"无法记录风险接受决定：{accepted.Error?.Message}");
        }

        logger.LogWarning(
            "实例 {InstanceId} 的用户接受 {Count} 项安全风险并继续启动。",
            context.Instance.Id,
            value.Findings.Count);
        return new LaunchStepResult(
            Id,
            LaunchStepStatus.Warning,
            $"已记录用户对 {value.Findings.Count} 项风险的接受决定，继续启动；风险等级不会被改为 Clean。");
    }
}
