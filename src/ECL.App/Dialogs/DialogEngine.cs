// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Threading;

namespace Ecl.App.Dialogs;

/// <summary>
/// 统一 Dialog Engine（ADR 0052）。负责弹窗创建、层级、模态与背景暗化状态、生命周期与结果返回；
/// 不执行任何业务操作，也不根据文案推断用户授权。
/// </summary>
public sealed class DialogEngine(DialogLayerViewModel layer) : IDialogEngine
{
    public DialogLayerViewModel Layer { get; } = layer;

    public bool IsAvailable => Layer.IsHostAttached;

    public DialogResult Show(DialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAvailable)
        {
            throw new DialogHostUnavailableException();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || !dispatcher.CheckAccess())
        {
            return ShowAsync(request).GetAwaiter().GetResult();
        }

        // 兼容同步调用方：与 Window.ShowDialog 一样压入嵌套消息循环，
        // 让弹窗内的事件仍由 UI 线程处理，调用方栈保持阻塞以维持模态语义。
        DialogResult? result = null;
        var frame = new DispatcherFrame();
        _ = ShowAsync(request).ContinueWith(
            task =>
            {
                result = task.IsCompletedSuccessfully ? task.Result : DialogResult.Closed;
                frame.Continue = false;
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        return result ?? DialogResult.Closed;
    }

    public Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAvailable)
        {
            throw new DialogHostUnavailableException();
        }

        var entry = new DialogEntryViewModel(Layer, request);
        if (request.Content is IDialogSessionAware aware)
        {
            aware.Attach(entry);
        }

        Layer.Add(entry);
        return AwaitCloseAsync(entry, cancellationToken);
    }

    public bool CloseTop(DialogOutcome outcome) => Layer.Top?.TryClose(outcome) ?? false;

    private static async Task<DialogResult> AwaitCloseAsync(DialogEntryViewModel entry, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await entry.Completion.ConfigureAwait(true);
        }

        using var registration = cancellationToken.Register(
            static state => ((DialogEntryViewModel)state!).TryClose(DialogOutcome.Cancel),
            entry);
        return await entry.Completion.ConfigureAwait(true);
    }
}
