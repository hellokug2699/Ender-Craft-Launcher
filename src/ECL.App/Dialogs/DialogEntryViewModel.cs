// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ecl.App.Dialogs;

/// <summary>
/// 单个弹窗实例。同一个实例只关闭一次，并保证结果与生命周期可观察；
/// 内容不能直接操作窗口，只能通过本会话提交关闭结果。
///
/// 线程契约：本对象与 <see cref="DialogLayerViewModel"/> 一样只属于创建它的 UI Dispatcher。
/// <see cref="TryClose"/> 会在错误的线程上抛出 <see cref="DialogThreadAffinityException"/>；
/// 取消令牌回调由 <see cref="DialogEngine"/> 封送，内容代码不需要处理跨线程关闭。
/// </summary>
public sealed partial class DialogEntryViewModel : ObservableObject, IDialogSession
{
    private readonly TaskCompletionSource<DialogResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly DialogLayerViewModel _layer;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isOpen;

    internal DialogEntryViewModel(DialogLayerViewModel layer, DialogRequest request)
    {
        _layer = layer;
        Request = request;
        _isOpen = true;
    }

    public DialogRequest Request { get; }

    public string Title => Request.Title;

    public object Content => Request.Content;

    public bool IsModal => Request.IsModal;

    public bool CanClose => Request.CanClose;

    public DialogSeverity Severity => Request.Severity;

    public DialogInitialFocus InitialFocus => Request.InitialFocus;

    public double MinWidth => Request.MinWidth;

    public double MaxWidth => Request.MaxWidth;

    public double MinHeight => Request.MinHeight;

    public double MaxHeight => Request.MaxHeight;

    public DialogResult? Result { get; private set; }

    public Task<DialogResult> Completion => _completion.Task;

    public event EventHandler? Closed;

    public void SetBusy(bool isBusy)
    {
        _layer.VerifyAccess();
        IsBusy = isBusy;
    }

    public bool TryClose(DialogOutcome outcome, object? value = null)
    {
        if (!IsOpen)
        {
            return false;
        }

        // 异步操作进行中不得被静默关闭：调用方要么等待，要么显式以 Failed 结束。
        if (IsBusy && outcome is not DialogOutcome.Failed)
        {
            return false;
        }

        _layer.VerifyAccess();
        _layer.CloseEntry(this, outcome, value);
        return true;
    }

    /// <summary>由弹窗层在 UI 线程调用：提交终态、完成等待任务，然后通知关闭事件。</summary>
    internal void CompleteClose(DialogOutcome outcome, object? value = null)
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        Result = new DialogResult(outcome, value);

        // 先完成任务再触发事件：内容的事件处理器即使抛异常，调用方也不会永久等待。
        _completion.TrySetResult(Result);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>弹窗标题栏的关闭按钮；是否可用由 <see cref="CanClose"/> 决定。</summary>
    [RelayCommand]
    private void Close() => RequestClose();

    /// <summary>按请求配置执行“关闭”语义；允许关闭时返回 true。</summary>
    public bool RequestClose() => CanClose && TryClose(Request.CloseOutcome);
}
