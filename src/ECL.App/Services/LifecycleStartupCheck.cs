// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.Core.Localization;
using Ecl.Minecraft.Engine.Lifecycle;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Services;

/// <summary>
/// 启动自检（F-03 §5.8）：读取生命周期 journal、隔离区、退休区与已分离数据，
/// 只<b>报告</b>上次未完成/需要恢复的实例操作，不自动继续或清理任何内容。
///
/// 设计约束：
/// - 检查在受管理的后台工作中执行，失败/不可读只记警告，不阻塞启动；
/// - 有需要人工处理的状态时才提示一次，提示里不包含本地绝对路径；
/// - 恢复动作仍只能由用户通过 CLI <c>ecl instance lifecycle</c> 或后续诊断入口显式触发。
/// </summary>
public sealed class LifecycleStartupCheck(
    IInstanceLifecycleRecoveryService recovery,
    IDialogEngine dialogs,
    ILocalizer localizer,
    ILogger<LifecycleStartupCheck> logger)
{
    public async Task<InstanceLifecycleRecoveryReport?> RunAsync(
        CancellationToken cancellationToken = default)
    {
        InstanceLifecycleRecoveryReport report;
        try
        {
            var inspected = await recovery.InspectAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!inspected.IsSuccess || inspected.Value is null)
            {
                logger.LogWarning(
                    "启动自检无法读取生命周期状态：{Error}",
                    inspected.Error?.Message ?? "未知错误");
                return null;
            }

            report = inspected.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "启动自检读取生命周期状态时失败；不阻塞启动。");
            return null;
        }

        var attention = report.NeedsRecoveryCount + report.ResumableCount +
                        report.CleanableSourceCount + report.OrphanCount;
        if (attention == 0)
        {
            logger.LogInformation(
                "启动自检：没有需要人工处理的生命周期状态（journal {Count} 条）。",
                report.Entries.Count);
            return report;
        }

        logger.LogWarning(
            "启动自检发现待处理的生命周期状态：需要恢复 {NeedsRecovery}，可继续 {Resumable}，" +
            "可清理退休源 {CleanableSource}，无 journal 残留 {Orphans}。不会自动执行任何恢复。",
            report.NeedsRecoveryCount,
            report.ResumableCount,
            report.CleanableSourceCount,
            report.OrphanCount);

        if (!dialogs.IsAvailable)
        {
            // 弹窗宿主尚未就绪时只留下日志，不退回 MessageBox 或静默执行恢复。
            return report;
        }

        try
        {
            await dialogs.ShowAsync(
                new DialogRequest
                {
                    Title = localizer.Get("lifecycle.startup.title"),
                    Content = new MessageDialogViewModel(
                        localizer.Get(
                            "lifecycle.startup.message",
                            report.NeedsRecoveryCount,
                            report.ResumableCount,
                            report.CleanableSourceCount,
                            report.OrphanCount),
                        localizer.Get("lifecycle.startup.ok")),
                    Severity = DialogSeverity.Warning,
                    CloseOutcome = DialogOutcome.Primary,
                    CanClose = true,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DialogHostUnavailableException)
        {
            // 与 IsAvailable 检查之间的竞态：日志已经记录了真实状态。
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 关闭应用时取消提示不影响状态报告。
        }

        return report;
    }
}
