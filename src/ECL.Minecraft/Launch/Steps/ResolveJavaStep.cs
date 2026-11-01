// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Java;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class ResolveJavaStep(IJavaSelectionService selection, ILocalizer localizer) : ILaunchStep
{
    public string Id => "java";

    public int Order => 200;

    public async Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        var result = await selection
            .ResolveAsync(context.Instance, context.Version, cancellationToken)
            .ConfigureAwait(false);

        context.MemoryMb = Math.Clamp(result.MemoryMb, 1024, 65536);
        context.JavaPlan = result.Plan;

        // 32 位 Java 和冲突要求是硬禁止，任何用户确认都不能绕过。
        if (result.IsBlocked)
        {
            context.Java = result.Runtime;
            context.JavaExecutable = result.Runtime?.ExecutablePath ?? "java";
            var blockedMessage = result.Compatibility?.Status switch
            {
                JavaCompatibilityStatus.UnsupportedArchitecture => localizer.Get(
                    "instances.java.unsupported32",
                    result.Runtime?.DisplayName ?? "Java"),
                JavaCompatibilityStatus.RequirementsConflict => localizer.Get(
                    "launch.java.conflict",
                    result.Decision?.Requirements?.Explain() ?? string.Empty),
                _ => result.BlockReason ?? localizer.Get("launch.java.blocked"),
            };
            return new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                AppendWarnings(blockedMessage, result.Warnings));
        }

        if (result.Runtime is null)
        {
            context.JavaExecutable = "java";
            var message = result.Decision?.Failure switch
            {
                JavaSelectionFailure.RequirementsConflict => localizer.Get(
                    "launch.java.conflict",
                    result.Decision?.Requirements?.Explain() ?? string.Empty),
                JavaSelectionFailure.NoCompatibleCandidate => localizer.Get(
                    "instances.java.noCompatibleReasons",
                    result.Requirement?.Describe() ?? "Java",
                    DescribeRejectedCandidates(result.Decision)),
                _ => localizer.Get(context.DryRun ? "launch.java.missing.preview" : "launch.java.missing"),
            };

            return new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                AppendWarnings(message, result.Warnings));
        }

        context.Java = result.Runtime;
        context.JavaExecutable = result.Runtime.ExecutablePath;

        if (result.Compatibility is { Status: JavaCompatibilityStatus.Unknown })
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Warning,
                AppendWarnings(
                    localizer.Get("launch.java.unknownVersion", result.Runtime.DisplayName),
                    result.Warnings));
        }

        if (result.Compatibility is { Status: JavaCompatibilityStatus.UnknownRequirement })
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Warning,
                AppendWarnings(
                    localizer.Get("launch.java.unknownRequirement", result.Runtime.DisplayName),
                    result.Warnings));
        }

        if (result.Compatibility is { IsCompatible: false })
        {
            var required = result.Requirement?.Describe() ?? "Java";
            var message = localizer.Get(
                "launch.java.incompatible",
                result.Runtime.Version?.Raw ?? result.Runtime.DisplayName,
                required);

            // 自动选择绝不能绕过兼容性要求；实例显式选择则允许用户承担风险并继续启动。
            if (result.IsExplicitOverride)
            {
                if (result.IsCompatibilityWarningAcknowledged)
                {
                    return new LaunchStepResult(
                        Id,
                        LaunchStepStatus.Warning,
                        AppendWarnings(
                            localizer.Get(
                                "launch.java.foundWithRisk",
                                result.Runtime.DisplayName,
                                context.MemoryMb,
                                required),
                            result.Warnings));
                }

                if (context.DryRun)
                {
                    return new LaunchStepResult(
                        Id,
                        LaunchStepStatus.Warning,
                        AppendWarnings(message, result.Warnings));
                }

                // CLI 没有弹窗；必须通过 --accept-java-risk 显式接受，或在 UI 中确认后复用持久化记录。
                if (context.AcceptJavaRisk && result.CompatibilityAcknowledgementToken is not null)
                {
                    context.Settings.TrySetRaw(
                        JavaRiskIdentity.SettingsKey,
                        result.CompatibilityAcknowledgementToken,
                        null,
                        out _);
                    return new LaunchStepResult(
                        Id,
                        LaunchStepStatus.Warning,
                        AppendWarnings(
                            localizer.Get(
                                "launch.java.foundWithRisk",
                                result.Runtime.DisplayName,
                                context.MemoryMb,
                                required),
                            result.Warnings));
                }

                return new LaunchStepResult(
                    Id,
                    LaunchStepStatus.Failed,
                    AppendWarnings(
                        localizer.Get("launch.java.riskNotAccepted", required),
                        result.Warnings));
            }

            return new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                AppendWarnings(message, result.Warnings));
        }

        var foundMessage = localizer.Get("launch.java.found", result.Runtime.DisplayName, context.MemoryMb);
        return new LaunchStepResult(
            Id,
            result.Warnings.Count > 0 ? LaunchStepStatus.Warning : LaunchStepStatus.Success,
            AppendWarnings(foundMessage, result.Warnings));
    }

    private string DescribeRejectedCandidates(JavaSelectionDecision? decision)
    {
        if (decision is null || decision.RejectedCandidates.Count == 0)
        {
            return localizer.Get("instances.java.notFound");
        }

        return string.Join(
            "，",
            decision.RejectedCandidates
                .GroupBy(candidate => candidate.Status)
                .Select(group => $"{DescribeCandidateStatus(group.Key)} ×{group.Count()}"));
    }

    private string DescribeCandidateStatus(JavaCandidateStatus status) => localizer.Get(status switch
    {
        JavaCandidateStatus.TooOld => "instances.java.candidate.tooOld",
        JavaCandidateStatus.TooNew => "instances.java.candidate.tooNew",
        JavaCandidateStatus.Unsupported32Bit => "instances.java.candidate.unsupported32Bit",
        JavaCandidateStatus.UnknownVersion => "instances.java.candidate.unknownVersion",
        JavaCandidateStatus.CompatibleButNotPreferred => "instances.java.candidate.compatibleButNotPreferred",
        JavaCandidateStatus.RequirementsConflict => "instances.java.candidate.requirementsConflict",
        _ => "instances.java.candidate.unknownVersion",
    });

    private static string AppendWarnings(string message, IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? message : $"{message} {string.Join(' ', warnings)}";
}
