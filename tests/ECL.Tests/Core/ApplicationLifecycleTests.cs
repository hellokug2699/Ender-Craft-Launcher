// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Runtime;
using Xunit;

namespace Ecl.Tests.Core;

/// <summary>ADR 0039：应用级生命周期与优雅关闭的可观察契约。</summary>
public sealed class ApplicationLifecycleTests
{
    private static ApplicationLifecycleOptions FastOptions(
        int defaultMs = 2_000,
        int totalMs = 5_000) => new()
        {
            DefaultShutdownTimeout = TimeSpan.FromMilliseconds(defaultMs),
            TotalShutdownTimeout = TimeSpan.FromMilliseconds(totalMs),
        };

    [Fact]
    public async Task ShutdownRunsStagesInOrderAndCancelsManagedWork()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastOptions());
        var states = new List<ApplicationLifecycleState>();
        lifecycle.StateChanged += (_, state) => states.Add(state);
        lifecycle.BeginStart();
        lifecycle.MarkRunning();

        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = lifecycle.TrackWork(
            new BackgroundWorkDescriptor("watcher", "tests", BackgroundWorkKind.Monitor),
            async token =>
            {
                workStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            });
        await workStarted.Task;

        var report = await lifecycle.ShutdownAsync();

        Assert.True(handle.Completion.IsCompleted);
        Assert.Equal(BackgroundWorkOutcomeState.Canceled, Assert.Single(report.Work).State);
        Assert.True(report.IsClean);
        Assert.Equal(ApplicationLifecycleState.Stopped, lifecycle.State);
        Assert.Equal(
            [
                ApplicationLifecycleState.Starting,
                ApplicationLifecycleState.Running,
                ApplicationLifecycleState.Stopping,
                ApplicationLifecycleState.Disposing,
                ApplicationLifecycleState.Stopped,
            ],
            states);
    }

    [Fact]
    public async Task ShutdownIsIdempotentAndRunsStepsOnce()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastOptions());
        lifecycle.BeginStart();
        var steps = 0;
        lifecycle.RegisterStopStep("flush", _ =>
        {
            steps++;
            return Task.CompletedTask;
        });

        var first = await lifecycle.ShutdownAsync();
        var second = await lifecycle.ShutdownAsync();

        Assert.Same(first, second);
        Assert.Equal(1, steps);
    }

    [Fact]
    public async Task WorkIgnoringCancellationIsReportedAsTimeoutAndShutdownStillCompletes()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastOptions(defaultMs: 150, totalMs: 600));
        lifecycle.BeginStart();
        lifecycle.MarkRunning();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lifecycle.TrackWork(
                new BackgroundWorkDescriptor("stuck", "tests", BackgroundWorkKind.Io),
                _ => release.Task);

            var report = await lifecycle.ShutdownAsync();

            Assert.True(report.TimedOut);
            Assert.False(report.IsClean);
            Assert.Equal(BackgroundWorkOutcomeState.TimedOut, Assert.Single(report.Work).State);
            Assert.Contains(report.Issues, issue => issue.Contains("stuck", StringComparison.Ordinal));
            Assert.Equal(ApplicationLifecycleState.Stopped, lifecycle.State);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task StopStepsRunInReverseRegistrationOrderAndFailuresDoNotSkipOthers()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastOptions());
        lifecycle.BeginStart();
        var order = new List<string>();
        lifecycle.RegisterStopStep("dependency", _ =>
        {
            order.Add("dependency");
            return Task.CompletedTask;
        });
        lifecycle.RegisterStopStep("consumer", _ =>
        {
            order.Add("consumer");
            throw new InvalidOperationException("step boom");
        });

        var report = await lifecycle.ShutdownAsync();

        Assert.Equal(["consumer", "dependency"], order);
        Assert.Equal(2, report.Steps.Count);
        Assert.Equal(ShutdownStepState.Faulted, report.Steps[0].State);
        Assert.Contains("step boom", report.Steps[0].Error);
        Assert.Equal(ShutdownStepState.Completed, report.Steps[1].State);
        Assert.True(report.HasFailures);
    }

    [Fact]
    public async Task ResourcesAreDisposedInReverseRegistrationOrder()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastOptions());
        lifecycle.BeginStart();
        var order = new List<string>();
        lifecycle.RegisterResource("first", () =>
        {
            order.Add("first");
            return ValueTask.CompletedTask;
        });
        lifecycle.RegisterResource("second", () =>
        {
            order.Add("second");
            return ValueTask.CompletedTask;
        });

        var report = await lifecycle.ShutdownAsync();

        Assert.Equal(["second", "first"], order);
        Assert.All(report.Resources, outcome => Assert.Equal(ResourceDisposalState.Disposed, outcome.State));
    }

    [Fact]
    public async Task StartupFailurePreservesOriginalExceptionAndStillRunsCleanup()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastOptions());
        lifecycle.BeginStart();
        var cleaned = false;
        lifecycle.RegisterStopStep("cleanup", _ =>
        {
            cleaned = true;
            return Task.CompletedTask;
        });
        var startupError = new InvalidOperationException("startup boom");

        var report = await lifecycle.FailStartupAsync(startupError);

        Assert.True(cleaned);
        Assert.Same(startupError, report.StartupError);
        Assert.False(report.IsClean);
        Assert.Contains(report.Issues, issue => issue.Contains("startup boom", StringComparison.Ordinal));
        Assert.Equal(ApplicationLifecycleState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task FaultedWorkIsObservedAndRecorded()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastOptions());
        lifecycle.BeginStart();
        lifecycle.MarkRunning();
        var handle = lifecycle.TrackWork(
            new BackgroundWorkDescriptor("broken", "tests", BackgroundWorkKind.Queue),
            _ => throw new InvalidOperationException("work boom"));

        var report = await lifecycle.ShutdownAsync();

        await handle.Completion;
        var outcome = Assert.Single(report.Work);
        Assert.Equal(BackgroundWorkOutcomeState.Faulted, outcome.State);
        Assert.Contains("work boom", outcome.Error);
    }

    [Fact]
    public async Task RegistrationIsRejectedAfterShutdown()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastOptions());
        lifecycle.BeginStart();
        await lifecycle.ShutdownAsync();

        var handle = lifecycle.TrackWork(
            new BackgroundWorkDescriptor("late", "tests"),
            _ => Task.CompletedTask);

        Assert.True(handle.IsRejected);
        Assert.Throws<InvalidOperationException>(() => lifecycle.RegisterStopStep("late-step", _ => Task.CompletedTask));
        Assert.Throws<InvalidOperationException>(() => lifecycle.RegisterResource("late-resource", () => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task CreatedLifecycleShutsDownWithoutRunningStepsTwice()
    {
        using var lifecycle = new ApplicationLifecycleCoordinator(FastOptions());
        var report = await lifecycle.ShutdownAsync();

        Assert.Empty(report.Work);
        Assert.Empty(report.Steps);
        Assert.Empty(report.Resources);
        Assert.Equal(ApplicationLifecycleState.Stopped, lifecycle.State);
    }
}
