// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Ecl.App.Services.Lifecycle;
using Ecl.Core.Runtime;
using Ecl.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Services;

/// <summary>
/// F-12 关闭协调：单飞、容器只释放一次、取消并等待活动任务、超时不阻塞、异常仍完成。
/// 测试使用真实 <see cref="ApplicationLifecycleCoordinator"/> 与内存任务中心，
/// 不用更短的等待掩盖“关闭会卡住”这类真实问题。
/// </summary>
public sealed class AppShutdownCoordinatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static ApplicationLifecycleOptions FastLifecycleOptions() => new()
    {
        DefaultShutdownTimeout = TimeSpan.FromMilliseconds(500),
        TotalShutdownTimeout = TimeSpan.FromSeconds(2),
    };

    private static AppShutdownOptions FastShutdownOptions() => new()
    {
        OperationDrainTimeout = TimeSpan.FromMilliseconds(150),
        OperationPollInterval = TimeSpan.FromMilliseconds(10),
        StopStepTimeout = TimeSpan.FromSeconds(1),
    };

    [Fact]
    public async Task ShutdownIsSingleFlightAndDisposesTheContainerOnce()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastLifecycleOptions());
        lifecycle.BeginStart();
        lifecycle.MarkRunning();
        var provider = new CountingServiceProvider();
        var coordinator = new AppShutdownCoordinator(
            lifecycle,
            new TestOperationService(),
            provider,
            NullLogger<AppShutdownCoordinator>.Instance,
            FastShutdownOptions());

        var first = coordinator.ShutdownAsync();
        var second = coordinator.ShutdownAsync();
        await Task.WhenAll(first, second).WaitAsync(Timeout);

        Assert.Same(first, second);
        Assert.True(coordinator.IsShuttingDown);
        Assert.Equal(1, provider.DisposeCount);
        Assert.Equal(ApplicationLifecycleState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task OperationsStepIsTheOnlyStepAndItCancelsActiveOperations()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastLifecycleOptions());
        lifecycle.BeginStart();
        lifecycle.MarkRunning();
        var operations = new TestOperationService();
        var running = operations.AddRunning("下载资源包");
        var provider = new CountingServiceProvider();
        var coordinator = new AppShutdownCoordinator(
            lifecycle,
            operations,
            provider,
            NullLogger<AppShutdownCoordinator>.Instance,
            FastShutdownOptions());

        await coordinator.ShutdownAsync().WaitAsync(Timeout);

        Assert.Equal(1, operations.CancelCalls);
        Assert.True(operations.TryGetSnapshot(running)!.IsTerminal);
        var report = await lifecycle.ShutdownAsync();
        var step = Assert.Single(report.Steps);
        Assert.Equal("operations", step.Name);
        Assert.Equal(ShutdownStepState.Completed, step.State);
        // 不注册游戏进程 / LogHost 的停止步骤：那些资源属于各自模块。
        Assert.Empty(report.Resources);
    }

    [Fact]
    public async Task WorkThatIgnoresCancellationDoesNotBlockShutdownPastTheDrainTimeout()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastLifecycleOptions());
        lifecycle.BeginStart();
        lifecycle.MarkRunning();
        var operations = new TestOperationService { CancelFlipsToTerminal = false };
        operations.AddRunning("顽固任务");
        var provider = new CountingServiceProvider();
        var coordinator = new AppShutdownCoordinator(
            lifecycle,
            operations,
            provider,
            NullLogger<AppShutdownCoordinator>.Instance,
            FastShutdownOptions());

        var watch = Stopwatch.StartNew();
        await coordinator.ShutdownAsync().WaitAsync(Timeout);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"关闭耗时过长：{watch.Elapsed}");
        Assert.Equal(1, provider.DisposeCount);
        Assert.Equal(ApplicationLifecycleState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task LifecycleFailureIsLoggedAndTheContainerIsStillDisposed()
    {
        var provider = new CountingServiceProvider();
        var coordinator = new AppShutdownCoordinator(
            new ThrowingLifecycle(),
            new TestOperationService(),
            provider,
            NullLogger<AppShutdownCoordinator>.Instance,
            FastShutdownOptions());

        await coordinator.ShutdownAsync().WaitAsync(Timeout);

        // 关闭失败不能让容器泄漏，也不能把异常抛给调用方。
        Assert.Equal(1, provider.DisposeCount);
        Assert.True(coordinator.IsShuttingDown);
    }

    [Fact]
    public void ProbeReadsActiveOperationsAndNeverInventsDirtyWorkspaceState()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastLifecycleOptions());
        lifecycle.BeginStart();
        lifecycle.MarkRunning();
        var operations = new TestOperationService();
        operations.AddRunning("安装实例");

        var readiness = new CloseReadinessProbe(operations, lifecycle).Read();

        Assert.Equal(1, readiness.ActiveOperations);
        Assert.Equal(0, readiness.ActiveBackgroundWork);
        // 工作区脏状态尚未接入：探测必须诚实地返回 false，而不是猜测。
        Assert.False(readiness.HasDirtyWorkspaces);
        Assert.False(readiness.HasUnsafeToAbort);
    }

    private sealed class CountingServiceProvider : IServiceProvider, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public object? GetService(Type serviceType) => null;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestOperationService : IOperationService, IOperationStore
    {
        private readonly object _gate = new();
        private readonly List<OperationSnapshot> _snapshots = [];

        public bool CancelFlipsToTerminal { get; set; } = true;

        public int CancelCalls { get; private set; }

        public IOperationStore Store => this;

        public IReadOnlyList<OperationSnapshot> Snapshots
        {
            get
            {
                lock (_gate)
                {
                    return _snapshots.ToArray();
                }
            }
        }

        public event EventHandler<OperationSnapshot>? Changed;

        public Guid AddRunning(string title)
        {
            var snapshot = new OperationSnapshot
            {
                Id = Guid.NewGuid(),
                Kind = OperationKind.Download,
                Title = title,
                State = OperationState.Running,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            lock (_gate)
            {
                _snapshots.Add(snapshot);
            }

            Changed?.Invoke(this, snapshot);
            return snapshot.Id;
        }

        public OperationSnapshot? TryGetSnapshot(Guid operationId)
        {
            lock (_gate)
            {
                return _snapshots.LastOrDefault(snapshot => snapshot.Id == operationId);
            }
        }

        public OperationSnapshot? TryGet(Guid operationId) => TryGetSnapshot(operationId);

        public bool TryCancel(Guid operationId)
        {
            CancelCalls++;
            if (!CancelFlipsToTerminal)
            {
                return false;
            }

            lock (_gate)
            {
                var index = _snapshots.FindIndex(snapshot => snapshot.Id == operationId);
                if (index < 0)
                {
                    return false;
                }

                _snapshots[index] = _snapshots[index] with { State = OperationState.Cancelled };
            }

            return true;
        }

        public IOperationHandle Begin(OperationStart start) => throw new NotSupportedException();

        public bool AttachControl(Guid operationId, IOperationControl control) => false;

        public Task<bool> RetryAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public bool Remove(Guid operationId) => false;

        public int PruneCompleted(DateTimeOffset completedBefore) => 0;
    }

    /// <summary>关闭总是失败的协调器替身：验证异常不会跳过容器释放。</summary>
    private sealed class ThrowingLifecycle : IApplicationLifecycle
    {
        public ApplicationLifecycleState State { get; private set; } = ApplicationLifecycleState.Running;

        public event EventHandler<ApplicationLifecycleState>? StateChanged
        {
            add { }
            remove { }
        }

        public int ActiveBackgroundWork => 0;

        public int ActiveTasks => 0;

        public void BeginStart() => State = ApplicationLifecycleState.Starting;

        public void MarkRunning() => State = ApplicationLifecycleState.Running;

        public BackgroundWorkHandle TrackWork(BackgroundWorkDescriptor descriptor, Func<CancellationToken, Task> work) =>
            throw new NotSupportedException();

        public BackgroundWorkHandle TrackTask(BackgroundWorkDescriptor descriptor, Task task) =>
            throw new NotSupportedException();

        public void RegisterStopStep(string name, Func<CancellationToken, Task> step, TimeSpan? timeout = null)
        {
        }

        public void RegisterResource(string owner, Func<ValueTask> disposeAsync, TimeSpan? timeout = null)
        {
        }

        public Task<ShutdownReport> ShutdownAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<ShutdownReport>(new InvalidOperationException("shutdown boom"));

        public Task<ShutdownReport> FailStartupAsync(Exception exception, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
