// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using Ecl.App.Dialogs;
using Xunit;

namespace Ecl.App.Tests.Dialogs;

/// <summary>
/// 弹窗层的线程与生命周期契约（ADR 0052）：
/// 只有 UI Dispatcher 能修改弹窗状态；取消从任意线程到来时必须被安全封送；
/// 每个弹窗任务只完成一次；宿主关停不会让等待方悬挂。
/// </summary>
public sealed class DialogEngineThreadingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static DialogRequest Request(string title = "测试弹窗") => new()
    {
        Title = title,
        Content = new object(),
    };

    private static DialogFacts Create()
    {
        var ui = new UiThread();
        var layer = ui.Invoke(() => new DialogLayerViewModel(ui.Dispatcher));
        ui.Invoke(layer.AttachHost);
        var engine = ui.Invoke(() => new DialogEngine(layer));
        return new DialogFacts(ui, layer, engine);
    }

    [Fact]
    public void LayerRejectsConstructionAndMutationOffOwnedDispatcher()
    {
        var ui = new UiThread();
        try
        {
            Assert.Throws<DialogThreadAffinityException>(() => new DialogLayerViewModel(ui.Dispatcher));

            var layer = ui.Invoke(() => new DialogLayerViewModel(ui.Dispatcher));
            Assert.Throws<DialogThreadAffinityException>(() => layer.AttachHost());
            Assert.Throws<DialogThreadAffinityException>(() => layer.Shutdown());
            Assert.False(ui.Invoke(() => layer.IsHostAttached));
        }
        finally
        {
            ui.Dispose();
        }
    }

    [Fact]
    public void SyncShowFromBackgroundThreadIsRejectedDeterministically()
    {
        using var facts = Create();

        Assert.Throws<DialogThreadAffinityException>(() => facts.Engine.Show(Request()));
        Assert.Empty(facts.Ui.Invoke(() => facts.Layer.Entries.ToArray()));
    }

    [Fact]
    public async Task ShowAsyncFromBackgroundThreadMarshalsPresentation()
    {
        using var facts = Create();

        var task = facts.Engine.ShowAsync(Request(), CancellationToken.None);

        facts.Ui.Drain();
        var entry = facts.Ui.Invoke(() => facts.Layer.Top);
        Assert.NotNull(entry);

        facts.Ui.Invoke(() => entry!.TryClose(DialogOutcome.Primary));
        var result = await task.WaitAsync(Timeout);

        Assert.Equal(DialogOutcome.Primary, result.Outcome);
        Assert.Empty(facts.Ui.Invoke(() => facts.Layer.Entries.ToArray()));
    }

    [Fact]
    public async Task CancellationFromWorkerThreadClosesDialogOnUiThread()
    {
        using var facts = Create();
        using var cancellation = new CancellationTokenSource();
        var closedOnUiThread = 0;
        var closedTotal = 0;

        var task = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request(), cancellation.Token));
        var entry = facts.Ui.Invoke(() => facts.Layer.Top)!;
        entry.Closed += (_, _) =>
        {
            Interlocked.Increment(ref closedTotal);
            if (facts.Ui.Dispatcher.CheckAccess())
            {
                Interlocked.Increment(ref closedOnUiThread);
            }
        };

        cancellation.Cancel();

        var result = await task.WaitAsync(Timeout);

        Assert.Equal(DialogOutcome.Cancel, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref closedTotal));
        Assert.Equal(1, Volatile.Read(ref closedOnUiThread));
        Assert.Empty(facts.Ui.Invoke(() => facts.Layer.Entries.ToArray()));
    }

    [Fact]
    public async Task NormalCompletionWinningRaceCompletesExactlyOnce()
    {
        using var facts = Create();
        using var cancellation = new CancellationTokenSource();
        var closedTotal = 0;

        var task = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request(), cancellation.Token));
        var entry = facts.Ui.Invoke(() => facts.Layer.Top)!;
        entry.Closed += (_, _) => Interlocked.Increment(ref closedTotal);

        facts.Ui.Invoke(() => entry.TryClose(DialogOutcome.Primary));
        cancellation.Cancel();

        var result = await task.WaitAsync(Timeout);

        Assert.Equal(DialogOutcome.Primary, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref closedTotal));
    }

    [Fact]
    public async Task CancellationWinningRaceCompletesExactlyOnce()
    {
        using var facts = Create();
        using var cancellation = new CancellationTokenSource();
        var closedTotal = 0;

        var task = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request(), cancellation.Token));
        var entry = facts.Ui.Invoke(() => facts.Layer.Top)!;
        entry.Closed += (_, _) => Interlocked.Increment(ref closedTotal);

        cancellation.Cancel();
        facts.Ui.Drain();
        var lateCloseAccepted = facts.Ui.Invoke(() => entry.TryClose(DialogOutcome.Primary));

        var result = await task.WaitAsync(Timeout);

        Assert.False(lateCloseAccepted);
        Assert.Equal(DialogOutcome.Cancel, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref closedTotal));
    }

    [Fact]
    public async Task RepeatedCloseRequestsOnlyCloseOnce()
    {
        using var facts = Create();
        var closedTotal = 0;

        var task = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request()));
        var entry = facts.Ui.Invoke(() => facts.Layer.Top)!;
        entry.Closed += (_, _) => Interlocked.Increment(ref closedTotal);

        Assert.True(facts.Ui.Invoke(() => entry.TryClose(DialogOutcome.Cancel)));
        Assert.False(facts.Ui.Invoke(() => entry.TryClose(DialogOutcome.Primary)));
        Assert.False(facts.Ui.Invoke(() => entry.RequestClose()));

        var result = await task.WaitAsync(Timeout);

        Assert.Equal(DialogOutcome.Cancel, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref closedTotal));
    }

    [Fact]
    public async Task CrossThreadEntryMutationIsRejected()
    {
        using var facts = Create();
        var task = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request()));
        var entry = facts.Ui.Invoke(() => facts.Layer.Top)!;

        Assert.Throws<DialogThreadAffinityException>(() => entry.TryClose(DialogOutcome.Cancel));
        Assert.Throws<DialogThreadAffinityException>(() => entry.SetBusy(true));

        facts.Ui.Invoke(() => entry.TryClose(DialogOutcome.Cancel));
        await task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ClosingParentClosesNestedChildrenFirst()
    {
        using var facts = Create();
        var closeOrder = new ConcurrentQueue<string>();

        var parentTask = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request("parent")));
        var parent = facts.Ui.Invoke(() => facts.Layer.Top)!;
        parent.Closed += (_, _) => closeOrder.Enqueue("parent");

        var childTask = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request("child")));
        var child = facts.Ui.Invoke(() => facts.Layer.Top)!;
        child.Closed += (_, _) => closeOrder.Enqueue("child");

        Assert.Equal(2, facts.Ui.Invoke(() => facts.Layer.Entries.Count));

        // 关闭父弹窗：仍打开的子弹窗必须先完成，不能留下失去所有者的窗口/任务。
        facts.Ui.Invoke(() => parent.TryClose(DialogOutcome.Cancel));

        var parentResult = await parentTask.WaitAsync(Timeout);
        var childResult = await childTask.WaitAsync(Timeout);

        Assert.Equal(DialogOutcome.Cancel, parentResult.Outcome);
        Assert.Equal(DialogOutcome.Closed, childResult.Outcome);
        Assert.Equal(["child", "parent"], closeOrder.ToArray());
        Assert.Empty(facts.Ui.Invoke(() => facts.Layer.Entries.ToArray()));
    }

    [Fact]
    public async Task HostShutdownCompletesActiveDialogsWithClosedResult()
    {
        using var facts = Create();
        var task = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request()));
        Assert.NotNull(facts.Ui.Invoke(() => facts.Layer.Top));

        facts.Ui.Invoke(facts.Layer.Shutdown);

        var result = await task.WaitAsync(Timeout);
        Assert.Equal(DialogOutcome.Closed, result.Outcome);
        Assert.True(facts.Ui.Invoke(() => facts.Layer.IsShutdown));
        Assert.Empty(facts.Ui.Invoke(() => facts.Layer.Entries.ToArray()));
        Assert.Null(facts.Ui.Invoke(() => facts.Layer.Top));
        Assert.False(facts.Engine.IsAvailable);

        // 捕获在 UI 线程上抛出的异常，避免它穿过 Dispatcher 消息循环导致测试宿主崩溃。
        var captured = facts.Ui.Invoke<Exception?>(() =>
        {
            try
            {
                _ = facts.Engine.ShowAsync(Request());
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });
        Assert.IsType<DialogHostUnavailableException>(captured);
    }

    [Fact]
    public async Task DispatcherShutdownCompletesActiveDialogInsteadOfHangingWaiter()
    {
        var facts = Create();
        try
        {
            var task = facts.Ui.Invoke(() => facts.Engine.ShowAsync(Request()));

            facts.Ui.Dispatcher.InvokeShutdown();

            var result = await task.WaitAsync(Timeout);
            Assert.Equal(DialogOutcome.Closed, result.Outcome);
        }
        finally
        {
            facts.Engine.Dispose();
            facts.Ui.Dispose();
        }
    }

    [Fact]
    public async Task SyncShowReturnsResultAfterUiInitiatedClose()
    {
        using var facts = Create();
        using var presented = new ManualResetEventSlim(false);
        facts.Layer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DialogLayerViewModel.Top) && facts.Layer.Top is not null)
            {
                presented.Set();
            }
        };

        var showTask = Task.Run(() =>
            facts.Ui.Invoke(() => facts.Engine.Show(Request())));

        Assert.True(presented.Wait(Timeout));
        var entry = facts.Ui.Invoke(() => facts.Layer.Top);
        Assert.NotNull(entry);

        facts.Ui.Invoke(() => entry!.TryClose(DialogOutcome.Primary));

        var result = await showTask.WaitAsync(Timeout);
        Assert.Equal(DialogOutcome.Primary, result.Outcome);
    }

    private sealed class DialogFacts(UiThread ui, DialogLayerViewModel layer, DialogEngine engine) : IDisposable
    {
        public UiThread Ui { get; } = ui;

        public DialogLayerViewModel Layer { get; } = layer;

        public DialogEngine Engine { get; } = engine;

        public void Dispose()
        {
            Ui.Invoke(() =>
            {
                Layer.Shutdown();
                Layer.DetachHost();
                Engine.Dispose();
            });
            Ui.Dispose();
        }
    }
}
