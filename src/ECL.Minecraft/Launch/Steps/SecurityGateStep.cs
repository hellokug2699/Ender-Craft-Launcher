// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Minecraft.Security;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch.Steps;

/// <summary>
/// 启动前安全检查门（ADR 0043 §6）。必须在创建 Minecraft 进程前完成：
/// 有需确认的风险时暂停启动，并把披露与选择交给交互层——
/// GUI 通过 <see cref="LaunchContext.ConfirmSecurityRisk"/> 弹出风险确认，
/// CLI 通过 <c>--accept-security-risk</c> 显式接受；用户明确接受后记录风险并继续，
/// 不把风险结论改成 Clean，也不在用户未选择时静默放行。
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

        // CLI／自动化已经显式接受：直接记录，不再弹出第二次确认。
        if (context.AcceptSecurityRisk)
        {
            return await AcceptAndContinueAsync(context, value, cancellationToken).ConfigureAwait(false);
        }

        if (context.ConfirmSecurityRisk is not { } confirm)
        {
            // 没有交互通道（CLI、自动化）：必须告诉用户显式标志，而不是让启动无声消失。
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                $"{value.Message} {Summarize(value.Findings)} {localizer.Get("launch.security.acceptHint")}");
        }

        bool accepted;
        try
        {
            accepted = await confirm(
                    new SecurityRiskConsentRequest(
                        context.Instance.Id,
                        context.Instance.Name,
                        value.Findings,
                        value.Message),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 风险确认失败不得被解释成“用户接受”：按未接受处理并说明原因。
            logger.LogWarning(exception, "实例 {InstanceId} 的风险确认未能完成。", context.Instance.Id);
            accepted = false;
        }

        if (!accepted)
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.security.declined", value.Findings.Count));
        }

        return await AcceptAndContinueAsync(context, value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LaunchStepResult> AcceptAndContinueAsync(
        LaunchContext context,
        LaunchSecurityDecision value,
        CancellationToken cancellationToken)
    {
        var accepted = await security.AcceptRiskAsync(context.Instance.Id, value.Findings, cancellationToken)
            .ConfigureAwait(false);
        if (!accepted.IsSuccess)
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.security.acceptFailed", accepted.Error?.Message ?? string.Empty));
        }

        logger.LogWarning(
            "实例 {InstanceId} 的用户接受 {Count} 项安全风险并继续启动。",
            context.Instance.Id,
            value.Findings.Count);
        return new LaunchStepResult(
            Id,
            LaunchStepStatus.Warning,
            localizer.Get("launch.security.accepted", value.Findings.Count));
    }

    private static string Summarize(IReadOnlyList<ThreatFinding> findings) =>
        string.Join(
            "；",
            findings.Take(5).Select(finding => $"{finding.RelativePath}（{finding.Level}）"));
}
