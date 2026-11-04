// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Core.Runtime;

/// <summary>
/// 应用级关闭协调器（ADR 0039）。它只负责编排：登记工作、请求取消、等待完成、执行停止步骤、
/// 释放登记资源并汇总失败；不接管业务模块的领域生命周期，也不强制结束 Minecraft 游戏进程。
/// </summary>
public sealed class ApplicationLifecycleCoordinator : IApplicationLifecycle, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<TrackedWork> _work = [];
    private readonly List<StopStep> _steps = [];
    private readonly List<AsyncResource> _resources = [];
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ApplicationLifecycleOptions _options;
    private readonly ILogger _logger;
    private readonly Stopwatch _stopwatch = new();
    private Task<ShutdownReport>? _shutdownTask;
    private Exception? _startupError;
    private ApplicationLifecycleState _state = ApplicationLifecycleState.Created;

    public ApplicationLifecycleCoordinator(
        ApplicationLifecycleOptions? options = null,
        ILogger<ApplicationLifecycleCoordinator>? logger = null)
    {
        _options = options ?? new ApplicationLifecycleOptions();
        _logger = logger ?? NullLogger<ApplicationLifecycleCoordinator>.Instance;
    }

    public ApplicationLifecycleState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public event EventHandler<ApplicationLifecycleState>? StateChanged;

    public void BeginStart()
    {
        lock (_gate)
        {
            if (_state is ApplicationLifecycleState.Starting or ApplicationLifecycleState.Running)
            {
                return;
            }

            if (_state != ApplicationLifecycleState.Created)
            {
                throw new InvalidOperationException($"当前状态 {_state} 不能开始启动。");
            }

            _state = ApplicationLifecycleState.Starting;
            _stopwatch.Start();
        }

        RaiseStateChanged(ApplicationLifecycleState.Starting);
    }

    public void MarkRunning()
    {
        lock (_gate)
        {
            if (_state == ApplicationLifecycleState.Running)
            {
                return;
            }

            if (_state != ApplicationLifecycleState.Starting)
            {
                throw new InvalidOperationException($"当前状态 {_state} 不能进入 Running。");
            }

            _state = ApplicationLifecycleState.Running;
        }

        RaiseStateChanged(ApplicationLifecycleState.Running);
    }

    public BackgroundWorkHandle TrackWork(BackgroundWorkDescriptor descriptor, Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            if (!AcceptsNewWork())
            {
                _logger.LogWarning("拒绝后台工作 {WorkId}：应用已在关闭流程中。", descriptor.Id);
                return Rejected(descriptor.Id);
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            var tracked = new TrackedWork(descriptor, linked, Stopwatch.StartNew());
            _work.Add(tracked);
            tracked.Completion = RunWorkAsync(tracked, work);
            return new BackgroundWorkHandle(descriptor.Id, tracked.Completion, linked.Token, rejected: false);
        }
    }

    public BackgroundWorkHandle TrackTask(BackgroundWorkDescriptor descriptor, Task task)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(task);

        lock (_gate)
        {
            if (!AcceptsNewWork())
            {
                _logger.LogWarning("拒绝后台工作 {WorkId}：应用已在关闭流程中。", descriptor.Id);
                return Rejected(descriptor.Id);
            }

            var tracked = new TrackedWork(descriptor, new CancellationTokenSource(), Stopwatch.StartNew());
            _work.Add(tracked);
            tracked.Completion = ObserveTaskAsync(tracked, task);
            return new BackgroundWorkHandle(descriptor.Id, tracked.Completion, tracked.Cancellation.Token, rejected: false);
        }
    }

    public void RegisterStopStep(string name, Func<CancellationToken, Task> step, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(step);

        lock (_gate)
        {
            if (_shutdownTask is not null || _state is ApplicationLifecycleState.Stopping or ApplicationLifecycleState.Disposing or ApplicationLifecycleState.Stopped)
            {
                throw new InvalidOperationException("应用已在关闭流程中，不能再登记关闭步骤。");
            }

            _steps.Add(new StopStep(name, step, timeout ?? _options.DefaultShutdownTimeout));
        }
    }

    public void RegisterResource(string owner, Func<ValueTask> disposeAsync, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(disposeAsync);

        lock (_gate)
        {
            if (_shutdownTask is not null || _state is ApplicationLifecycleState.Stopping or ApplicationLifecycleState.Disposing or ApplicationLifecycleState.Stopped)
            {
                throw new InvalidOperationException("应用已在关闭流程中，不能再登记资源。");
            }

            _resources.Add(new AsyncResource(owner, disposeAsync, timeout ?? _options.DefaultShutdownTimeout));
        }
    }

    public Task<ShutdownReport> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _shutdownTask ??= ShutdownCoreAsync(cancellationToken);
        }
    }

    public async Task<ShutdownReport> FailStartupAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_gate)
        {
            _startupError ??= exception;
            if (_state is ApplicationLifecycleState.Created or ApplicationLifecycleState.Starting or ApplicationLifecycleState.Running)
            {
                _state = ApplicationLifecycleState.Faulted;
            }
        }

        RaiseStateChanged(ApplicationLifecycleState.Faulted);
        _logger.LogError(exception, "应用启动失败，进入受控清理。");
        return await ShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    /// <summary>
    /// 同步兜底释放（DI 容器同步 Dispose 路径）。不无限阻塞 UI 线程：最多等待 2 秒，
    /// 未完成的工作由关闭报告与日志记录，真正的优雅关闭应由宿主显式 await <see cref="ShutdownAsync"/>。
    /// </summary>
    public void Dispose()
    {
        try
        {
            ShutdownAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception)
        {
            _logger.LogError(exception.GetBaseException(), "同步释放生命周期协调器时失败。");
        }
    }

    private async Task<ShutdownReport> ShutdownCoreAsync(CancellationToken externalToken)
    {
        _stopwatch.Start();
        var workReport = new List<BackgroundWorkOutcome>();
        var stepReport = new List<ShutdownStepOutcome>();
        var resourceReport = new List<ResourceDisposalOutcome>();

        List<TrackedWork> work;
        List<StopStep> steps;
        List<AsyncResource> resources;
        Exception? startupError;
        lock (_gate)
        {
            startupError = _startupError;
            if (_state is not (ApplicationLifecycleState.Stopping or ApplicationLifecycleState.Disposing or ApplicationLifecycleState.Stopped))
            {
                _state = ApplicationLifecycleState.Stopping;
            }

            work = _work.ToList();
            steps = _steps.ToList();
            _steps.Clear();
            resources = _resources.ToList();
            _resources.Clear();
        }

        RaiseStateChanged(ApplicationLifecycleState.Stopping);
        _logger.LogInformation("开始关闭：{WorkCount} 项后台工作，{StepCount} 个停止步骤，{ResourceCount} 项资源。",
            work.Count,
            steps.Count,
            resources.Count);

        // 1. 停止接收新工作并发出协作取消信号。
        TryCancel(_shutdownCts);

        // 2. 等待受管理工作结束，每项都受单项与总预算约束。
        foreach (var tracked in work)
        {
            var timeout = ResolveTimeout(tracked.Descriptor.ShutdownTimeout);
            var finished = await WaitAsync(tracked.Completion, timeout).ConfigureAwait(false);
            tracked.Duration = tracked.Stopwatch.Elapsed;
            var state = finished
                ? tracked.State
                : BackgroundWorkOutcomeState.TimedOut;
            if (!finished)
            {
                _logger.LogWarning("后台工作 {WorkId}（{Owner}）在 {Timeout} 内未结束，记录为超时。",
                    tracked.Descriptor.Id,
                    tracked.Descriptor.Owner,
                    timeout);
            }

            workReport.Add(new BackgroundWorkOutcome(
                tracked.Descriptor.Id,
                tracked.Descriptor.Owner,
                tracked.Descriptor.Kind,
                state,
                tracked.Duration,
                tracked.Error));

            if (!finished)
            {
                // 不无限等待不响应取消的工作，但要观察其后续异常。
                ObserveContinuation(tracked.Completion, tracked.Descriptor.Id);
            }
        }

        // 3. 按登记逆序执行模块级停止步骤。
        for (var index = steps.Count - 1; index >= 0; index--)
        {
            var step = steps[index];
            var timeout = ResolveTimeout(step.Timeout);
            using var stepCts = new CancellationTokenSource();
            if (timeout <= TimeSpan.Zero)
            {
                stepCts.Cancel();
            }
            else
            {
                stepCts.CancelAfter(timeout);
            }

            var watch = Stopwatch.StartNew();
            var task = RunStepAsync(step, stepCts.Token);
            var finished = await WaitAsync(task, timeout).ConfigureAwait(false);
            watch.Stop();
            if (!finished)
            {
                _logger.LogWarning("关闭步骤 {Step} 在 {Timeout} 内未结束，记录为超时。", step.Name, timeout);
                ObserveContinuation(task, step.Name);
                stepReport.Add(new ShutdownStepOutcome(step.Name, ShutdownStepState.TimedOut, watch.Elapsed, null));
            }
            else
            {
                var failure = task.IsCompletedSuccessfully ? task.Result : null;
                stepReport.Add(new ShutdownStepOutcome(
                    step.Name,
                    failure is null ? ShutdownStepState.Completed : ShutdownStepState.Faulted,
                    watch.Elapsed,
                    failure));
            }
        }

        // 4. 释放登记资源：后登记的资源通常依赖先登记的资源，因此逆序释放。
        lock (_gate)
        {
            _state = ApplicationLifecycleState.Disposing;
        }

        RaiseStateChanged(ApplicationLifecycleState.Disposing);
        for (var index = resources.Count - 1; index >= 0; index--)
        {
            var resource = resources[index];
            var timeout = ResolveTimeout(resource.Timeout);
            var watch = Stopwatch.StartNew();
            Task task;
            try
            {
                task = resource.DisposeAsync().AsTask();
            }
            catch (Exception exception)
            {
                task = Task.FromException(exception);
            }

            var finished = await WaitAsync(task, timeout).ConfigureAwait(false);
            watch.Stop();
            if (!finished)
            {
                _logger.LogWarning("资源 {Owner} 在 {Timeout} 内未释放，记录为超时。", resource.Owner, timeout);
                ObserveContinuation(task, resource.Owner);
                resourceReport.Add(new ResourceDisposalOutcome(resource.Owner, ResourceDisposalState.TimedOut, watch.Elapsed, null));
                continue;
            }

            resourceReport.Add(new ResourceDisposalOutcome(
                resource.Owner,
                task.IsFaulted ? ResourceDisposalState.Faulted : ResourceDisposalState.Disposed,
                watch.Elapsed,
                task.Exception?.GetBaseException().Message));
        }

        _stopwatch.Stop();
        lock (_gate)
        {
            _state = ApplicationLifecycleState.Stopped;
        }

        RaiseStateChanged(ApplicationLifecycleState.Stopped);
        var report = new ShutdownReport
        {
            Work = workReport,
            Steps = stepReport,
            Resources = resourceReport,
            Duration = _stopwatch.Elapsed,
            StartupError = startupError,
        };

        if (report.IsClean)
        {
            _logger.LogInformation("应用已优雅关闭，用时 {Duration}。", report.Duration);
        }
        else
        {
            _logger.LogWarning(
                "应用关闭完成但存在问题（{Count} 项）：{Issues}",
                report.Issues.Count,
                string.Join("；", report.Issues));
        }

        return report;
    }

    private async Task RunWorkAsync(TrackedWork tracked, Func<CancellationToken, Task> work)
    {
        try
        {
            await work(tracked.Cancellation.Token).ConfigureAwait(false);
            tracked.State = BackgroundWorkOutcomeState.Completed;
        }
        catch (OperationCanceledException) when (tracked.Cancellation.IsCancellationRequested)
        {
            tracked.State = BackgroundWorkOutcomeState.Canceled;
        }
        catch (Exception exception)
        {
            tracked.State = BackgroundWorkOutcomeState.Faulted;
            tracked.Error = exception.Message;
            _logger.LogError(exception, "后台工作 {WorkId}（{Owner}）失败。", tracked.Descriptor.Id, tracked.Descriptor.Owner);
        }
        finally
        {
            tracked.Duration = tracked.Stopwatch.Elapsed;
        }
    }

    private async Task ObserveTaskAsync(TrackedWork tracked, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            tracked.State = BackgroundWorkOutcomeState.Completed;
        }
        catch (OperationCanceledException)
        {
            tracked.State = BackgroundWorkOutcomeState.Canceled;
        }
        catch (Exception exception)
        {
            tracked.State = BackgroundWorkOutcomeState.Faulted;
            tracked.Error = exception.Message;
            _logger.LogError(exception, "后台工作 {WorkId}（{Owner}）失败。", tracked.Descriptor.Id, tracked.Descriptor.Owner);
        }
        finally
        {
            tracked.Duration = tracked.Stopwatch.Elapsed;
        }
    }

    private async Task<string?> RunStepAsync(StopStep step, CancellationToken token)
    {
        try
        {
            await step.Action(token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 超时触发的协作取消由外层统一记录为超时。
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "关闭步骤 {Step} 失败。", step.Name);
            return exception.Message;
        }
    }

    private TimeSpan ResolveTimeout(TimeSpan? specific)
    {
        var remaining = _options.TotalShutdownTimeout - _stopwatch.Elapsed;
        var requested = specific ?? _options.DefaultShutdownTimeout;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return requested < remaining ? requested : remaining;
    }

    private static async Task<bool> WaitAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        var delay = Task.Delay(timeout);
        return await Task.WhenAny(task, delay).ConfigureAwait(false) == task;
    }

    private void ObserveContinuation(Task task, string name)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    _logger.LogError(completed.Exception?.GetBaseException(), "{Name} 在关闭超时后仍然失败。", name);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
            // 取消回调抛出的异常不能阻止其余清理。
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool AcceptsNewWork() =>
        _state is ApplicationLifecycleState.Starting or ApplicationLifecycleState.Running;

    private void RaiseStateChanged(ApplicationLifecycleState state) =>
        StateChanged?.Invoke(this, state);

    private static BackgroundWorkHandle Rejected(string id) =>
        new(id, Task.CompletedTask, CancellationToken.None, rejected: true);

    private sealed class TrackedWork
    {
        public TrackedWork(BackgroundWorkDescriptor descriptor, CancellationTokenSource cancellation, Stopwatch stopwatch)
        {
            Descriptor = descriptor;
            Cancellation = cancellation;
            Stopwatch = stopwatch;
            Completion = Task.CompletedTask;
        }

        public BackgroundWorkDescriptor Descriptor { get; }

        public CancellationTokenSource Cancellation { get; }

        public Stopwatch Stopwatch { get; }

        public Task Completion { get; set; }

        public BackgroundWorkOutcomeState State { get; set; } = BackgroundWorkOutcomeState.Canceled;

        public TimeSpan Duration { get; set; }

        public string? Error { get; set; }
    }

    private sealed record StopStep(string Name, Func<CancellationToken, Task> Action, TimeSpan Timeout);

    private sealed record AsyncResource(string Owner, Func<ValueTask> DisposeAsync, TimeSpan Timeout);
}
