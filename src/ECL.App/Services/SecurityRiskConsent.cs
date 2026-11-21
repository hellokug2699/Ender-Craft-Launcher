// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.Core.Localization;
using Ecl.Minecraft.Launch;

namespace Ecl.App.Services;

/// <summary>
/// 启动前风险披露（ADR 0043 §6）：把启动门控拦下的未接受风险呈现为一次强制确认，给出
/// 「无视风险，继续启动 / 先自行处理 / 取消」三种选择，并把结果交回启动管线。
///
/// 本类只负责呈现与回传：不记录接受决定（由门控步骤在用户选择继续后写入），
/// 不改写检测结论，也不在宿主不可用时默认放行。
/// </summary>
public sealed class SecurityRiskConsent(
    IDialogEngine dialogs,
    IDialogService dialogService,
    ILocalizer localizer)
{
    /// <summary>最多列出的条目数；其余只给数量，避免弹窗无界增长。</summary>
    private const int MaxListedFindings = 6;

    public async Task<bool> ConfirmAsync(
        SecurityRiskConsentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var title = localizer.Get("launch.security.prompt.title");
        var message = BuildMessage(request);
        var acceptText = localizer.Get("launch.security.prompt.accept");
        var handleText = localizer.Get("launch.security.prompt.handle");

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
                                acceptText,
                                handleText,
                                localizer.Get("dialog.cancel"),
                                DialogSeverity.Error),
                            // 默认焦点在取消：Enter 不会误接受风险。
                            InitialFocus = DialogInitialFocus.Cancel,
                            Severity = DialogSeverity.Error,
                            MaxWidth = 640,
                            MaxHeight = 600,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return result.IsPrimary;
            }
            catch (DialogHostUnavailableException)
            {
                // 宿主在检查与显示之间被卸载：回退到兼容确认。
            }
            catch (DialogThreadAffinityException)
            {
                // 弹窗层已关闭（例如退出过程中）：按未接受处理，绝不静默放行。
                return false;
            }
        }

        return ShowFallback(title, message);
    }

    /// <summary>
    /// 把风险披露渲染成文本：条目只显示实例内相对路径与本地化等级，
    /// 不含盘符、用户目录或完整磁盘路径。
    /// </summary>
    private string BuildMessage(SecurityRiskConsentRequest request)
    {
        var lines = request.Findings
            .Take(MaxListedFindings)
            .Select(finding => localizer.Get(
                "launch.security.prompt.finding",
                DenotePath(finding.RelativePath),
                SecurityFindingPresentation.LevelText(finding.Level, localizer)))
            .ToList();

        if (request.Findings.Count > MaxListedFindings)
        {
            lines.Add(localizer.Get(
                "launch.security.prompt.more",
                request.Findings.Count - MaxListedFindings));
        }

        return localizer.Get(
            "launch.security.prompt.body",
            string.IsNullOrWhiteSpace(request.InstanceName) ? request.InstanceId : request.InstanceName,
            request.Findings.Count,
            string.Join(Environment.NewLine, lines.Select(line => "• " + line)));
    }

    private static string DenotePath(string? relativePath)
    {
        var formatted = SecurityFindingPresentation.FormatRelativePath(relativePath);
        return formatted.Length == 0 ? "—" : formatted;
    }

    /// <summary>
    /// 兼容路径：统一弹窗宿主尚未附加时使用系统确认框。仍是强制确认，
    /// 且只有用户明确选择“是”才算接受。
    /// </summary>
    private bool ShowFallback(string title, string message)
    {
        var text = message +
                   Environment.NewLine +
                   Environment.NewLine +
                   localizer.Get("launch.security.prompt.fallbackHint");

        try
        {
            return dialogService.Confirm(title, text);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ThreadStateException)
        {
            // 没有可用的 UI 线程通道：不替用户接受风险。
            return false;
        }
    }
}
