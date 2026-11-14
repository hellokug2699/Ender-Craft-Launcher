// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch;

/// <summary>启动尝试的最终状态；Running 表示已经开始、尚未记录结束（home.md §3.4）。</summary>
public enum LaunchAttemptState
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>一次启动中某个步骤的结果快照。</summary>
public sealed record LaunchStatusStep
{
    public required string StepId { get; init; }

    public required LaunchStepStatus Status { get; init; }

    public string Message { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }
}

/// <summary>
/// 某个实例“最近一次启动”的持久化状态（home.md §3.4）：
/// 启动状态按实例归属，ECL 关闭后不丢失，直到该实例下一次启动开始时被重置。
/// 这不是游戏日志会话（ADR 0015）的替代品：会话负责证据，本快照只负责主页启动卡呈现。
/// </summary>
public sealed record LaunchStatusSnapshot
{
    public required string InstanceId { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>为空表示本次尝试尚未结束（正在启动，或 ECL 在启动途中退出）。</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    public LaunchAttemptState State { get; init; } = LaunchAttemptState.Running;

    public bool DryRun { get; init; }

    /// <summary>本次尝试产生的游戏日志会话；为空表示没有可追溯的会话。</summary>
    public string? SessionId { get; init; }

    /// <summary>失败时第一个失败步骤的原因，供启动卡与错误日志入口显示上下文。</summary>
    public string? FailureSummary { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<LaunchStatusStep> Steps { get; init; } = [];
}

/// <summary>启动进行中的步骤进度事件（仅用于实时呈现，不持久化）。</summary>
public enum LaunchStepProgressPhase
{
    Started,
    Completed,
}

public sealed record LaunchStepProgress
{
    public required string StepId { get; init; }

    public required LaunchStepProgressPhase Phase { get; init; }

    public int Index { get; init; }

    public int Total { get; init; }

    /// <summary>Completed 时的真实结果；Started 时为 null。</summary>
    public LaunchStepStatus? Status { get; init; }

    public string? Message { get; init; }

    public TimeSpan Duration { get; init; }
}

/// <summary>
/// 最近一次启动状态的读写入口。写入失败不得影响启动，读取失败按“没有记录”处理。
/// Dry Run 不是启动，不写入本存储（避免把预览当成真实启动状态）。
/// </summary>
public interface ILaunchStatusStore
{
    LaunchStatusSnapshot? Get(string instanceId);

    /// <summary>记录一次启动开始：推进 StartedAt 并清除上一次的会话/结果。</summary>
    void RecordStarted(string instanceId);

    /// <summary>记录启动结束；只覆盖同一实例，不触碰其它实例。</summary>
    void RecordCompleted(string instanceId, LaunchReport report);
}

/// <summary>
/// 文件实现：每个实例一个 JSON，位于启动器数据目录的 <c>logs/launch-status</c> 下。
/// 放在数据目录而不是实例目录：它是启动器的呈现记录，不是实例配置，也不写入实例 JSON。
/// </summary>
public sealed class LaunchStatusStore : ILaunchStatusStore
{
    private const string DirectoryName = "launch-status";

    private readonly IAppPaths _paths;
    private readonly ILogger<LaunchStatusStore> _logger;

    public LaunchStatusStore(IAppPaths paths, ILogger<LaunchStatusStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _logger = logger;
    }

    public LaunchStatusSnapshot? Get(string instanceId)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return null;
        }

        var path = GetPath(instanceId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<LaunchStatusSnapshot>(json, EclJson.Default);
            return snapshot is null || !string.Equals(snapshot.InstanceId, instanceId, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 记录损坏时按“没有记录”呈现；下一次启动开始会重建文件。
            _logger.LogWarning(exception, "无法读取实例启动状态，按无记录处理：{InstanceId}", instanceId);
            return null;
        }
    }

    public void RecordStarted(string instanceId)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return;
        }

        Write(new LaunchStatusSnapshot
        {
            InstanceId = instanceId,
            StartedAtUtc = DateTimeOffset.UtcNow,
            State = LaunchAttemptState.Running,
        });
    }

    public void RecordCompleted(string instanceId, LaunchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!AppPaths.IsValidInstanceId(instanceId) || report.DryRun)
        {
            return;
        }

        var completedAt = DateTimeOffset.UtcNow;
        Write(new LaunchStatusSnapshot
        {
            InstanceId = instanceId,
            StartedAtUtc = completedAt - report.Duration,
            CompletedAtUtc = completedAt,
            State = report.Success ? LaunchAttemptState.Succeeded : LaunchAttemptState.Failed,
            DryRun = report.DryRun,
            SessionId = string.IsNullOrWhiteSpace(report.SessionId) ? null : report.SessionId,
            FailureSummary = report.Success
                ? null
                : report.Steps.FirstOrDefault(step => step.Status == LaunchStepStatus.Failed)?.Message,
            Duration = report.Duration,
            Steps = report.Steps
                .Select(step => new LaunchStatusStep
                {
                    StepId = step.StepId,
                    Status = step.Status,
                    Message = step.Message,
                    Duration = step.Duration,
                })
                .ToArray(),
        });
    }

    private string GetPath(string instanceId) =>
        Path.Combine(_paths.LogsDirectory, DirectoryName, $"{instanceId}.json");

    private void Write(LaunchStatusSnapshot snapshot)
    {
        var path = GetPath(snapshot.InstanceId);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, EclJson.Indented));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 启动状态记录失败只影响下次打开主页时能否回看，不影响启动本身。
            _logger.LogWarning(exception, "无法保存实例启动状态：{InstanceId}", snapshot.InstanceId);
        }
    }
}
