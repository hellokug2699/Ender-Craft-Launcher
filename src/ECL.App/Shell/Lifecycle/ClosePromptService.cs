// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.App.Services.Lifecycle;
using Ecl.Core.Localization;

namespace Ecl.App.Shell.Lifecycle;

/// <summary>
/// 关闭询问的默认实现：走统一 Dialog Engine；宿主不可用时退回 <see cref="IDialogService"/> 的
/// 系统消息框路径。
///
/// 回退原则：托盘尚未接好、弹窗层尚未附加时，关闭请求不能被静默忽略，
/// 也不能静默退出。回退只提供“退出 / 取消”两个选择（没有托盘时最小化到托盘本来就不成立），
/// 并把取消设为安全默认。
/// </summary>
public sealed class ClosePromptService(
    IDialogEngine dialogs,
    IDialogService fallbackDialogs,
    ILocalizer localizer) : IClosePrompt
{
    public async Task<ClosePromptDecision> AskAsync(
        ClosePromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (dialogs.IsAvailable)
        {
            try
            {
                var content = new ClosePromptViewModel(
                    request,
                    localizer.Get("close.prompt.minimize"),
                    localizer.Get("close.prompt.exit"),
                    localizer.Get("close.prompt.cancel"),
                    localizer.Get("close.prompt.remember"),
                    localizer.Get("close.prompt.minimizeUnavailable"));

                // 关闭窗口（Escape / 标题栏关闭）按“取消”处理：关闭询问本身被关掉不代表同意退出。
                var result = await dialogs.ShowAsync(
                        new DialogRequest
                        {
                            Title = localizer.Get("close.prompt.title"),
                            Content = content,
                            InitialFocus = DialogInitialFocus.Cancel,
                            Severity = DialogSeverity.Warning,
                            CloseOutcome = DialogOutcome.Cancel,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return result.Value as ClosePromptDecision ?? result.Outcome switch
                {
                    DialogOutcome.Primary => new ClosePromptDecision(CloseChoice.MinimizeToTray, Remember: false),
                    DialogOutcome.Secondary => new ClosePromptDecision(CloseChoice.Exit, Remember: false),
                    _ => new ClosePromptDecision(CloseChoice.Cancel, Remember: false),
                };
            }
            catch (DialogHostUnavailableException)
            {
                // 宿主在可用性检查与显示之间被卸载：继续走下方回退，不丢用户请求。
            }
        }

        // 回退路径带“确定/取消”而不是只显示信息：用户必须能拒绝退出。
        var exit = fallbackDialogs.Confirm(
            localizer.Get("close.prompt.title"),
            localizer.Get("close.prompt.fallback", request.Message),
            acknowledgementToken: null);
        return new ClosePromptDecision(exit ? CloseChoice.Exit : CloseChoice.Cancel, Remember: false);
    }

    public async Task<bool> ConfirmExitAsync(
        CloseReadiness readiness,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        var title = localizer.Get("close.confirm.title");
        var message = localizer.Get(
            "close.confirm.message",
            readiness.ActiveOperations,
            readiness.ActiveBackgroundWork);

        // 未保存工作区 / 不可安全中止的状态优先于普通关闭确认：无论 confirmOnClose 如何都要明确说明。
        if (readiness.HasDirtyWorkspaces || readiness.HasUnsafeToAbort)
        {
            message = string.Equals(message, title, StringComparison.Ordinal)
                ? localizer.Get("close.confirm.unsafe")
                : $"{message}\n\n{localizer.Get("close.confirm.unsafe")}";
        }

        if (dialogs.IsAvailable)
        {
            try
            {
                var result = await dialogs.ShowAsync(
                        new DialogRequest
                        {
                            Title = title,
                            Content = new MessageDialogViewModel(
                                message,
                                localizer.Get("close.confirm.exit"),
                                cancelText: localizer.Get("close.prompt.cancel"),
                                severity: DialogSeverity.Warning,
                                isDestructive: true),
                            InitialFocus = DialogInitialFocus.Cancel,
                            Severity = DialogSeverity.Warning,
                            CloseOutcome = DialogOutcome.Cancel,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                return result.IsPrimary;
            }
            catch (DialogHostUnavailableException)
            {
                // 与 AskAsync 相同的竞态回退。
            }
        }

        return fallbackDialogs.Confirm(title, message, acknowledgementToken: null);
    }
}
