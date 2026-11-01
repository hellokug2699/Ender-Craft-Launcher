// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Sessions;

public interface IGameSessionService
{
    GameLogSession? Get(string sessionId);

    IReadOnlyList<GameLogSession> List();

    /// <summary>在启动游戏进程之前创建并持久化会话。</summary>
    GameLogSession Create(SessionCreateRequest request);

    void MarkState(string sessionId, GameSessionState state, string? detail = null);

    void AttachProcess(
        string sessionId,
        int processId,
        DateTimeOffset processStartedAtUtc,
        string executablePath,
        string workingDirectory);

    void RegisterArtifact(string sessionId, SessionArtifact artifact);

    void SetCaptureFault(string sessionId, string fault);

    void SetExitCode(string sessionId, int exitCode);

    /// <summary>ECL 启动后的恢复。幂等：重复执行不会重复登记文件或破坏证据。</summary>
    SessionRecoveryReport Recover();
}

/// <summary>
/// 会话生命周期的唯一所有者。UI 与 CLI 只读取会话，不自行扫描目录推断归属。
/// </summary>
public sealed class GameSessionService : IGameSessionService
{
    private readonly GameSessionStore _store;
    private readonly ILogger<GameSessionService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, GameLogSession> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);

    public GameSessionService(IAppPaths paths, ILogger<GameSessionService> logger)
    {
        _store = new GameSessionStore(paths);
        _logger = logger;
    }

    public GameLogSession? Get(string sessionId)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(sessionId, out var cached))
            {
                return cached;
            }

            var loaded = _store.Load(sessionId);
            if (loaded is not null)
            {
                _cache[sessionId] = loaded;
                if (!_sequences.ContainsKey(sessionId))
                {
                    _sequences[sessionId] = SessionJournal.LastSequence(loaded.JournalPath);
                }
            }

            return loaded;
        }
    }

    public IReadOnlyList<GameLogSession> List()
    {
        var all = _store.LoadAll();
        lock (_gate)
        {
            foreach (var session in all)
            {
                _cache[session.SessionId] = session;
            }
        }

        return all;
    }

    public GameLogSession Create(SessionCreateRequest request)
    {
        var sessionId = GameSessionStore.CreateSessionId();
        var session = _store.Create(sessionId);
        var manifest = session.Manifest with
        {
            InstanceId = request.InstanceId,
            InstanceName = request.InstanceName,
            GameDirectory = request.GameDirectory,
            JavaPath = request.JavaPath,
            JavaVersion = request.JavaVersion,
            MinecraftVersion = request.MinecraftVersion,
            Loader = request.Loader,
            LogIsolation = request.LogIsolation,
            State = GameSessionState.Preparing,
            CaptureHost = "in-process",
        };

        session = new GameLogSession(manifest, session.DirectoryPath);
        _store.SaveManifest(session);

        lock (_gate)
        {
            _cache[sessionId] = session;
            _sequences[sessionId] = 0;
        }

        AppendEvent(session, "session.created", request.InstanceId);

        _logger.LogInformation("已创建游戏日志会话 {SessionId}（实例 {InstanceId}）。", sessionId, request.InstanceId);
        return session;
    }

    public void MarkState(string sessionId, GameSessionState state, string? detail = null)
    {
        var session = Require(sessionId);
        session = Update(session, session.Manifest with
        {
            State = state,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        AppendEvent(session, "state." + state.ToString().ToLowerInvariant(), detail);
    }

    public void AttachProcess(
        string sessionId,
        int processId,
        DateTimeOffset processStartedAtUtc,
        string executablePath,
        string workingDirectory)
    {
        var session = Require(sessionId);
        session = Update(session, session.Manifest with
        {
            ProcessId = processId,
            ProcessStartedAtUtc = processStartedAtUtc,
            ExecutablePath = executablePath,
            WorkingDirectory = workingDirectory,
            State = GameSessionState.Running,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        AppendEvent(
            session,
            "process.attached",
            $"pid={processId}; started={processStartedAtUtc:O}; exe={executablePath}");
    }

    public void RegisterArtifact(string sessionId, SessionArtifact artifact)
    {
        var session = Require(sessionId);
        var remaining = session.Manifest.Artifacts
            .Where(existing => !string.Equals(existing.Id, artifact.Id, StringComparison.Ordinal))
            .Append(artifact)
            .ToArray();

        session = Update(session, session.Manifest with
        {
            Artifacts = remaining,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        AppendEvent(session, "artifact.registered", $"{artifact.Kind}:{artifact.Ownership}:{artifact.CapturedPath}");
    }

    public void SetCaptureFault(string sessionId, string fault)
    {
        var session = Require(sessionId);
        session = Update(session, session.Manifest with
        {
            CaptureFault = fault,
            Warnings = AppendWarning(session.Manifest.Warnings, "session.capture.fault"),
        });

        AppendEvent(session, "capture.fault", fault);
    }

    public void SetExitCode(string sessionId, int exitCode)
    {
        var session = Require(sessionId);
        Update(session, session.Manifest with { ExitCode = exitCode });
    }

    public SessionRecoveryReport Recover()
    {
        var sessions = _store.LoadAll();
        var interrupted = 0;
        var reattached = 0;
        var unknown = 0;
        var warnings = new List<string>();

        foreach (var session in sessions)
        {
            if (session.Manifest.State is not (
                GameSessionState.Starting or
                GameSessionState.Running or
                GameSessionState.GameExitedDraining or
                GameSessionState.RecoveryPending))
            {
                continue;
            }

            var current = session;
            var probe = Probe(current.Manifest);
            if (probe.Alive && probe.IdentityMatch)
            {
                reattached++;
                current = Update(current, current.Manifest with
                {
                    State = GameSessionState.Running,
                    Warnings = AppendWarning(current.Manifest.Warnings, "session.recovered.reattached"),
                });
                AppendEvent(current, "recovery.reattached", $"pid={current.Manifest.ProcessId}");
                continue;
            }

            if (probe.Alive)
            {
                unknown++;
                warnings.Add($"{current.SessionId}: 进程仍存在但身份不匹配，标记为状态未知。");
                current = Update(current, current.Manifest with
                {
                    State = GameSessionState.ProcessStateUnknown,
                    Warnings = AppendWarning(current.Manifest.Warnings, "session.recovered.unknown"),
                });
                AppendEvent(current, "recovery.unknown", "PID 存在但启动时间或可执行文件不匹配");
                continue;
            }

            interrupted++;
            var state = current.Manifest.State == GameSessionState.GameExitedDraining
                ? GameSessionState.CaptureIncomplete
                : GameSessionState.Interrupted;
            current = Update(current, current.Manifest with
            {
                State = state,
                Warnings = AppendWarning(current.Manifest.Warnings, "session.recovered.interrupted"),
            });
            AppendEvent(current, "recovery.interrupted", "未观察到正常退出");
        }

        if (interrupted + reattached + unknown > 0)
        {
            _logger.LogInformation(
                "会话恢复：扫描 {Scanned}，中断 {Interrupted}，重连 {Reattached}，未知 {Unknown}。",
                sessions.Count,
                interrupted,
                reattached,
                unknown);
        }

        return new SessionRecoveryReport(sessions.Count, interrupted, reattached, unknown, warnings);
    }

    private GameLogSession Require(string sessionId) =>
        Get(sessionId) ?? throw new InvalidOperationException($"会话不存在：{sessionId}");

    private GameLogSession Update(GameLogSession session, SessionManifest manifest)
    {
        var updated = session with { Manifest = manifest };
        _store.SaveManifest(updated);
        lock (_gate)
        {
            _cache[updated.SessionId] = updated;
        }

        return updated;
    }

    private void AppendEvent(GameLogSession session, string type, string? detail)
    {
        long sequence;
        lock (_gate)
        {
            _sequences.TryGetValue(session.SessionId, out var current);
            sequence = current + 1;
            _sequences[session.SessionId] = sequence;
        }

        try
        {
            SessionJournal.Append(
                session.JournalPath,
                new SessionEvent(sequence, DateTimeOffset.UtcNow, type, detail));

            var refreshed = session with
            {
                Manifest = session.Manifest with { LastDurableSequence = sequence },
            };
            _store.SaveManifest(refreshed);
            lock (_gate)
            {
                _cache[refreshed.SessionId] = refreshed;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "写入会话事件失败：{SessionId} {Type}", session.SessionId, type);
        }
    }

    private static IReadOnlyList<string> AppendWarning(IReadOnlyList<string> warnings, string warning) =>
        warnings.Contains(warning, StringComparer.Ordinal) ? warnings : [.. warnings, warning];

    private static (bool Alive, bool IdentityMatch) Probe(SessionManifest manifest)
    {
        if (manifest.ProcessId is not { } pid || pid <= 0)
        {
            return (false, false);
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return (false, false);
            }

            var startMatches = manifest.ProcessStartedAtUtc is not { } expected ||
                               Math.Abs((process.StartTime.ToUniversalTime() - expected.UtcDateTime).TotalSeconds) < 2;
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // 权限不足时无法读取路径，交由启动时间判断。
            }

            var pathMatches = string.IsNullOrWhiteSpace(manifest.ExecutablePath) ||
                              string.IsNullOrWhiteSpace(path) ||
                              string.Equals(
                                  Path.GetFullPath(path),
                                  Path.GetFullPath(manifest.ExecutablePath),
                                  StringComparison.OrdinalIgnoreCase);
            return (true, startMatches && pathMatches);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return (false, false);
        }
    }
}
