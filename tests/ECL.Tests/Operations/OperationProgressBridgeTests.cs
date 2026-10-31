// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Operations;
using Xunit;

namespace Ecl.Tests.Operations;

public sealed class OperationProgressBridgeTests
{
    [Fact]
    public void ForwardsProgressToBothInnerProgressAndStore()
    {
        using var service = new OperationService();
        var values = new List<int>();
        using var bridge = new OperationProgressBridge<int>(
            service,
            new OperationStart { Kind = OperationKind.Install, Title = "install", Total = 10 },
            new CallbackProgress<int>(values.Add),
            value => new OperationProgressUpdate
            {
                StageId = "install",
                Completed = value,
                Total = 10,
                BytesDownloaded = value * 10,
                TotalBytes = 100,
            });

        bridge.Report(3);

        Assert.Equal([3], values);
        Assert.True(bridge.IsOwner);
        var snapshot = Assert.Single(service.Snapshots);
        Assert.Equal(OperationState.Running, snapshot.State);
        Assert.Equal(3, snapshot.Completed);
        Assert.Equal(30, snapshot.BytesDownloaded);
    }

    [Fact]
    public void NestedBridgeReusesTheOuterOperationAndKeepsForwarding()
    {
        using var service = new OperationService();
        var values = new List<int>();
        using var outer = new OperationProgressBridge<int>(
            service,
            new OperationStart { Kind = OperationKind.Install, Title = "outer" },
            new CallbackProgress<int>(values.Add),
            value => new OperationProgressUpdate { StageId = "outer", Completed = value });
        using var inner = new OperationProgressBridge<int>(
            service,
            new OperationStart { Kind = OperationKind.Download, Title = "inner" },
            outer,
            value => new OperationProgressUpdate { StageId = "inner", Completed = value });

        // 嵌套管道的进度只经外层写入任务中心，不会覆盖外层阶段。
        inner.Report(5);
        outer.Report(6);

        Assert.False(inner.IsOwner);
        Assert.Equal([5, 6], values);
        var snapshot = Assert.Single(service.Snapshots);
        Assert.Equal("outer", snapshot.StageId);
        Assert.Equal(6, snapshot.Completed);
    }

    [Fact]
    public void ScopeIsEnteredForTheOwnerAndRestoredOnDispose()
    {
        using var service = new OperationService();
        Assert.False(OperationScope.IsActive);

        using (var bridge = new OperationProgressBridge<int>(
                   service,
                   new OperationStart { Kind = OperationKind.Install, Title = "scoped" },
                   null,
                   _ => new OperationProgressUpdate()))
        {
            Assert.True(OperationScope.IsActive);
            Assert.Equal(bridge.OperationId, OperationScope.CurrentId);
        }

        Assert.False(OperationScope.IsActive);
    }

    [Fact]
    public void BridgeWorksWithoutOperationService()
    {
        var values = new List<int>();
        using var cancellation = new CancellationTokenSource();
        using var bridge = new OperationProgressBridge<int>(
            null,
            new OperationStart { Kind = OperationKind.Install, Title = "no store" },
            new CallbackProgress<int>(values.Add),
            value => new OperationProgressUpdate { Completed = value });

        bridge.Report(1);

        Assert.Equal([1], values);
        Assert.False(bridge.IsOwner);
        Assert.Null(bridge.OperationId);
        Assert.False(bridge.AttachControl(cancellation));
    }

    [Fact]
    public void AttachControlCancelsTheBusinessTaskThroughTheStore()
    {
        using var service = new OperationService();
        using var cancellation = new CancellationTokenSource();
        using var bridge = new OperationProgressBridge<int>(
            service,
            new OperationStart { Kind = OperationKind.Install, Title = "cancellable" },
            null,
            _ => new OperationProgressUpdate());
        Assert.True(bridge.AttachControl(cancellation));

        Assert.True(service.TryCancel(bridge.OperationId!.Value));
        Assert.True(cancellation.IsCancellationRequested);

        // 取消请求后任务中心不再允许重复取消。
        Assert.False(bridge.Handle!.Snapshot.CanCancel);
    }

    [Fact]
    public async Task AttachedRetryRunsTheBusinessActionAgain()
    {
        using var service = new OperationService();
        using var cancellation = new CancellationTokenSource();
        var retries = 0;
        using var bridge = new OperationProgressBridge<int>(
            service,
            new OperationStart { Kind = OperationKind.Install, Title = "retryable" },
            null,
            _ => new OperationProgressUpdate());
        Assert.True(bridge.AttachControl(
            cancellation,
            _ =>
            {
                retries++;
                return Task.CompletedTask;
            }));

        bridge.Fail(new OperationError(OperationErrorKind.Network, "install.download_failed", "下载失败"));
        Assert.True(await service.RetryAsync(bridge.OperationId!.Value));
        Assert.Equal(1, retries);
    }

    [Fact]
    public void BridgeExposesSnapshotForBusinessServices()
    {
        using var service = new OperationService();
        using var bridge = new OperationProgressBridge<int>(
            service,
            new OperationStart { Kind = OperationKind.Install, Title = "snapshot" },
            null,
            _ => new OperationProgressUpdate());

        bridge.Report(1);
        Assert.Equal(OperationState.Running, bridge.Handle!.Snapshot.State);
        bridge.Complete();
        Assert.Equal(OperationState.Completed, bridge.Handle.Snapshot.State);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
