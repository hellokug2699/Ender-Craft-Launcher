// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Operations;
using Xunit;

namespace Ecl.Tests.Operations;

public sealed class OperationServiceTests
{
    [Fact]
    public void BeginCreatesRunningSnapshotAndPublishesIt()
    {
        using var service = new OperationService();
        var changes = new List<OperationSnapshot>();
        service.Changed += (_, snapshot) => changes.Add(snapshot);

        using var handle = service.Begin(new OperationStart
        {
            Kind = OperationKind.Download,
            Title = " client.jar ",
            StageId = "download",
            Source = "Mojang",
            Total = 10,
            TotalBytes = 1_000,
        });

        var snapshot = handle.Snapshot;
        Assert.Equal("client.jar", snapshot.Title);
        Assert.Equal(OperationKind.Download, snapshot.Kind);
        Assert.Equal(OperationState.Running, snapshot.State);
        Assert.Equal(0, snapshot.BytesDownloaded);
        Assert.Equal(0, snapshot.Progress);
        Assert.Equal(0, Assert.Single(changes).BytesDownloaded);
    }

    [Fact]
    public void ProgressComputesPercentAndEta()
    {
        using var service = new OperationService();
        using var handle = service.Begin(new OperationStart
        {
            Kind = OperationKind.Download,
            Title = "libraries",
            TotalBytes = 4 * 1024 * 1024,
        });

        handle.Report(new OperationProgressUpdate
        {
            CurrentItem = "libraries/foo.jar",
            Completed = 1,
            Total = 4,
            BytesDownloaded = 1024 * 1024,
            BytesPerSecond = 1024 * 1024,
        });

        var snapshot = handle.Snapshot;
        Assert.Equal(0.25, snapshot.Progress);
        Assert.Equal(3, snapshot.Eta?.TotalSeconds);
        Assert.Equal("libraries/foo.jar", snapshot.CurrentItem);
        Assert.Equal(OperationState.Running, snapshot.State);
    }

    [Fact]
    public void CompleteKeepsFinalValuesAndIgnoresLaterReports()
    {
        using var service = new OperationService();
        var handle = service.Begin(new OperationStart { Kind = OperationKind.Install, Title = "install" });

        handle.Report(new OperationProgressUpdate { Completed = 9, Total = 10, BytesDownloaded = 900, TotalBytes = 1_000 });
        handle.Complete(new OperationProgressUpdate { Completed = 10, Total = 10, BytesDownloaded = 1_000 });
        handle.Report(new OperationProgressUpdate { BytesDownloaded = 1, CurrentItem = "ignored" });

        var snapshot = handle.Snapshot;
        Assert.True(snapshot.IsTerminal);
        Assert.Equal(OperationState.Completed, snapshot.State);
        Assert.Equal(10, snapshot.Completed);
        Assert.Equal(1, snapshot.Progress);
        Assert.Null(snapshot.Eta);
        Assert.Null(snapshot.CurrentItem);
        Assert.True(service.Snapshots.Single().IsTerminal);
    }

    [Fact]
    public void FailureStoresAttribution()
    {
        using var service = new OperationService();
        var handle = service.Begin(new OperationStart
        {
            Kind = OperationKind.Download,
            Title = "broken",
            CanRetry = true,
        });

        handle.Fail(OperationErrors.FromException("download.broken", new InvalidDataException("hash mismatch")));

        var snapshot = handle.Snapshot;
        Assert.Equal(OperationState.Failed, snapshot.State);
        Assert.NotNull(snapshot.Error);
        Assert.Equal(OperationErrorKind.FileIntegrity, snapshot.Error!.Kind);
        Assert.Equal("download.broken", snapshot.Error.Code);
        Assert.True(snapshot.CanRetry);
        Assert.False(snapshot.IsPaused);
    }

    [Fact]
    public void CancelIsNotFailure()
    {
        using var service = new OperationService();
        var handle = service.Begin(new OperationStart { Kind = OperationKind.Download, Title = "cancel me" });

        handle.Cancel();

        var snapshot = handle.Snapshot;
        Assert.Equal(OperationState.Cancelled, snapshot.State);
        Assert.Equal(OperationErrorKind.Cancelled, snapshot.Error?.Kind);
        Assert.True(snapshot.IsTerminal);
    }

    [Fact]
    public void DisposeMarksAnUnfinishedOperationAsCancelled()
    {
        using var service = new OperationService();
        var handle = service.Begin(new OperationStart { Kind = OperationKind.Download, Title = "abandoned" });

        handle.Dispose();

        var snapshot = Assert.Single(service.Snapshots);
        Assert.Equal(OperationState.Cancelled, snapshot.State);
        Assert.Equal("operation.abandoned", snapshot.Error?.Code);
    }

    [Fact]
    public void PauseIsSeparateFromTheStateMachine()
    {
        using var service = new OperationService();
        using var handle = service.Begin(new OperationStart { Kind = OperationKind.Download, Title = "paused" });

        handle.Pause();
        var paused = handle.Snapshot;
        Assert.True(paused.IsPaused);
        Assert.Equal(OperationState.Running, paused.State);

        handle.Resume();
        Assert.False(handle.Snapshot.IsPaused);
        Assert.Equal(OperationState.Running, handle.Snapshot.State);
    }

    [Fact]
    public void RetryReturnsToRunningOnNextProgress()
    {
        using var service = new OperationService();
        using var handle = service.Begin(new OperationStart { Kind = OperationKind.Download, Title = "retrying" });

        handle.Retrying(2, 3, "HTTP 503");
        var retrying = handle.Snapshot;
        Assert.Equal(OperationState.Retrying, retrying.State);
        Assert.Equal(2, retrying.RetryAttempt);
        Assert.Equal(3, retrying.MaxRetryAttempts);
        Assert.Equal("HTTP 503", retrying.RetryReason);

        handle.Report(new OperationProgressUpdate { BytesDownloaded = 100, TotalBytes = 1_000 });
        var running = handle.Snapshot;
        Assert.Equal(OperationState.Running, running.State);
        Assert.Equal(0, running.RetryAttempt);
        Assert.Null(running.RetryReason);
    }

    [Fact]
    public async Task StalledOperationIsFlaggedButStaysRunning()
    {
        using var service = new OperationService(new OperationServiceOptions
        {
            StallAfter = TimeSpan.FromMilliseconds(80),
            StallCheckInterval = TimeSpan.FromMilliseconds(20),
        });
        using var handle = service.Begin(new OperationStart { Kind = OperationKind.Download, Title = "stall" });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!handle.Snapshot.IsStalled && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var stalled = handle.Snapshot;
        Assert.True(stalled.IsStalled, "任务应在没有进度后标记为停滞。");
        Assert.Equal(OperationState.Running, stalled.State);
        Assert.Null(stalled.Error);
    }

    [Fact]
    public async Task ConcurrentUpdatesKeepSnapshotsConsistent()
    {
        using var service = new OperationService();
        var handles = Enumerable.Range(0, 5)
            .Select(index => service.Begin(new OperationStart
            {
                Kind = OperationKind.Download,
                Title = $"task-{index}",
                TotalBytes = 10_000,
            }))
            .ToArray();

        await Task.WhenAll(handles.Select(handle => Task.Run(() =>
        {
            for (var step = 1; step <= 100; step++)
            {
                handle.Report(new OperationProgressUpdate
                {
                    BytesDownloaded = step * 100,
                    BytesPerSecond = 1_000,
                    CurrentItem = $"file-{step}.jar",
                });
            }

            handle.Complete();
        })));

        var snapshots = service.Snapshots;
        Assert.Equal(5, snapshots.Count);
        Assert.All(snapshots, snapshot =>
        {
            Assert.Equal(OperationState.Completed, snapshot.State);
            Assert.Equal(10_000, snapshot.BytesDownloaded);
        });

        foreach (var handle in handles)
        {
            handle.Dispose();
        }
    }

    [Fact]
    public void PruneCompletedOnlyRemovesOldTerminalOperations()
    {
        using var service = new OperationService();
        var completed = service.Begin(new OperationStart { Kind = OperationKind.Generic, Title = "old" });
        completed.Complete();
        completed.Dispose();

        using var running = service.Begin(new OperationStart { Kind = OperationKind.Generic, Title = "running" });
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.Equal(1, service.PruneCompleted(cutoff));
        Assert.Equal("running", Assert.Single(service.Snapshots).Title);
    }

    [Fact]
    public async Task AttachedControlCanCancelAndRetryAFailedOperation()
    {
        using var service = new OperationService();
        var handle = service.Begin(new OperationStart
        {
            Kind = OperationKind.Download,
            Title = "controllable",
            CanCancel = true,
        });

        var retries = 0;
        var controller = new TestControl
        {
            Retry = _ =>
            {
                retries++;
                service.Begin(new OperationStart { Kind = OperationKind.Download, Title = "retried" });
                return Task.CompletedTask;
            },
        };
        Assert.True(service.AttachControl(handle.Id, controller));
        Assert.True(handle.Snapshot.CanRetry);

        // 失败前不能重试。
        Assert.False(await service.RetryAsync(handle.Id));

        Assert.True(service.TryCancel(handle.Id));
        Assert.Equal(1, controller.CancelCalls);

        handle.Fail(new OperationError(OperationErrorKind.Network, "download.network", "down"));
        Assert.True(handle.Snapshot.CanRetry);

        Assert.True(await service.RetryAsync(handle.Id));
        Assert.Equal(1, retries);
        Assert.Contains(service.Snapshots, snapshot => snapshot.Title == "retried" && snapshot.State == OperationState.Running);
        Assert.False(handle.Snapshot.CanRetry);

        // 重试只允许启动一次。
        Assert.False(await service.RetryAsync(handle.Id));
        handle.Dispose();
    }

    [Fact]
    public void AttachingControlToAFinishedOperationIsRejected()
    {
        using var service = new OperationService();
        var handle = service.Begin(new OperationStart { Kind = OperationKind.Generic, Title = "done" });
        handle.Complete();

        Assert.False(service.AttachControl(handle.Id, new TestControl()));
        Assert.False(service.TryCancel(handle.Id));
        handle.Dispose();
    }

    private sealed class TestControl : IOperationControl
    {
        public int CancelCalls { get; private set; }

        public Func<CancellationToken, Task>? Retry { get; init; }

        public bool TryCancel()
        {
            CancelCalls++;
            return true;
        }
    }

    [Fact]
    public void RemovesOperationById()
    {
        using var service = new OperationService();
        var handle = service.Begin(new OperationStart { Kind = OperationKind.Generic, Title = "bye" });
        Assert.True(service.Remove(handle.Id));
        Assert.Empty(service.Snapshots);
        Assert.Null(service.TryGet(handle.Id));
        handle.Dispose();
    }
}
