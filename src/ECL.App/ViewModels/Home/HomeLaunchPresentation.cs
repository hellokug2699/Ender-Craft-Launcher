// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.ViewModels.Home;

/// <summary>主页启动卡中单个步骤的呈现状态（home.md §3.2）。</summary>
public enum HomeLaunchStepState
{
    /// <summary>链路中存在该步骤，但本次启动尚未到达。</summary>
    Pending,

    Running,

    Success,

    Warning,

    Skipped,

    Failed,

    /// <summary>本次启动在到达该步骤前已结束（失败或中断）。</summary>
    NotRun,
}

/// <summary>
/// 启动卡进度呈现的纯计算部分（B5）。抽成静态类便于测试锁定两条硬约束：
/// 1. 没有完成信号时估量进度永远不到 100%，也不显示百分比数字；
/// 2. 只按“耗时较长 + 一段时间没有新输出”提示可能卡住，**不**把单纯耗时长判定为卡死。
/// </summary>
public static class HomeLaunchPresentation
{
    /// <summary>
    /// 步骤估量时长的名义参考值（秒）。它只是进度条走势的平滑依据，不是真实测量，
    /// 因此不显示成时间估计或百分比；来源为各步骤的实际工作类型（文件校验 > 解压 > 下载）。
    /// </summary>
    private static readonly Dictionary<string, TimeSpan> NominalEstimates = new(StringComparer.Ordinal)
    {
        ["instance"] = TimeSpan.FromSeconds(1),
        ["account"] = TimeSpan.FromSeconds(2),
        ["validate"] = TimeSpan.FromSeconds(2),
        ["java"] = TimeSpan.FromSeconds(3),
        ["version"] = TimeSpan.FromSeconds(3),
        ["files"] = TimeSpan.FromSeconds(10),
        ["natives"] = TimeSpan.FromSeconds(8),
        ["eula"] = TimeSpan.FromSeconds(1),
        ["authlib-injector"] = TimeSpan.FromSeconds(6),
        ["command"] = TimeSpan.FromSeconds(1),
        ["security"] = TimeSpan.FromSeconds(2),
        ["start"] = TimeSpan.FromSeconds(5),
    };

    private static readonly TimeSpan DefaultEstimate = TimeSpan.FromSeconds(4);

    /// <summary>运行中步骤的进度上限：没有完成信号之前不得显示已完成的进度。</summary>
    public const double RunningCap = 0.92;

    /// <summary>步骤运行超过该时长后才考虑提示“可能卡住”。</summary>
    public static readonly TimeSpan LongRunningAfter = TimeSpan.FromSeconds(30);

    /// <summary>同时要求该时长内没有新的 ECL 输出，才认为有卡住的证据。</summary>
    public static readonly TimeSpan SilentOutputAfter = TimeSpan.FromSeconds(12);

    public static string TitleKey(string stepId) => $"launch.step.{stepId}";

    public static TimeSpan NominalEstimate(string stepId) =>
        NominalEstimates.TryGetValue(stepId, out var estimate) ? estimate : DefaultEstimate;

    /// <summary>运行中步骤的估量进度（0..<see cref="RunningCap"/>），随时间单调递增并逐渐收敛。</summary>
    public static double RunningFraction(string stepId, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        var ratio = elapsed.TotalSeconds / Math.Max(0.001, NominalEstimate(stepId).TotalSeconds);
        var fraction = RunningCap * (1 - Math.Exp(-2.5 * ratio));
        return Math.Clamp(fraction, 0, RunningCap);
    }

    /// <summary>总进度 = 已完成步骤 + 当前步骤的估量进度；失败时由调用方停在对应位置。</summary>
    public static double TotalProgress(int totalSteps, int completedSteps, double runningFraction)
    {
        if (totalSteps <= 0)
        {
            return 0;
        }

        var completed = Math.Clamp(completedSteps, 0, totalSteps);
        var running = Math.Clamp(runningFraction, 0, 1);
        return Math.Clamp((completed + running) / totalSteps, 0, 1);
    }

    /// <summary>
    /// 是否具有“可能卡住”的可观测证据。必须同时满足：步骤已运行较久，且一段时间没有新的 ECL 输出。
    /// 只有其中一条时不提示，避免把正常的长耗时步骤说成卡死。
    /// </summary>
    public static bool ShouldReportLongRunning(TimeSpan elapsed, TimeSpan sinceLastOutput) =>
        elapsed >= LongRunningAfter && sinceLastOutput >= SilentOutputAfter;

    /// <summary>步骤状态文案的本地化键。</summary>
    public static string StateKey(HomeLaunchStepState state) => state switch
    {
        HomeLaunchStepState.Running => "home.launch.state.running",
        HomeLaunchStepState.Success => "launch.status.success",
        HomeLaunchStepState.Warning => "launch.status.warning",
        HomeLaunchStepState.Skipped => "launch.status.skipped",
        HomeLaunchStepState.Failed => "launch.status.failed",
        HomeLaunchStepState.NotRun => "home.launch.state.notRun",
        _ => "home.launch.state.pending",
    };
}
