// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Threading;

namespace Ecl.App.Dialogs;

/// <summary>
/// 单个弹窗实例。同一个实例只关闭一次，并保证结果与生命周期可观察；
/// 内容不能直接操作窗口，只能通过本会话提交关闭结果。
///
/// 线程契约：本对象与 <see cref="DialogLayerViewModel"/> 一样只属于创建它的 UI Dispatcher。
/// <see cref="TryClose"/> 会在错误的线程上抛出 <see cref="DialogThreadAffinityException"/>；
/// 取消令牌回调由 <see cref="DialogEngine"/> 封送，内容代码不需要处理跨线程关闭。
///
/// F-12：请求可以带 <see cref="DialogRequest.AutoCloseAfter"/>。计时器回调用
/// <see cref="TimeProvider"/> 驱动（测试可注入假时钟），到点后封送回 UI Dispatcher，
/// 并且只在“仍打开、不 Busy、无结果”时以 <see cref="DialogOutcome.Closed"/> 关闭。
/// </summary>
public sealed partial class DialogEntryViewModel : ObservableObject, IDialogSession
{
    private readonly TaskCompletionSource<DialogResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly DialogLayerViewModel _layer;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource? _autoCloseCancellation;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isOpen;

    internal DialogEntryViewModel(
        DialogLayerViewModel layer,
        DialogRequest request,
        TimeProvider? timeProvider = null)
    {
        _layer = layer;
        Request = request;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isOpen = true;

        if (request.AutoCloseAfter is { } delay && delay > TimeSpan.Zero)
        {
            _autoCloseCancellation = new CancellationTokenSource();
            _ = AutoCloseAsync(delay, _autoCloseCancellation.Token);
        }
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

        // 关闭后计时器不再有意义；取消并释放，避免残留回调在后续测试/关停中重入。
        _autoCloseCancellation?.Cancel();
        _autoCloseCancellation?.Dispose();

        // 先完成任务再触发事件：内容的事件处理器即使抛异常，调用方也不会永久等待。
        _completion.TrySetResult(Result);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>弹窗标题栏的关闭按钮；是否可用由 <see cref="CanClose"/> 决定。</summary>
    [RelayCommand]
    private void Close() => RequestClose();

    /// <summary>按请求配置执行“关闭”语义；允许关闭时返回 true。</summary>
    public bool RequestClose() => CanClose && TryClose(Request.CloseOutcome);

    /// <summary>
    /// 等待自动关闭时限。计时器可能在线程池线程完成，因此关闭动作必须封送回弹窗所属
    /// Dispatcher；Dispatcher 已开始关停时直接放弃（弹窗层统一按 Closed 完成）。
    /// </summary>
    private async Task AutoCloseAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var dispatcher = _layer.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                TryAutoClose();
            }
            else
            {
                _ = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(TryAutoClose));
            }
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 在检查之后开始关停：由 Shutdown 路径统一完成弹窗。
        }
    }

    /// <summary>
    /// 到点关闭：Busy 时内容正在执行异步操作，静默关闭会让用户以为操作被取消；
    /// 已关闭或已有结果时不重复提交。自动关闭固定使用 Closed，不伪造 Primary。
    /// </summary>
    private void TryAutoClose()
    {
        if (!IsOpen || IsBusy || Result is not null)
        {
            return;
        }

        TryClose(DialogOutcome.Closed);
    }
}
