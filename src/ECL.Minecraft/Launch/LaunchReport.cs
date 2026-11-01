// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Java;

namespace Ecl.Minecraft.Launch;

public enum LaunchStepStatus
{
    Success,
    Warning,
    Skipped,
    Failed,
}

public sealed record LaunchStepResult(string StepId, LaunchStepStatus Status, string Message)
{
    public string TitleKey => $"launch.step.{StepId}";

    public TimeSpan Duration { get; init; }
}

public sealed record LaunchReport(
    bool Success,
    bool DryRun,
    string? CommandLine,
    int? ProcessId,
    IReadOnlyList<LaunchStepResult> Steps,
    TimeSpan Duration,
    JavaLaunchPlan? JavaPlan = null);
