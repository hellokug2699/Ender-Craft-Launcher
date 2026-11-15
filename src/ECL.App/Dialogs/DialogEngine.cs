// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Threading;

namespace Ecl.App.Dialogs;

/// <summary>
/// 统一 Dialog Engine（ADR 0052）。负责弹窗创建、层级、模态与背景暗化状态、生命周期与结果返回；
/// 不执行任何业务操作，也不根据文案推断用户授权。
///
/// 线程契约：
/// - 弹窗图层、条目与所有绑定属性都归创建 <see cref="DialogLayerViewModel"/> 的 UI Dispatcher；
///   引擎只在该 Dispatcher 上创建、关闭与修改弹窗。
/// - <see cref="ShowAsync"/> 可从任意线程调用：后台调用会被封送到 UI 线程后显示，并返回同一个结果任务。
/// - <see cref="Show"/> 使用嵌套消息循环实现同步模态，只能在 UI 线程调用；后台线程必须使用 <see cref="ShowAsync"/>，
///   否则抛出 <see cref="DialogThreadAffinityException"/>。
/// - 取消令牌回调可能在线程池线程执行，引擎会把关闭请求封送回 UI 线程，不直接改 WPF 绑定状态。
/// - 每个弹窗任务只完成一次；重复关闭、取消与正常完成的竞争由 UI 线程串行化。
/// - 所属 Dispatcher 关停或弹窗层关停时，打开中的弹窗以 <see cref="DialogOutcome.Closed"/> 完成，等待方不会悬挂。
/// </summary>
public sealed class DialogEngine : IDialogEngine, IDisposable
{
    private readonly DialogLayerViewModel _layer;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public DialogEngine(DialogLayerViewModel layer)
    {
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _dispatcher = layer.Dispatcher;
        _dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
    }

    public DialogLayerViewModel Layer => _layer;

    public bool IsAvailable => !_disposed && !_layer.IsShutdown && _layer.IsHostAttached;

    public DialogResult Show(DialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_dispatcher.CheckAccess())
        {
            throw new DialogThreadAffinityException(
                "同步 Show 只能在弹窗所属 UI 线程调用；后台线程请使用 ShowAsync。");
        }

        ThrowIfUnavailable();
        var entry = Present(request);

        // 兼容同步调用方：与 Window.ShowDialog 一样压入嵌套消息循环，
        // 让弹窗内的事件仍由 UI 线程处理，调用方栈保持阻塞以维持模态语义。
        DialogResult? result = null;
        var frame = new DispatcherFrame();
        _ = entry.Completion.ContinueWith(
            task =>
            {
                var outcome = task.IsCompletedSuccessfully ? task.Result : DialogResult.Closed;
                try
                {
                    _dispatcher.BeginInvoke(
                        DispatcherPriority.Send,
                        new Action(() =>
                        {
                            result = outcome;
                            frame.Continue = false;
                        }));
                }
                catch (InvalidOperationException)
                {
                    // Dispatcher 已进入关停：窗口生命周期结束时嵌套帧随之退出，这里不再排队。
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        Dispatcher.PushFrame(frame);
        return result ?? DialogResult.Closed;
    }

    public Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_dispatcher.CheckAccess())
        {
            ThrowIfUnavailable();
            return AwaitCloseAsync(Present(request), cancellationToken);
        }

        return ShowMarshaledAsync(request, cancellationToken);
    }

    public bool CloseTop(DialogOutcome outcome)
    {
        if (!_dispatcher.CheckAccess())
        {
            throw new DialogThreadAffinityException("CloseTop 只能在弹窗所属 UI 线程调用。");
        }

        return _layer.Top?.TryClose(outcome) ?? false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dispatcher.ShutdownStarted -= OnDispatcherShutdownStarted;
    }

    private async Task<DialogResult> ShowMarshaledAsync(DialogRequest request, CancellationToken cancellationToken)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            throw new DialogThreadAffinityException("弹窗所属 Dispatcher 已关闭，无法显示弹窗。");
        }

        DialogEntryViewModel entry;
        try
        {
            entry = await _dispatcher.InvokeAsync(
                    () =>
                    {
                        ThrowIfUnavailable();
                        return Present(request);
                    })
                .Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException exception)
        {
            throw new DialogThreadAffinityException($"弹窗所属 Dispatcher 已关闭，无法显示弹窗：{exception.Message}");
        }

        return await AwaitCloseAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DialogResult> AwaitCloseAsync(DialogEntryViewModel entry, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            // 不用 UI 同步上下文：Dispatcher 关停时挂起的续体可能被丢弃，会让调用方永久等待。
            return await entry.Completion.ConfigureAwait(false);
        }

        using var registration = cancellationToken.Register(() => RequestCancellation(entry));
        return await entry.Completion.ConfigureAwait(false);
    }

    /// <summary>
    /// 取消可能在任意线程触发；所有关闭动作都封送到弹窗所属 Dispatcher 执行。
    /// </summary>
    private void RequestCancellation(DialogEntryViewModel entry)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            entry.TryClose(DialogOutcome.Cancel);
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => entry.TryClose(DialogOutcome.Cancel)));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 在检查之后开始关停：弹窗由 ShutdownStarted/窗口关闭路径统一按 Closed 完成。
        }
    }

    private void OnDispatcherShutdownStarted(object? sender, EventArgs e) => _layer.Shutdown();

    private DialogEntryViewModel Present(DialogRequest request)
    {
        var entry = new DialogEntryViewModel(_layer, request);
        if (request.Content is IDialogSessionAware aware)
        {
            aware.Attach(entry);
        }

        _layer.Add(entry);
        return entry;
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed || _layer.IsShutdown)
        {
            throw new DialogHostUnavailableException();
        }

        if (!_layer.IsHostAttached)
        {
            throw new DialogHostUnavailableException();
        }
    }
}
