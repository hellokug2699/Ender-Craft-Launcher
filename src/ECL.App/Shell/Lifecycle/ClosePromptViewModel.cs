// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;

namespace Ecl.App.Shell.Lifecycle;

/// <summary>
/// 关闭询问弹窗的内容（F-12）：三个明确语义的操作 + “记住我的选择”复选。
///
/// 设计约束：
/// - 按钮语义由内容决定并通过会话提交，引擎不根据文案猜测“是/否”；
/// - 取消按钮是默认焦点，防止 Enter 误退出；
/// - 托盘不可用时不禁用整个弹窗，只禁用“最小化到托盘”并说明原因，
///   用户仍然可以选择退出或取消——静默忽略关闭请求是不允许的。
/// </summary>
public sealed partial class ClosePromptViewModel : ObservableObject, IDialogSessionAware
{
    private IDialogSession? _session;

    public ClosePromptViewModel(
        ClosePromptRequest request,
        string minimizeText,
        string exitText,
        string cancelText,
        string rememberText,
        string unavailableText)
    {
        ArgumentNullException.ThrowIfNull(request);

        Message = request.Message;
        CanMinimizeToTray = request.CanMinimizeToTray;
        MinimizeText = minimizeText;
        ExitText = exitText;
        CancelText = cancelText;
        RememberText = rememberText;
        MinimizeUnavailableText = request.CanMinimizeToTray ? string.Empty : unavailableText;
    }

    public string Message { get; }

    public bool CanMinimizeToTray { get; }

    public string MinimizeText { get; }

    public string ExitText { get; }

    public string CancelText { get; }

    public string RememberText { get; }

    /// <summary>托盘不可用时在“最小化到托盘”旁显示的真实原因。</summary>
    public string MinimizeUnavailableText { get; }

    public bool HasMinimizeUnavailableReason => !string.IsNullOrEmpty(MinimizeUnavailableText);

    /// <summary>用户是否要求把本次选择写回 <c>launcher.closeButtonAction</c>。</summary>
    [ObservableProperty]
    private bool _rememberChoice;

    void IDialogSessionAware.Attach(IDialogSession session) => _session = session;

    [RelayCommand]
    private void Minimize()
    {
        if (!CanMinimizeToTray)
        {
            return;
        }

        _session?.TryClose(
            DialogOutcome.Primary,
            new ClosePromptDecision(CloseChoice.MinimizeToTray, RememberChoice));
    }

    [RelayCommand]
    private void Exit() =>
        _session?.TryClose(DialogOutcome.Secondary, new ClosePromptDecision(CloseChoice.Exit, RememberChoice));

    [RelayCommand]
    private void Cancel() => _session?.TryClose(DialogOutcome.Cancel);
}
