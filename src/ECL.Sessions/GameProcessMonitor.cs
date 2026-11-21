// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ecl.Sessions;

/// <summary>
/// 仍在运行的 Minecraft 游戏进程快照。数据全部来自会话 manifest + 进程探针，
/// UI 不自行扫描目录或推断归属；实例名以调用方解析的用户设定名称为准。
/// </summary>
public sealed record RunningGameSnapshot(
    string SessionId,
    string? InstanceId,
    string? InstanceName,
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    bool RecoveredAfterRestart);

/// <summary>进程探针结果：PID 仍在且能读出启动时间。</summary>
public sealed record GameProcessInfo(int ProcessId, DateTimeOffset StartedAtUtc);

public interface IGameProcessMonitor
{
    /// <summary>列出仍存活且身份匹配的游戏进程；没有时返回空集合。</summary>
    IReadOnlyList<RunningGameSnapshot> Scan();

    /// <summary>强制终止指定会话的游戏进程；成功后把仍在运行的会话标记为 Interrupted。</summary>
    bool TryTerminate(string sessionId, out string? error);
}

/// <summary>
/// 运行中游戏监视器（显式需求：全局强制终止 + 重启后扫描遗留进程）。
///
/// 身份校验只用两个来源：会话 manifest 记录的 PID/启动时间，以及操作系统进程的启动时间；
/// 启动时间相差超过 <see cref="StartTimeTolerance"/> 视为 PID 复用，不误报。
/// 终止只结束游戏进程，不结束独立日志宿主；会话状态由后续采集收尾或本类的标记继续推进。
/// </summary>
public sealed class GameProcessMonitor : IGameProcessMonitor
{
    /// <summary>允许的启动时间误差（进程启动与 manifest 记录之间的正常抖动）。</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromMinutes(5);

    private readonly IGameSessionService _sessions;
    private readonly ILogger<GameProcessMonitor>? _logger;
    private readonly Func<int, GameProcessInfo?> _probe;
    private readonly Func<int, bool> _kill;

    public GameProcessMonitor(
        IGameSessionService sessions,
        ILogger<GameProcessMonitor>? logger = null,
        Func<int, GameProcessInfo?>? probe = null,
        Func<int, bool>? kill = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _logger = logger;
        _probe = probe ?? ProbeProcess;
        _kill = kill ?? KillProcess;
    }

    public IReadOnlyList<RunningGameSnapshot> Scan()
    {
        var result = new List<RunningGameSnapshot>();
        foreach (var session in _sessions.List())
        {
            var manifest = session.Manifest;
            if (manifest.ProcessId is not { } processId || !IsLiveState(manifest.State))
            {
                continue;
            }

            var info = _probe(processId);
            if (info is null)
            {
                continue;
            }

            if (manifest.ProcessStartedAtUtc is { } expected &&
                (info.StartedAtUtc - expected).Duration() > StartTimeTolerance)
            {
                _logger?.LogDebug(
                    "会话 {SessionId} 的 PID {Pid} 启动时间不匹配，按 PID 复用忽略。",
                    manifest.SessionId,
                    processId);
                continue;
            }

            var recovered = manifest.State == GameSessionState.ProcessStateUnknown ||
                            manifest.Warnings.Contains(
                                SessionRecoveryWarnings.Reattached,
                                StringComparer.Ordinal);
            result.Add(new RunningGameSnapshot(
                manifest.SessionId,
                manifest.InstanceId,
                manifest.InstanceName,
                processId,
                info.StartedAtUtc,
                recovered));
        }

        return result.OrderBy(snapshot => snapshot.StartedAtUtc).ToArray();
    }

    public bool TryTerminate(string sessionId, out string? error)
    {
        error = null;
        var session = _sessions.Get(sessionId);
        if (session is null)
        {
            error = "会话不存在。";
            return false;
        }

        if (session.Manifest.ProcessId is not { } processId)
        {
            error = "会话没有记录游戏进程 ID。";
            return false;
        }

        if (!_kill(processId))
        {
            error = "游戏进程已退出或无法终止。";
            return false;
        }

        try
        {
            var current = _sessions.Get(sessionId)?.Manifest.State;
            if (current is GameSessionState.Starting
                or GameSessionState.Running
                or GameSessionState.GameExitedDraining
                or GameSessionState.ProcessStateUnknown)
            {
                _sessions.MarkState(sessionId, GameSessionState.Interrupted, "用户强制终止游戏进程。");
            }
        }
        catch (InvalidOperationException exception)
        {
            // 会话可能已被采集宿主收尾；终止动作本身已成功，不影响结果。
            _logger?.LogDebug(exception, "标记强制终止会话 {SessionId} 失败。", sessionId);
        }

        _logger?.LogInformation("用户强制终止会话 {SessionId} 的游戏进程 PID {Pid}。", sessionId, processId);
        return true;
    }

    private static bool IsLiveState(GameSessionState state) =>
        state is GameSessionState.Starting
            or GameSessionState.Running
            or GameSessionState.GameExitedDraining
            or GameSessionState.ProcessStateUnknown;

    private static GameProcessInfo? ProbeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return null;
            }

            var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return new GameProcessInfo(processId, startedAt);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            // 无法读取启动时间（权限/受保护进程）时不把 PID 当成有效游戏进程，避免误终止。
            return null;
        }
    }

    private static bool KillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            process.Kill();
            process.WaitForExit(5000);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>会话恢复警告代码（与 manifest.Warnings 中的字符串保持一致）。</summary>
public static class SessionRecoveryWarnings
{
    public const string Reattached = "session.recovered.reattached";

    public const string Unknown = "session.recovered.unknown";

    public const string Interrupted = "session.recovered.interrupted";
}
