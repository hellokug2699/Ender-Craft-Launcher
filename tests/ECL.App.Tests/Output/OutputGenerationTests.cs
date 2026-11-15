// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Shell.Output;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecl.App.Tests.Output;

/// <summary>
/// 输出清空与迟到通知的竞态回归测试。
///
/// 观察到的缺陷：<see cref="AppLogBuffer.Write"/> 在锁内提交条目、在锁外触发
/// <see cref="AppLogBuffer.EntryWritten"/>，因此生产者可能在“已提交、未送达”之间停住，
/// 而 UI 线程完成清空后才把旧条目送达面板，导致清空结果被旧条目覆盖。
///
/// 这些测试用阻塞订阅者精确构造该窗口，不依赖休眠或时序运气：
/// 阻塞订阅者先于面板订阅，从而稳定地让面板成为“迟到”的一方。
/// </summary>
public sealed class OutputGenerationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly UiThread _ui = new();
    private readonly AppLogBuffer _buffer = new();
    private readonly JsonLocalizer _localizer = new(CultureInfo.GetCultureInfo("zh-CN"));
    private readonly OperationCenterViewModel _operations;

    public OutputGenerationTests() =>
        _operations = new OperationCenterViewModel(new StubOperationService(), _localizer);

    /// <summary>
    /// 需求 1：生产者已提交条目但尚未送达通知时清空输出，迟到的旧通知不得让条目复活。
    /// </summary>
    [Fact]
    public async Task DelayedNotificationFromBeforeClearIsRejected()
    {
        using var notificationInFlight = new ManualResetEventSlim(false);
        using var resumeDelivery = new ManualResetEventSlim(false);

        // 先于面板订阅：通知到达顺序固定为“阻塞订阅者 → 面板”。
        _buffer.EntryWritten += (_, _) =>
        {
            notificationInFlight.Set();
            resumeDelivery.Wait(Timeout);
        };

        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");

        var producer = Task.Run(() => logger.LogInformation("before-clear"));

        Assert.True(notificationInFlight.Wait(Timeout), "生产者未进入通知送达阶段。");

        // 条目已经提交到日志缓冲并捕获了旧代次，但通知还没有送达面板。
        _ui.Invoke(() => viewModel.ClearOutputCommand.Execute(null));

        resumeDelivery.Set();
        await producer.WaitAsync(Timeout);
        _ui.Drain();

        Assert.Empty(viewModel.OutputEntries);
        Assert.False(viewModel.HasOutput);
        Assert.Equal(0, viewModel.DroppedEntries);
    }

    /// <summary>需求 2：清空之后产生的条目必须保留并显示，且携带新的输出代次。</summary>
    [Fact]
    public void EntryProducedAfterClearIsRetainedAndShown()
    {
        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");

        _ui.Invoke(() => viewModel.ClearOutputCommand.Execute(null));

        var generationAfterClear = _buffer.Generation;
        Assert.True(generationAfterClear > 0, "清空输出必须推进输出代次。");

        long publishedGeneration = -1;
        _buffer.EntryWritten += (_, notification) => publishedGeneration = notification.Generation;

        logger.LogInformation("after-clear");
        _ui.Drain();

        Assert.Equal(generationAfterClear, publishedGeneration);
        Assert.Single(viewModel.OutputEntries);
        Assert.Equal("after-clear", viewModel.OutputEntries[0].Message);
        Assert.True(viewModel.HasOutput);
    }

    /// <summary>
    /// 需求 3：清空与待刷新队列、并发生产者交错时，任何清空前的条目都不得留在界面上。
    /// 断言的是硬性不变量：最终清空后只可能看到最终清空之后写入的那一行。
    /// 生产者数量有限，避免“无限补充调度队列”让 Drain 长时间拿不到 Background 优先级。
    /// </summary>
    [Fact]
    public async Task ClearDuringConcurrentProductionNeverLeavesPreClearEntries()
    {
        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");
        using var stop = new CancellationTokenSource();
        const int perProducer = 400;

        var producers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < perProducer && !stop.IsCancellationRequested; index++)
            {
                logger.LogInformation("old-{Worker}-{Index}", worker, index);
            }
        })).ToArray();

        for (var round = 0; round < 10; round++)
        {
            _ui.Invoke(() => viewModel.ClearOutputCommand.Execute(null));

            // 让排队的刷新与下一轮清空交错执行，覆盖“清空后队列里仍有旧条目”的路径。
            _ui.Drain();
        }

        stop.Cancel();
        await Task.WhenAll(producers).WaitAsync(Timeout);

        _ui.Invoke(() => viewModel.ClearOutputCommand.Execute(null));
        logger.LogInformation("after-final-clear");
        _ui.Drain();

        var visible = _ui.Invoke(() => viewModel.OutputEntries.Select(entry => entry.Message).ToArray());
        Assert.Equal(["after-final-clear"], visible);
    }

    /// <summary>
    /// 需求 4：清空期间旧生产者迟到、新生产者继续写入。Drain 后只允许新代次条目显示，
    /// 旧代次条目既不复活，也不污染清空后的丢弃计数。
    /// </summary>
    [Fact]
    public async Task LateOldProducerCannotMixWithNewGenerationEntries()
    {
        using var oldNotificationInFlight = new ManualResetEventSlim(false);
        using var resumeOldDelivery = new ManualResetEventSlim(false);

        // 先于面板订阅：旧条目停在通知阶段，面板尚未看到它。
        _buffer.EntryWritten += (_, notification) =>
        {
            if (notification.Entry.Message == "old")
            {
                oldNotificationInFlight.Set();
                resumeOldDelivery.Wait(Timeout);
            }
        };

        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");

        var oldProducer = Task.Run(() => logger.LogInformation("old"));
        Assert.True(oldNotificationInFlight.Wait(Timeout), "旧生产者未进入通知阶段。");

        _ui.Invoke(() => viewModel.ClearOutputCommand.Execute(null));

        logger.LogInformation("new");
        _ui.Drain();
        Assert.Equal(["new"], _ui.Invoke(() => viewModel.OutputEntries.Select(entry => entry.Message).ToArray()));

        resumeOldDelivery.Set();
        await oldProducer.WaitAsync(Timeout);
        _ui.Drain();

        Assert.Equal(["new"], _ui.Invoke(() => viewModel.OutputEntries.Select(entry => entry.Message).ToArray()));
        Assert.Equal(0, viewModel.DroppedEntries);
    }

    /// <summary>
    /// 需求 5：代次推进不得破坏有界容量与丢弃计数；清空后各阶段的计数必须归零。
    /// </summary>
    [Fact]
    public async Task GenerationChangesKeepPendingBoundedAndAccountingConsistent()
    {
        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");
        using var gate = new ManualResetEventSlim(false);

        // 占住 UI 线程：日志只进入待刷新队列，待刷新队列必然溢出。
        var blocker = _ui.InvokeAsync(() => gate.Wait(Timeout));
        for (var index = 0; index < OutputPanelViewModel.PendingCapacity + 250; index++)
        {
            logger.LogInformation("flood-{Index}", index);
        }

        gate.Set();
        await blocker.WaitAsync(Timeout);

        Assert.True(viewModel.PendingDroppedCount >= 250, "待刷新队列必须有界并记录丢弃数。");

        _ui.Invoke(() => viewModel.ClearOutputCommand.Execute(null));
        Assert.Equal(0, viewModel.PendingDroppedCount);
        Assert.Equal(0, viewModel.DroppedEntries);
        _ui.Drain();
        Assert.Empty(viewModel.OutputEntries);

        // 新代次继续写入时容量边界仍然生效。
        var logger2 = _buffer.CreateLogger("Test");
        _ui.Invoke(() =>
        {
            for (var index = 0; index < OutputPanelViewModel.MaxVisibleEntries + 40; index++)
            {
                logger2.LogInformation("new-{Index}", index);
            }
        });

        Assert.Equal(OutputPanelViewModel.MaxVisibleEntries, viewModel.OutputEntries.Count);
        Assert.Equal("new-40", viewModel.OutputEntries[0].Message);
        Assert.True(_buffer.Snapshot().Count <= AppLogBuffer.Capacity);
    }

    /// <summary>需求 6：释放后迟到的通知不得再改动面板状态。</summary>
    [Fact]
    public async Task DisposeStopsDelayedNotificationFromReachingThePanel()
    {
        using var notificationInFlight = new ManualResetEventSlim(false);
        using var resumeDelivery = new ManualResetEventSlim(false);

        _buffer.EntryWritten += (_, _) =>
        {
            notificationInFlight.Set();
            resumeDelivery.Wait(Timeout);
        };

        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");

        var producer = Task.Run(() => logger.LogInformation("before-dispose"));

        Assert.True(notificationInFlight.Wait(Timeout), "生产者未进入通知送达阶段。");

        _ui.Invoke(viewModel.Dispose);
        resumeDelivery.Set();
        await producer.WaitAsync(Timeout);
        _ui.Drain();

        Assert.Empty(viewModel.OutputEntries);
    }

    public void Dispose()
    {
        _buffer.Dispose();
        _ui.Dispose();
    }

    private OutputPanelViewModel CreateViewModel() =>
        _ui.Invoke(() => new OutputPanelViewModel(_operations, _buffer, _ui.Dispatcher, _localizer));
}
