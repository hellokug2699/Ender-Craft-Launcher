// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services.Confirmation;
using Ecl.App.Services.Lifecycle;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Shell.Lifecycle;

/// <summary>
/// 启动时的“是否恢复上次工作区”询问（F-12 最小切片）。
///
/// 边界：
/// - 只在<strong>有效快照</strong>存在且至少有一个条目时询问；没有快照、损坏或不兼容时直接跳过，
///   不把“读不到”包装成“没有”去打扰用户；
/// - 用户拒绝（或直接关闭弹窗）时不恢复任何条目，也不删除快照；
/// - 弹窗宿主不可用时只记录日志、**不**静默恢复：恢复行为必须由用户确认；
/// - 跳过/失败逐项说明：用 <see cref="IResultDialogService"/> 且 <c>IsCritical=true</c>，
///   关键结果永不自动关闭，避免用户错过“某个实例已经不存在”的结论。
/// </summary>
public sealed class StartupRestorePrompt(
    WorkspaceRestoreService restore,
    IDialogEngine dialogs,
    IResultDialogService resultDialogs,
    ILocalizer localizer,
    ILogger<StartupRestorePrompt> logger)
{
    /// <summary>执行询问与恢复；返回 null 表示没有询问或用户选择跳过。</summary>
    public async Task<WorkspaceRestoreResult?> RunAsync(CancellationToken cancellationToken = default)
    {
        WorkspaceRestorePlan plan;
        try
        {
            // Inspect 会调用实例服务读取文件系统，放到后台线程避免启动时卡住消息循环。
            plan = await restore.InspectAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 快照问题不应阻断启动；用户仍然可以手动打开任何工作区。
            logger.LogWarning(exception, "检查工作区会话快照失败；跳过恢复询问。");
            return null;
        }

        if (plan.Availability != WorkspaceRestoreAvailability.Available || plan.Candidates.Count == 0)
        {
            logger.LogInformation(
                "启动恢复：没有可询问的快照（{Availability}）。",
                plan.Availability);
            return null;
        }

        if (!dialogs.IsAvailable)
        {
            // 宿主尚未附加时不能恢复：没有用户确认的自动恢复会改变用户启动后的落点。
            logger.LogInformation("启动恢复：弹窗宿主不可用，跳过询问且不恢复任何条目。");
            return null;
        }

        bool accepted;
        try
        {
            var result = await dialogs.ShowAsync(
                    new DialogRequest
                    {
                        Title = localizer.Get("workspace.restore.prompt.title"),
                        Content = new MessageDialogViewModel(
                            localizer.Get(
                                "workspace.restore.prompt.message",
                                plan.Candidates.Count,
                                plan.Skipped),
                            localizer.Get("workspace.restore.prompt.restore"),
                            localizer.Get("workspace.restore.prompt.skip")),
                        InitialFocus = DialogInitialFocus.Primary,
                        Severity = DialogSeverity.Information,
                        CloseOutcome = DialogOutcome.Cancel,
                    },
                    cancellationToken)
                .ConfigureAwait(true);
            accepted = result.IsPrimary;
        }
        catch (DialogHostUnavailableException)
        {
            logger.LogInformation("启动恢复：询问时弹窗宿主已卸载，跳过恢复。");
            return null;
        }

        if (!accepted)
        {
            logger.LogInformation("启动恢复：用户选择跳过；不修改任何工作区。");
            return null;
        }

        WorkspaceRestoreResult restored;
        try
        {
            restored = await restore.RestoreAsync(plan, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "启动恢复执行失败；保留已打开的工作区，不回滚。");
            restored = new WorkspaceRestoreResult(0, 0, []);
        }

        logger.LogInformation(
            "启动恢复：恢复 {Restored}，跳过 {Skipped}，失败 {Failed}。",
            restored.Restored,
            restored.Skipped,
            restored.Failures.Count);

        if (restored.Skipped > 0 || restored.Failures.Count > 0)
        {
            await ShowPartialFailureAsync(plan, restored, cancellationToken).ConfigureAwait(true);
        }

        return restored;
    }

    /// <summary>
    /// 逐项说明为什么有内容没有恢复。关键结果永远等待用户阅读：
    /// “实例已不存在”这类结论可能影响用户对数据去向的判断。
    /// </summary>
    private async Task ShowPartialFailureAsync(
        WorkspaceRestorePlan plan,
        WorkspaceRestoreResult result,
        CancellationToken cancellationToken)
    {
        var message = new StringBuilder();
        message.Append(localizer.Get(
            "workspace.restore.result.summary",
            result.Restored,
            plan.Skipped,
            result.Failures.Count));

        foreach (var candidate in plan.Candidates.Where(candidate => !candidate.CanRestore))
        {
            message.Append('\n').Append(localizer.Get(
                "workspace.restore.result.item",
                Describe(candidate.InstanceId, candidate.Destination, candidate.ToolId),
                localizer.Get(candidate.Reason ?? WorkspaceRestoreService.ReasonEntryEmpty)));
        }

        foreach (var failure in result.Failures)
        {
            message.Append('\n').Append(localizer.Get(
                "workspace.restore.result.item",
                Describe(failure.InstanceId, failure.Destination, failure.ToolId),
                localizer.Get(failure.Reason)));
        }

        try
        {
            await resultDialogs.ShowResultAsync(
                    new ResultDialogRequest(
                        localizer.Get("workspace.restore.result.title"),
                        message.ToString(),
                        IsCritical: true),
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 结果弹窗失败时结论仍然写入日志，用户可再次手动打开工作区。
            logger.LogWarning(exception, "展示工作区恢复结果失败。");
        }
    }

    private static string Describe(string? instanceId, string? destination, string? toolId) =>
        instanceId ?? destination ?? toolId ?? "?";
}
