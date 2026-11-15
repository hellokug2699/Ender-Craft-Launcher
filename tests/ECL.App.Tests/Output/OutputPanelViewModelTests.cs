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
/// 输出管线的内存边界与清空语义：
/// 每个内存阶段都有固定容量；溢出丢最旧并计数；清空后旧条目不回魂；
/// 释放后不再刷新。测试使用独立 Dispatcher，不依赖真实窗口。
/// </summary>
public sealed class OutputPanelViewModelTests : IDisposable
{
    private readonly UiThread _ui = new();
    private readonly AppLogBuffer _buffer = new();
    private readonly JsonLocalizer _localizer = new(CultureInfo.GetCultureInfo("zh-CN"));
    private readonly OperationCenterViewModel _operations;

    public OutputPanelViewModelTests() =>
        _operations = new OperationCenterViewModel(new StubOperationService(), _localizer);

    [Fact]
    public void VisibleCollectionKeepsNewestEntriesAndReportsVisibleTruncation()
    {
        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");

        _ui.Invoke(() =>
        {
            for (var index = 0; index < OutputPanelViewModel.MaxVisibleEntries + 100; index++)
            {
                logger.LogInformation("line-{Index}", index);
            }
        });

        Assert.Equal(OutputPanelViewModel.MaxVisibleEntries, viewModel.OutputEntries.Count);
        Assert.Equal("line-100", viewModel.OutputEntries[0].Message);
        Assert.Equal("line-599", viewModel.OutputEntries[^1].Message);
        Assert.True(viewModel.DroppedEntries >= 100);
        Assert.True(viewModel.HasDropped);
        Assert.Contains("100", viewModel.TruncationText);
    }

    [Fact]
    public void ProducerThroughputExceedingFlushKeepsAllStagesBounded()
    {
        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");
        const int workers = 4;
        const int perWorker = 2000;

        Parallel.For(0, workers, worker =>
        {
            for (var index = 0; index < perWorker; index++)
            {
                logger.LogInformation("w{Worker}-{Index}", worker, index);
            }
        });

        _ui.Drain();

        Assert.True(_buffer.Snapshot().Count <= AppLogBuffer.Capacity);
        Assert.True(viewModel.OutputEntries.Count <= OutputPanelViewModel.MaxVisibleEntries);
        Assert.True(_buffer.DroppedCount > 0);
        Assert.True(viewModel.DroppedEntries > 0);
    }

    [Fact]
    public async Task ClearRemovesPendingEntriesSoTheyNeverReappear()
    {
        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");
        using var gate = new ManualResetEventSlim(false);

        // 占住 UI 线程，让日志只进入 pending 队列。
        var blocker = _ui.InvokeAsync(() => gate.Wait(TimeSpan.FromSeconds(10)));
        for (var index = 0; index < 10; index++)
        {
            logger.LogInformation("pending-{Index}", index);
        }

        gate.Set();
        await blocker.WaitAsync(TimeSpan.FromSeconds(10));

        // Clear 以 Send 优先级排队，会先于 Normal 优先级的刷新执行。
        _ui.Invoke(() => viewModel.ClearOutputCommand.Execute(null));
        _ui.Drain();

        Assert.Empty(viewModel.OutputEntries);
        Assert.False(viewModel.HasOutput);
        Assert.Equal(0, viewModel.DroppedEntries);
        Assert.Equal(0, viewModel.PendingDroppedCount);

        logger.LogInformation("after-clear");
        _ui.Drain();

        Assert.Single(viewModel.OutputEntries);
        Assert.Equal("after-clear", viewModel.OutputEntries[0].Message);
    }

    [Fact]
    public async Task ClearRacingWithProducersDoesNotResurrectOldEntries()
    {
        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");
        using var stop = new CancellationTokenSource();

        var producer = Task.Run(() =>
        {
            var index = 0;
            while (!stop.IsCancellationRequested)
            {
                logger.LogInformation("race-{Index}", index++);
            }
        });

        for (var round = 0; round < 20; round++)
        {
            _ui.Invoke(() => viewModel.ClearOutputCommand.Execute(null));
        }

        stop.Cancel();
        await producer.WaitAsync(TimeSpan.FromSeconds(10));

        _ui.Invoke(() => viewModel.ClearOutputCommand.Execute(null));
        _ui.Drain();

        Assert.Empty(viewModel.OutputEntries);
        Assert.Equal(0, viewModel.DroppedEntries);
    }

    [Fact]
    public void DisposeStopsFlushingAndUnsubscribes()
    {
        var viewModel = CreateViewModel();
        var logger = _buffer.CreateLogger("Test");
        logger.LogInformation("before-dispose");
        _ui.Drain();
        var countBeforeDispose = _ui.Invoke(() => viewModel.OutputEntries.Count);
        Assert.Equal(1, countBeforeDispose);

        _ui.Invoke(viewModel.Dispose);
        logger.LogInformation("after-dispose");
        _ui.Drain();

        Assert.Equal(countBeforeDispose, _ui.Invoke(() => viewModel.OutputEntries.Count));

        _buffer.Dispose();
        logger.LogInformation("after-buffer-dispose");
        _ui.Drain();
        Assert.Equal(countBeforeDispose, _ui.Invoke(() => viewModel.OutputEntries.Count));
    }

    [Fact]
    public void LogBufferIsBoundedAndReportsDroppedEntries()
    {
        const int extra = 150;
        for (var index = 0; index < AppLogBuffer.Capacity + extra; index++)
        {
            _buffer.CreateLogger("Test").LogInformation("buffer-{Index}", index);
        }

        var snapshot = _buffer.Snapshot();

        Assert.Equal(AppLogBuffer.Capacity, snapshot.Count);
        Assert.Equal(extra, _buffer.DroppedCount);
        Assert.Equal($"buffer-{extra}", snapshot[0].Message);
        Assert.Equal($"buffer-{AppLogBuffer.Capacity + extra - 1}", snapshot[^1].Message);
    }

    public void Dispose()
    {
        _buffer.Dispose();
        _ui.Dispose();
    }

    private OutputPanelViewModel CreateViewModel() =>
        _ui.Invoke(() => new OutputPanelViewModel(_operations, _buffer, _ui.Dispatcher, _localizer));
}
