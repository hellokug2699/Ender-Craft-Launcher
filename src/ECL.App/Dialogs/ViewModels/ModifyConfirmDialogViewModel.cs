// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ecl.App.Dialogs.ViewModels;

/// <summary>
/// 实例「修改」页的执行确认内容（`instance-modify.md` §6.4）：目标组合、影响三档摘要、
/// 快照范围与执行提示。只承载页面已经算好的结论，不重新计算影响或目标。
/// 破坏性操作把初始焦点交给取消按钮（由调用方设置的 <see cref="DialogInitialFocus"/> 决定）。
/// </summary>
public sealed partial class ModifyConfirmDialogViewModel : ObservableObject, IDialogSessionAware
{
    private IDialogSession? _session;

    public ModifyConfirmDialogViewModel(
        IReadOnlyList<string> summary,
        string snapshotText,
        string notice,
        string primaryText,
        string cancelText)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(snapshotText);
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(primaryText);
        ArgumentNullException.ThrowIfNull(cancelText);

        Summary = summary;
        SnapshotText = snapshotText;
        Notice = notice;
        PrimaryText = primaryText;
        CancelText = cancelText;
    }

    /// <summary>目标组合与影响三档摘要，每条一行。</summary>
    public IReadOnlyList<string> Summary { get; }

    /// <summary>快照范围说明。</summary>
    public string SnapshotText { get; }

    /// <summary>执行期间的行为提示（不提供强制取消等）。</summary>
    public string Notice { get; }

    public string PrimaryText { get; }

    public string CancelText { get; }

    void IDialogSessionAware.Attach(IDialogSession session) => _session = session;

    [RelayCommand]
    private void Primary() => _session?.TryClose(DialogOutcome.Primary);

    [RelayCommand]
    private void Cancel() => _session?.TryClose(DialogOutcome.Cancel);
}
