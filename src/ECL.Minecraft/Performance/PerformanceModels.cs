// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Engine;

namespace Ecl.Minecraft.Performance;

/// <summary>
/// 卡死判定阶段（ADR 0041 §3）。启动、正常、退化、短暂 Stall、疑似 Hang 与退出必须分开，
/// 不能以“进程仍存在”作为唯一健康判断。
/// </summary>
public enum HangPhase
{
    NotStarted,
    Starting,
    Running,
    Degraded,
    Stalled,
    SuspectedHang,
    Exited,
}

/// <summary>
/// 一次性能观测。无法可靠取得的指标保持 <c>null</c>，不得用猜测值填充。
/// </summary>
public sealed record PerformanceObservation(
    DateTimeOffset Timestamp,
    bool ProcessAlive,
    double? CpuPercent = null,
    long? WorkingSetBytes = null,
    long? PrivateBytes = null,
    long? TickCount = null,
    double? Mspt = null,
    double? Tps = null,
    bool? WindowResponsive = null);

public sealed record HangThresholds(
    TimeSpan StartupGrace,
    double DegradedMspt,
    double StallMspt,
    TimeSpan SuspectedHangAfter,
    int ConsecutiveSamples)
{
    public static HangThresholds Default { get; } = new(
        StartupGrace: TimeSpan.FromMinutes(2),
        DegradedMspt: 50,
        StallMspt: 200,
        SuspectedHangAfter: TimeSpan.FromSeconds(30),
        ConsecutiveSamples: 3);
}

public sealed record HangAssessment(
    HangPhase Phase,
    string Reason,
    IReadOnlyList<string> Evidence,
    bool RequiresAttention,
    EvidenceConfidence EvidenceConfidence);

public sealed record PerformanceSample(
    DateTimeOffset Timestamp,
    bool ProcessAlive,
    double? CpuPercent,
    long? WorkingSetBytes,
    long? PrivateBytes,
    long? TickCount,
    double? Mspt,
    double? Tps,
    HangPhase Phase,
    string Reason);

public sealed record PerformanceMonitorSnapshot(
    string InstanceId,
    int ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastSampleAt,
    HangPhase Phase,
    string Reason,
    PerformanceSample? LastSample,
    string? SampleLogPath,
    string? Warning);

public interface IPerformanceMonitorService
{
    PerformanceMonitorSnapshot Start(string instanceId, int processId, string gameDirectory);

    PerformanceMonitorSnapshot? GetSnapshot(string instanceId);

    IReadOnlyList<PerformanceMonitorSnapshot> List();

    void Stop(string instanceId);
}

/// <summary>可替换的 profiler backend 契约。Core 不实现 JVM profiler，也不把第三方实现细节扩散到启动流程。</summary>
public interface IProfilerBackend
{
    string Id { get; }

    string DisplayName { get; }

    bool IsAvailable { get; }

    Task<Result<ProfilerSession>> StartAsync(
        string instanceId,
        ProfilerStartOptions options,
        CancellationToken cancellationToken = default);

    Task<Result> StopAsync(string sessionId, CancellationToken cancellationToken = default);
}

public sealed record ProfilerStartOptions(
    string GameDirectory,
    int? ProcessId,
    bool CaptureSlowTicksOnly = true,
    double SlowTickMspt = 50);

public sealed record ProfilerSession(
    string SessionId,
    string BackendId,
    string InstanceId,
    DateTimeOffset StartedAt,
    string? OutputPath);

public sealed class ProfilerSessionManager(IEnumerable<IProfilerBackend> backends)
{
    private readonly IReadOnlyDictionary<string, IProfilerBackend> _backends =
        backends.ToDictionary(backend => backend.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IProfilerBackend> Backends => _backends.Values.ToArray();

    public Task<Result<ProfilerSession>> StartAsync(
        string backendId,
        string instanceId,
        ProfilerStartOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!_backends.TryGetValue(backendId, out var backend))
        {
            return Task.FromResult(Result<ProfilerSession>.Fail(
                "profiler.unknown_backend",
                $"未知的 profiler backend：{backendId}"));
        }

        if (!backend.IsAvailable)
        {
            return Task.FromResult(Result<ProfilerSession>.Fail(
                "profiler.unavailable",
                $"profiler backend {backend.DisplayName} 当前不可用；基础性能监控不受影响。"));
        }

        return backend.StartAsync(instanceId, options, cancellationToken);
    }
}
