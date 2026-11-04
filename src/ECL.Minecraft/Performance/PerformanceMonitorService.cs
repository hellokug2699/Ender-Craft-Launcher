// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Performance;

public sealed record ProcessMetrics(
    DateTimeOffset Timestamp,
    bool ProcessAlive,
    double? CpuPercent,
    long? WorkingSetBytes,
    long? PrivateBytes,
    bool? WindowResponsive);

/// <summary>进程外基础监控抽象；测试可替换为确定性探针。</summary>
public interface IProcessMetricsProbe
{
    ProcessMetrics Sample(int processId);
}

public sealed class ProcessMetricsProbe : IProcessMetricsProbe
{
    private readonly ConcurrentDictionary<int, (DateTimeOffset At, TimeSpan Cpu)> _previous = new();

    public ProcessMetrics Sample(int processId)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Refresh();
            if (process.HasExited)
            {
                return new ProcessMetrics(now, false, null, null, null, null);
            }

            var cpu = process.TotalProcessorTime;
            double? percent = null;
            if (_previous.TryGetValue(processId, out var previous))
            {
                var wall = now - previous.At;
                var used = cpu - previous.Cpu;
                if (wall.TotalMilliseconds > 0 && used >= TimeSpan.Zero)
                {
                    percent = used.TotalMilliseconds /
                              wall.TotalMilliseconds /
                              Math.Max(1, Environment.ProcessorCount) *
                              100;
                }
            }

            _previous[processId] = (now, cpu);
            bool? responding = null;
            try
            {
                responding = process.Responding;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                responding = null;
            }

            return new ProcessMetrics(
                now,
                true,
                percent,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                responding);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or NotSupportedException)
        {
            return new ProcessMetrics(now, false, null, null, null, null);
        }
    }
}

public sealed record GameTickTelemetry(
    DateTimeOffset Timestamp,
    long? TickCount,
    double? Mspt,
    double? Tps);

/// <summary>游戏侧 telemetry 可选；不存在时必须保持 null，不能猜测 TPS/MSPT。</summary>
public interface IGameTelemetryProbe
{
    GameTickTelemetry? Read(string gameDirectory);
}

public sealed class JsonLinesGameTelemetryProbe : IGameTelemetryProbe
{
    private const int MaxTailBytes = 64 * 1024;

    public GameTickTelemetry? Read(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, ".ecl", "telemetry", "tick.jsonl");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, MaxTailBytes);
            stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (last is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(last);
            var root = document.RootElement;
            var timestamp = root.TryGetProperty("timestamp", out var timeElement) &&
                            timeElement.TryGetDateTimeOffset(out var timeValue)
                ? timeValue
                : DateTimeOffset.MinValue;
            return new GameTickTelemetry(
                timestamp,
                root.TryGetProperty("tick", out var tickElement) && tickElement.TryGetInt64(out var tick)
                    ? tick
                    : null,
                root.TryGetProperty("mspt", out var msptElement) && msptElement.TryGetDouble(out var mspt)
                    ? mspt
                    : null,
                root.TryGetProperty("tps", out var tpsElement) && tpsElement.TryGetDouble(out var tps)
                    ? tps
                    : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 低开销持续性能监控（ADR 0041 §2/§3）。默认从进程外读取 CPU/内存/Tick 证据；
/// 游戏侧数据不存在时明确标记不可用，不填充猜测值。样本只写本地日志。
/// </summary>
public sealed class PerformanceMonitorService : IPerformanceMonitorService, IDisposable
{
    private readonly IAppPaths _paths;
    private readonly IProcessMetricsProbe _processProbe;
    private readonly IGameTelemetryProbe _telemetryProbe;
    private readonly ILogger<PerformanceMonitorService> _logger;
    private readonly ConcurrentDictionary<string, MonitoredInstance> _monitors = new(StringComparer.Ordinal);
    private readonly TimeSpan _sampleInterval;

    public PerformanceMonitorService(
        IAppPaths paths,
        IProcessMetricsProbe processProbe,
        IGameTelemetryProbe telemetryProbe,
        ILogger<PerformanceMonitorService>? logger = null)
    {
        _paths = paths;
        _processProbe = processProbe;
        _telemetryProbe = telemetryProbe;
        _logger = logger ?? NullLogger<PerformanceMonitorService>.Instance;
        _sampleInterval = TimeSpan.FromSeconds(5);
    }

    public PerformanceMonitorSnapshot Start(string instanceId, int processId, string gameDirectory)
    {
        Stop(instanceId);
        var startedAt = DateTimeOffset.UtcNow;
        var sessionId = $"{startedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..32];
        var logPath = Path.Combine(_paths.LogsDirectory, "performance", $"{instanceId}-{sessionId}.jsonl");
        var monitor = new MonitoredInstance(
            instanceId,
            processId,
            gameDirectory,
            startedAt,
            logPath,
            new HangDetector());
        _monitors[instanceId] = monitor;
        monitor.Timer = new Timer(_ => Sample(monitor), null, TimeSpan.Zero, _sampleInterval);
        _logger.LogInformation(
            "开始监控实例 {InstanceId} 的进程 {ProcessId}，样本日志：{Path}",
            instanceId,
            processId,
            logPath);
        return Snapshot(monitor);
    }

    public PerformanceMonitorSnapshot? GetSnapshot(string instanceId) =>
        _monitors.TryGetValue(instanceId, out var monitor) ? Snapshot(monitor) : null;

    public IReadOnlyList<PerformanceMonitorSnapshot> List() =>
        _monitors.Values.Select(Snapshot).ToArray();

    public void Stop(string instanceId)
    {
        if (_monitors.TryRemove(instanceId, out var monitor))
        {
            monitor.Timer?.Dispose();
            monitor.Timer = null;
        }
    }

    public void Dispose()
    {
        foreach (var key in _monitors.Keys)
        {
            Stop(key);
        }
    }

    private void Sample(MonitoredInstance monitor)
    {
        if (Interlocked.Exchange(ref monitor.Sampling, 1) == 1)
        {
            return;
        }

        try
        {
            var metrics = _processProbe.Sample(monitor.ProcessId);
            var telemetry = _telemetryProbe.Read(monitor.GameDirectory);
            var observation = new PerformanceObservation(
                metrics.Timestamp,
                metrics.ProcessAlive,
                metrics.CpuPercent,
                metrics.WorkingSetBytes,
                metrics.PrivateBytes,
                telemetry?.TickCount,
                telemetry?.Mspt,
                telemetry?.Tps,
                metrics.WindowResponsive);
            var assessment = monitor.Detector.Observe(observation);
            var sample = new PerformanceSample(
                metrics.Timestamp,
                metrics.ProcessAlive,
                metrics.CpuPercent,
                metrics.WorkingSetBytes,
                metrics.PrivateBytes,
                telemetry?.TickCount,
                telemetry?.Mspt,
                telemetry?.Tps,
                assessment.Phase,
                assessment.Reason);
            monitor.LastSample = sample;
            monitor.Phase = assessment.Phase;
            monitor.Reason = assessment.Reason;
            monitor.Warning = telemetry is null
                ? "未提供游戏侧 Tick telemetry；TPS/MSPT 标记为不可用。"
                : null;
            AppendSample(monitor, sample);

            if (!metrics.ProcessAlive)
            {
                monitor.Timer?.Dispose();
                monitor.Timer = null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            monitor.Reason = $"性能采样失败：{exception.Message}";
            _logger.LogWarning(exception, "实例 {InstanceId} 性能采样失败。", monitor.InstanceId);
        }
        finally
        {
            Interlocked.Exchange(ref monitor.Sampling, 0);
        }
    }

    private static void AppendSample(MonitoredInstance monitor, PerformanceSample sample)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(monitor.LogPath)!);
            File.AppendAllText(
                monitor.LogPath,
                JsonSerializer.Serialize(sample, EclJson.Default) + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 样本日志失败不影响游戏进程。
        }
    }

    private static PerformanceMonitorSnapshot Snapshot(MonitoredInstance monitor) =>
        new(
            monitor.InstanceId,
            monitor.ProcessId,
            monitor.StartedAt,
            monitor.LastSample?.Timestamp,
            monitor.Phase,
            monitor.Reason,
            monitor.LastSample,
            monitor.LogPath,
            monitor.Warning);

    private sealed class MonitoredInstance(
        string instanceId,
        int processId,
        string gameDirectory,
        DateTimeOffset startedAt,
        string logPath,
        HangDetector detector)
    {
        public string InstanceId { get; } = instanceId;

        public int ProcessId { get; } = processId;

        public string GameDirectory { get; } = gameDirectory;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public string LogPath { get; } = logPath;

        public HangDetector Detector { get; } = detector;

        public Timer? Timer { get; set; }

        public PerformanceSample? LastSample { get; set; }

        public HangPhase Phase { get; set; } = HangPhase.NotStarted;

        public string Reason { get; set; } = "尚未采样。";

        public string? Warning { get; set; }

        public int Sampling;
    }
}
