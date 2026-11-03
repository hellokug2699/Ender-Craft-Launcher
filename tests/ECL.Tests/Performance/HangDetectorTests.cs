// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Engine;
using Ecl.Minecraft.Performance;
using Xunit;

namespace Ecl.Tests.Performance;

public sealed class HangDetectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DistinguishesStartupFromRunning()
    {
        var detector = new HangDetector();
        var startup = detector.Observe(new PerformanceObservation(Start, true));
        Assert.Equal(HangPhase.Starting, startup.Phase);

        var running = detector.Observe(new PerformanceObservation(
            Start.AddSeconds(10),
            true,
            TickCount: 100,
            Mspt: 12,
            Tps: 20));
        Assert.Equal(HangPhase.Running, running.Phase);
        Assert.Equal(EvidenceConfidence.Confirmed, running.EvidenceConfidence);
    }

    [Fact]
    public void SingleStallIsNotReportedAsStalled()
    {
        var detector = new HangDetector();
        detector.Observe(new PerformanceObservation(Start, true));
        var first = detector.Observe(new PerformanceObservation(Start.AddSeconds(5), true, TickCount: 1, Mspt: 300));
        Assert.Equal(HangPhase.Running, first.Phase);
        Assert.False(first.RequiresAttention);

        var second = detector.Observe(new PerformanceObservation(Start.AddSeconds(10), true, TickCount: 2, Mspt: 300));
        Assert.Equal(HangPhase.Running, second.Phase);

        var third = detector.Observe(new PerformanceObservation(Start.AddSeconds(15), true, TickCount: 3, Mspt: 300));
        Assert.Equal(HangPhase.Stalled, third.Phase);
        Assert.True(third.RequiresAttention);
    }

    [Fact]
    public void DegradedWhenTickAdvancesButMsptStaysHigh()
    {
        var detector = new HangDetector();
        detector.Observe(new PerformanceObservation(Start, true));
        var assessment = detector.Observe(new PerformanceObservation(
            Start.AddSeconds(5),
            true,
            TickCount: 10,
            Mspt: 80,
            Tps: 12));
        Assert.Equal(HangPhase.Degraded, assessment.Phase);
    }

    [Fact]
    public void SuspectedHangWhenTickStopsAdvancing()
    {
        var detector = new HangDetector(new HangThresholds(
            TimeSpan.FromMinutes(2),
            50,
            200,
            TimeSpan.FromSeconds(30),
            3));
        detector.Observe(new PerformanceObservation(Start, true, TickCount: 10, Mspt: 10));
        detector.Observe(new PerformanceObservation(Start.AddSeconds(10), true, TickCount: 10, Mspt: 10));
        Assert.Equal(HangPhase.Running, detector.Phase);

        var hang = detector.Observe(new PerformanceObservation(Start.AddSeconds(45), true, TickCount: 10, Mspt: 10));
        Assert.Equal(HangPhase.SuspectedHang, hang.Phase);
        Assert.True(hang.RequiresAttention);

        var recovered = detector.Observe(new PerformanceObservation(Start.AddSeconds(50), true, TickCount: 11, Mspt: 10));
        Assert.Equal(HangPhase.Running, recovered.Phase);
        Assert.Contains(recovered.Evidence, item => item.Contains("恢复"));
    }

    [Fact]
    public void ExitedWhenProcessIsGone()
    {
        var detector = new HangDetector();
        detector.Observe(new PerformanceObservation(Start, true, TickCount: 1, Mspt: 10));
        var exited = detector.Observe(new PerformanceObservation(Start.AddSeconds(5), false));
        Assert.Equal(HangPhase.Exited, exited.Phase);
        Assert.False(exited.RequiresAttention);
    }

    [Fact]
    public void MissingTickTelemetryIsPartialNotHealthy()
    {
        var detector = new HangDetector();
        detector.Observe(new PerformanceObservation(Start, true));
        var assessment = detector.Observe(new PerformanceObservation(
            Start.AddMinutes(3),
            true,
            CpuPercent: 12,
            WorkingSetBytes: 1024));
        Assert.Equal(HangPhase.Running, assessment.Phase);
        Assert.Equal(EvidenceConfidence.Partial, assessment.EvidenceConfidence);
        Assert.Contains("不能据此声称性能健康", assessment.Evidence[0]);
    }
}

public sealed class ProfilerBackendTests
{
    [Fact]
    public async Task UnavailableBackendKeepsBasicMonitoringIndependent()
    {
        var manager = new ProfilerSessionManager([new UnavailableBackend()]);
        var result = await manager.StartAsync(
            "unavailable",
            "instance",
            new ProfilerStartOptions("dir", null));

        Assert.False(result.IsSuccess);
        Assert.Equal("profiler.unavailable", result.Error?.Code);
    }

    [Fact]
    public async Task UnknownBackendIsRejected()
    {
        var manager = new ProfilerSessionManager([]);
        var result = await manager.StartAsync(
            "spark",
            "instance",
            new ProfilerStartOptions("dir", null));
        Assert.False(result.IsSuccess);
        Assert.Equal("profiler.unknown_backend", result.Error?.Code);
    }

    private sealed class UnavailableBackend : IProfilerBackend
    {
        public string Id => "unavailable";

        public string DisplayName => "Unavailable";

        public bool IsAvailable => false;

        public Task<Result<ProfilerSession>> StartAsync(
            string instanceId,
            ProfilerStartOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ProfilerSession>.Fail("profiler.unavailable", "unavailable"));

        public Task<Result> StopAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Ok());
    }
}
