// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Threading;
using Ecl.App.Shell.Output;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecl.App.Tests.Output;

/// <summary>
/// Output 面板的释放竞态回归测试：事件准入、待刷新队列入队、Dispatcher 刷新调度与
/// <see cref="OutputPanelViewModel.Dispose"/> 必须串行化。
///
/// 这些测试通过内部同步点（<c>AdmissionPaused</c>、<c>DisposeStarted</c>、<c>FlushPaused</c>）
/// 精确构造交错，不使用休眠来等待竞争窗口；断言的是 Dispose 的线性化关系：
/// 返回后没有新的有效入队、新的刷新调度或新的可见集合变更。
/// </summary>
public sealed class OutputLifecycleTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly UiThread _ui = new();
    private readonly AppLogBuffer _buffer = new();
    private readonly JsonLocalizer _localizer = new(CultureInfo.GetCultureInfo("zh-CN"));
    private readonly OperationCenterViewModel _operations;

    public OutputLifecycleTests() =>
        _operations = new OperationCenterViewModel(new StubOperationService(), _localizer);

    /// <summary>
    /// 场景 a：回调已通过释放/代次早期检查、尚未入队时 Dispose 开始。
    /// 准入临界区必须让 Dispose 等待；恢复后条目不会留在队列，也不会在释放后刷新到界面。
    /// </summary>
    [Fact]
    public async Task DisposeWaitsForAdmissionAndDiscardsTheQueuedEntry()
    {
        using var admissionPaused = new ManualResetEventSlim(false);
        using var resumeAdmission = new ManualResetEventSlim(false);
        using var disposeStarted = new ManualResetEventSlim(false);

        var viewModel = CreateViewModel();
        viewModel.AdmissionPaused = () =>
        {
            admissionPaused.Set();
            resumeAdmission.Wait(Timeout);
        };
        viewModel.DisposeStarted = () => disposeStarted.Set();

        var logger = _buffer.CreateLogger("Test");
        var producer = Task.Run(() => logger.LogInformation("during-dispose"));

        Assert.True(admissionPaused.Wait(Timeout), "生产者未进入准入临界区。");

        var disposeTask = Task.Run(() => _ui.Invoke(viewModel.Dispose));
        Assert.True(disposeStarted.Wait(Timeout), "Dispose 未开始。");

        // 准入检查已经通过但入队尚未完成：Dispose 必须等待生命周期锁。
        Assert.False(disposeTask.IsCompleted, "Dispose 不得在准入临界区结束前返回。");

        resumeAdmission.Set();
        await producer.WaitAsync(Timeout);
        await disposeTask.WaitAsync(Timeout);
        _ui.Drain();

        Assert.Empty(viewModel.OutputEntries);
        Assert.Equal(0, viewModel.PendingDroppedCount);
    }

    /// <summary>
    /// 场景 b：刷新已作为 DispatcherOperation 提交，Dispose 随后执行。
    /// 已提交的刷新必须被取消或安全 no-op，不得在释放后修改可见集合。
    /// </summary>
    [Fact]
    public async Task DisposeCancelsFlushThatWasAlreadyQueued()
    {
        using var uiBlocked = new ManualResetEventSlim(false);
        using var releaseUi = new ManualResetEventSlim(false);
        using var notificationDelivered = new ManualResetEventSlim(false);

        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");

        // 占住 UI 线程，让日志只进入待刷新队列；刷新操作在 Normal 优先级排队。
        var blocker = _ui.InvokeAsync(() =>
        {
            uiBlocked.Set();
            releaseUi.Wait(Timeout);
        });
        Assert.True(uiBlocked.Wait(Timeout), "UI 线程未被占住。");

        // 面板先订阅，这个后订阅者确认面板处理完通知并已提交刷新调度。
        _buffer.EntryWritten += (_, _) => notificationDelivered.Set();
        var producer = Task.Run(() => logger.LogInformation("queued"));
        Assert.True(notificationDelivered.Wait(Timeout), "日志未进入通知阶段。");
        await producer.WaitAsync(Timeout);

        // Send 优先级保证 Dispose 先于排队中的 Normal 刷新执行。
        var disposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _ui.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            viewModel.Dispose();
            disposeCompleted.TrySetResult();
        }));

        releaseUi.Set();
        await blocker.WaitAsync(Timeout);
        await disposeCompleted.Task.WaitAsync(Timeout);
        _ui.Drain();

        Assert.Empty(viewModel.OutputEntries);
        Assert.Equal(0, viewModel.DroppedEntries);
    }

    /// <summary>
    /// 场景 c：刷新回调已开始修改可见集合时，另一个线程调用 Dispose。
    /// Dispose 必须通过 UI 屏障等待该回调完成，而不是在回调仍在执行时返回。
    /// </summary>
    [Fact]
    public async Task DisposeWaitsForFlushThatIsAlreadyRunning()
    {
        using var flushPaused = new ManualResetEventSlim(false);
        using var resumeFlush = new ManualResetEventSlim(false);
        using var disposeStarted = new ManualResetEventSlim(false);

        var viewModel = CreateViewModel();
        viewModel.FlushPaused = () =>
        {
            flushPaused.Set();
            resumeFlush.Wait(Timeout);
        };
        viewModel.DisposeStarted = () => disposeStarted.Set();

        var logger = _buffer.CreateLogger("Test");

        // 在 UI 线程写日志：准入成功后直接在 UI 线程刷新，回调在写集合之后暂停。
        var flushTask = Task.Run(() => _ui.Invoke(() => logger.LogInformation("during-dispose")));
        Assert.True(flushPaused.Wait(Timeout), "刷新回调未开始。");

        var disposeTask = Task.Run(viewModel.Dispose);
        Assert.True(disposeStarted.Wait(Timeout), "Dispose 未开始。");

        Assert.False(disposeTask.IsCompleted, "刷新回调仍在执行时 Dispose 不得返回。");

        resumeFlush.Set();
        await flushTask.WaitAsync(Timeout);
        await disposeTask.WaitAsync(Timeout);

        // 刷新回调在 Dispose 返回前完成，它写入的条目属于释放前的可见状态。
        Assert.Equal(1, _ui.Invoke(() => viewModel.OutputEntries.Count));
        _ui.Drain();
        Assert.Equal(1, _ui.Invoke(() => viewModel.OutputEntries.Count));
    }

    /// <summary>
    /// 场景 d：Dispatcher 已开始关停时调用 Dispose。
    /// 不得尝试同步 Invoke 造成死锁或异常；释放本身仍要完成。
    /// </summary>
    [Fact]
    public async Task DisposeAfterDispatcherShutdownCompletesWithoutBlocking()
    {
        using var ui = new UiThread();
        var viewModel = ui.Invoke(() =>
            new OutputPanelViewModel(_operations, _buffer, ui.Dispatcher, _localizer));

        ui.Dispatcher.InvokeShutdown();

        var disposeTask = Task.Run(viewModel.Dispose);
        await disposeTask.WaitAsync(Timeout);

        // 释放后日志事件已退订，写入不再触及面板状态。
        _buffer.CreateLogger("Test").LogInformation("after-dispose");
    }

    /// <summary>
    /// 场景 e：后台线程调用 ClearOutput，已通过 Dispatcher 归属检查、尚未提交 DispatcherOperation 时
    /// Dispose 完成并返回。恢复后的清空不得再提交新的 Dispatcher 操作。
    /// </summary>
    [Fact]
    public async Task DisposePreventsPendingClearOutputFromSchedulingAfterDisposal()
    {
        using var clearPassedEarlyCheck = new ManualResetEventSlim(false);
        using var resumeClear = new ManualResetEventSlim(false);

        var viewModel = CreateViewModel();
        var paused = 0;
        viewModel.ClearDispatchPaused = () =>
        {
            if (Interlocked.Exchange(ref paused, 1) != 0)
            {
                return;
            }

            clearPassedEarlyCheck.Set();
            resumeClear.Wait(Timeout);
        };

        // 后台线程进入跨线程清空路径：CheckAccess 已为 false，但尚未提交 DispatcherOperation。
        var clearTask = Task.Run(() => viewModel.ClearOutputCommand.Execute(null));
        Assert.True(clearPassedEarlyCheck.Wait(Timeout), "ClearOutput 未进入跨线程调度路径。");

        // 清空被暂停期间，Dispose 在 UI 线程完成并返回。
        var disposeTask = Task.Run(() => _ui.Invoke(viewModel.Dispose));
        await disposeTask.WaitAsync(Timeout);

        // 占住 UI 线程，使“释放后错误提交的清空操作”保持可观察，而不会立刻被 Dispatcher 执行。
        using var uiBlocked = new ManualResetEventSlim(false);
        using var releaseUi = new ManualResetEventSlim(false);
        var blocker = _ui.InvokeAsync(() =>
        {
            uiBlocked.Set();
            releaseUi.Wait(Timeout);
        });
        Assert.True(uiBlocked.Wait(Timeout), "UI 线程未被占住。");

        resumeClear.Set();
        await clearTask.WaitAsync(Timeout);

        Assert.False(viewModel.HasPendingClearOperation, "Dispose 返回后不得再提交清空操作。");

        releaseUi.Set();
        await blocker.WaitAsync(Timeout);
        _ui.Drain();

        Assert.False(viewModel.HasPendingClearOperation);
        Assert.Empty(viewModel.OutputEntries);
    }

    /// <summary>
    /// 场景 f：清空已提交为 DispatcherOperation、尚未执行时 Dispose。
    /// 尚未开始的清空必须被取消，不得在释放后清理可见集合或推进代次。
    /// </summary>
    [Fact]
    public async Task DisposeCancelsPendingClearOperation()
    {
        using var uiBlocked = new ManualResetEventSlim(false);
        using var releaseUi = new ManualResetEventSlim(false);

        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");

        logger.LogInformation("kept");
        _ui.Drain();
        Assert.Equal(1, _ui.Invoke(() => viewModel.OutputEntries.Count));

        var blocker = _ui.InvokeAsync(() =>
        {
            uiBlocked.Set();
            releaseUi.Wait(Timeout);
        });
        Assert.True(uiBlocked.Wait(Timeout), "UI 线程未被占住。");

        var clearTask = Task.Run(() => viewModel.ClearOutputCommand.Execute(null));
        Assert.True(
            SpinWait.SpinUntil(() => viewModel.HasPendingClearOperation, Timeout),
            "清空操作未提交为 DispatcherOperation。");
        await clearTask.WaitAsync(Timeout);

        // Send 优先级保证 Dispose 先于 Normal 优先级的清空操作执行，从而取消它。
        var disposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _ui.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            viewModel.Dispose();
            disposeCompleted.TrySetResult();
        }));

        releaseUi.Set();
        await blocker.WaitAsync(Timeout);
        await disposeCompleted.Task.WaitAsync(Timeout);
        _ui.Drain();

        // 清空被取消：Dispose 不会清空可见集合，被取消的回调也不会改动它。
        Assert.Equal(1, _ui.Invoke(() => viewModel.OutputEntries.Count));
        Assert.False(viewModel.HasPendingClearOperation);
    }

    public void Dispose()
    {
        _buffer.Dispose();
        _ui.Dispose();
    }

    private OutputPanelViewModel CreateViewModel() =>
        _ui.Invoke(() => new OutputPanelViewModel(_operations, _buffer, _ui.Dispatcher, _localizer));
}
