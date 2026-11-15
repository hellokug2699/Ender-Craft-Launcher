// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Dialogs;

/// <summary>弹窗结果语义（ADR 0052 §6）。用户确认、关闭窗口、业务失败必须可区分。</summary>
public enum DialogOutcome
{
    /// <summary>尚无结果。</summary>
    None,

    /// <summary>用户选择了主操作。</summary>
    Primary,

    /// <summary>用户选择了次要操作。</summary>
    Secondary,

    /// <summary>用户明确取消。</summary>
    Cancel,

    /// <summary>用户直接关闭弹窗（遮罩关闭、Escape、系统关闭）。</summary>
    Closed,

    /// <summary>业务执行失败，或内容进入无法继续的状态。</summary>
    Failed,
}

/// <summary>弹窗打开后应获得焦点的控件。破坏性操作应把焦点放在取消按钮上。</summary>
public enum DialogInitialFocus
{
    FirstFocusable,
    Primary,
    Secondary,
    Cancel,
}

public enum DialogSeverity
{
    Neutral,
    Information,
    Warning,
    Error,
}

/// <summary>
/// 统一弹窗请求。内容只承载 UI 视图与交互状态；业务判断、目标校验和风险说明由调用方负责。
/// </summary>
public sealed record DialogRequest
{
    public required string Title { get; init; }

    public required object Content { get; init; }

    /// <summary>模态弹窗会暗化并锁定所属窗口背景；非模态不暗化，也不锁定背景。</summary>
    public bool IsModal { get; init; } = true;

    /// <summary>是否允许通过 Escape/关闭按钮结束弹窗。</summary>
    public bool CanClose { get; init; } = true;

    /// <summary>用户直接关闭时的结果语义。</summary>
    public DialogOutcome CloseOutcome { get; init; } = DialogOutcome.Closed;

    public DialogInitialFocus InitialFocus { get; init; } = DialogInitialFocus.FirstFocusable;

    public DialogSeverity Severity { get; init; } = DialogSeverity.Neutral;

    public double MinWidth { get; init; } = 360;

    public double MaxWidth { get; init; } = 560;

    public double MinHeight { get; init; }

    public double MaxHeight { get; init; } = 520;
}

/// <summary>弹窗关闭后的稳定结果；<see cref="Value"/> 只用于调用方自己的结果对象。</summary>
public sealed record DialogResult(DialogOutcome Outcome, object? Value = null)
{
    public bool IsPrimary => Outcome == DialogOutcome.Primary;

    public static DialogResult Cancel { get; } = new(DialogOutcome.Cancel);

    public static DialogResult Closed { get; } = new(DialogOutcome.Closed);
}

/// <summary>由引擎交给弹窗内容的会话；内容不能自行创建或操纵窗口。</summary>
public interface IDialogSession
{
    bool IsOpen { get; }

    /// <summary>内容正在执行异步操作；此时拒绝普通关闭，避免用户误以为操作已取消。</summary>
    bool IsBusy { get; }

    DialogResult? Result { get; }

    void SetBusy(bool isBusy);

    bool TryClose(DialogOutcome outcome, object? value = null);

    event EventHandler? Closed;
}

/// <summary>内容 ViewModel 实现该接口后，引擎会在显示前注入会话。</summary>
public interface IDialogSessionAware
{
    void Attach(IDialogSession session);
}

/// <summary>
/// 统一弹窗入口。所有新弹窗都必须通过该接口创建；业务页面不得自行创建窗口或叠加遮罩。
/// </summary>
public interface IDialogEngine
{
    /// <summary>弹窗宿主是否已附加到主窗口；附加前（例如首次启动向导）调用方必须使用兼容路径。</summary>
    bool IsAvailable { get; }

    DialogResult Show(DialogRequest request);

    Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken cancellationToken = default);

    bool CloseTop(DialogOutcome outcome);
}

/// <summary>宿主尚未附加到窗口时抛出；调用方据此回退到兼容路径。</summary>
public sealed class DialogHostUnavailableException()
    : InvalidOperationException("统一弹窗宿主尚未附加到主窗口。");
