// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ecl.App.Dialogs.ViewModels;

/// <summary>
/// 通用文本弹窗内容：说明文字 + 最多三个明确语义的操作。
/// 按钮文案由调用方提供，避免引擎根据业务文案猜测语义。
/// </summary>
public sealed partial class MessageDialogViewModel : ObservableObject, IDialogSessionAware
{
    private IDialogSession? _session;

    public MessageDialogViewModel(
        string message,
        string primaryText,
        string? secondaryText = null,
        string? cancelText = null,
        DialogSeverity severity = DialogSeverity.Neutral,
        bool isDestructive = false)
    {
        Message = message;
        PrimaryText = primaryText;
        SecondaryText = secondaryText;
        CancelText = cancelText;
        Severity = severity;
        IsDestructive = isDestructive;
    }

    public string Message { get; }

    public string PrimaryText { get; }

    public string? SecondaryText { get; }

    public string? CancelText { get; }

    public DialogSeverity Severity { get; }

    public bool IsDestructive { get; }

    public bool HasSecondary => !string.IsNullOrWhiteSpace(SecondaryText);

    public bool HasCancel => !string.IsNullOrWhiteSpace(CancelText);

    public bool IsSingleAction => !HasSecondary && !HasCancel;

    public bool IsError => Severity == DialogSeverity.Error;

    public bool IsWarning => Severity == DialogSeverity.Warning;

    /// <summary>破坏性操作把焦点交给取消按钮，避免 Enter 误触发不可撤销动作。</summary>
    public DialogInitialFocus PreferredFocus =>
        HasCancel ? DialogInitialFocus.Cancel
        : HasSecondary ? DialogInitialFocus.Secondary
        : DialogInitialFocus.Primary;

    void IDialogSessionAware.Attach(IDialogSession session) => _session = session;

    [RelayCommand]
    private void Primary() => _session?.TryClose(DialogOutcome.Primary);

    [RelayCommand]
    private void Secondary() => _session?.TryClose(DialogOutcome.Secondary);

    [RelayCommand]
    private void Cancel() => _session?.TryClose(DialogOutcome.Cancel);
}
