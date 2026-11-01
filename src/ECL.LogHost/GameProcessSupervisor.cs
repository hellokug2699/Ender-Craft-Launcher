// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;

namespace Ecl.LogHost;

/// <summary>
/// 单个游戏进程的监督器：创建进程、持续排空 stdout/stderr、有界落盘，
/// 并在退出后排空、登记崩溃证据、收尾会话。
/// </summary>
internal sealed class GameProcessSupervisor
{
    private readonly IGameSessionService _sessions;
    private readonly SessionArtifactCollector _artifacts;
    private readonly ILogger _logger;
    private readonly SessionLogWriter _stdout;
    private readonly SessionLogWriter _stderr;
    private readonly Process _process;
    private readonly string _sessionId;

    private GameProcessSupervisor(
        IGameSessionService sessions,
        SessionArtifactCollector artifacts,
        ILogger logger,
        string sessionId,
        Process process,
        SessionLogWriter stdout,
        SessionLogWriter stderr)
    {
        _sessions = sessions;
        _artifacts = artifacts;
        _logger = logger;
        _sessionId = sessionId;
        _process = process;
        _stdout = stdout;
        _stderr = stderr;
    }

    public int ProcessId => _process.Id;

    public long WrittenBytes => _stdout.WrittenBytes + _stderr.WrittenBytes;

    public long DroppedBytes => _stdout.DroppedBytes + _stderr.DroppedBytes;

    public bool HasExited => _process.HasExited;

    public static GameProcessSupervisor Start(
        IGameSessionService sessions,
        SessionArtifactCollector artifacts,
        ILogger logger,
        GameLogSession session,
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        var stdout = SessionLogWriter.Open(session.StdoutPath);
        var stderr = SessionLogWriter.Open(session.StderrPath);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("游戏进程启动失败。");
            }
        }
        catch
        {
            stdout.DisposeAsync().AsTask().GetAwaiter().GetResult();
            stderr.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        var supervisor = new GameProcessSupervisor(
            sessions,
            artifacts,
            logger,
            session.SessionId,
            process,
            stdout,
            stderr);

        var startedAt = TryGetStartTime(process);
        sessions.AttachProcess(
            session.SessionId,
            process.Id,
            startedAt,
            executable,
            workingDirectory,
            "loghost",
            Environment.ProcessId);
        _ = supervisor.MonitorAsync(session);
        _ = VerifyIsolationAsync(sessions, session);
        return supervisor;
    }

    public async Task<long> FlushAsync()
    {
        await _stdout.FlushAsync().ConfigureAwait(false);
        await _stderr.FlushAsync().ConfigureAwait(false);
        return WrittenBytes;
    }

    /// <summary>宿主关闭时尽力冲刷已采集内容，并诚实标记会话为中断。</summary>
    public async Task StopCaptureAsync(string reason)
    {
        try
        {
            await _stdout.CompleteAsync().ConfigureAwait(false);
            await _stderr.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _logger.LogWarning(exception, "关闭时刷新会话 {SessionId} 失败。", _sessionId);
        }

        var current = _sessions.Get(_sessionId);
        if (current is null)
        {
            return;
        }

        if (current.Manifest.State is GameSessionState.Running
            or GameSessionState.Starting
            or GameSessionState.GameExitedDraining)
        {
            _sessions.SetCaptureFault(_sessionId, reason);
            _sessions.MarkState(_sessionId, GameSessionState.Interrupted, reason);
        }
    }

    private static async Task VerifyIsolationAsync(IGameSessionService sessions, GameLogSession session)
    {
        var logPath = Path.Combine(session.DirectoryPath, "game.log");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (File.Exists(logPath))
            {
                sessions.SetLogIsolation(session.SessionId, "direct", null, null);
                return;
            }
        }

        sessions.SetLogIsolation(session.SessionId, "shared", null, "session.log_isolation_unverified");
    }

    private async Task MonitorAsync(GameLogSession session)
    {
        try
        {
            var stdoutPump = PumpAsync(_process.StandardOutput.BaseStream, _stdout);
            var stderrPump = PumpAsync(_process.StandardError.BaseStream, _stderr);

            await _process.WaitForExitAsync().ConfigureAwait(false);
            _process.WaitForExit();
            await AwaitPumpAsync(stdoutPump).ConfigureAwait(false);
            await AwaitPumpAsync(stderrPump).ConfigureAwait(false);
            await _stdout.DisposeAsync().ConfigureAwait(false);
            await _stderr.DisposeAsync().ConfigureAwait(false);

            _sessions.SetExitCode(session.SessionId, _process.ExitCode);
            _sessions.MarkState(session.SessionId, GameSessionState.GameExitedDraining, $"exit={_process.ExitCode}");

            if (DroppedBytes > 0)
            {
                _sessions.SetCaptureFault(session.SessionId, $"采集队列饱和，丢弃 {DroppedBytes} 字节。");
            }

            _artifacts.CollectAfterExit(session);
            _sessions.MarkState(
                session.SessionId,
                DroppedBytes > 0 ? GameSessionState.CaptureIncomplete : GameSessionState.CaptureCompleted);

            _logger.LogInformation(
                "会话 {SessionId} 的游戏进程已退出，退出码 {ExitCode}。",
                session.SessionId,
                _process.ExitCode);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogError(exception, "监督会话 {SessionId} 的游戏进程失败。", session.SessionId);
            try
            {
                _sessions.SetCaptureFault(session.SessionId, exception.Message);
                _sessions.MarkState(session.SessionId, GameSessionState.CaptureIncomplete);
            }
            catch (InvalidOperationException)
            {
                // 会话已不存在；不因监督失败而终止游戏。
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static async Task PumpAsync(Stream stream, SessionLogWriter writer)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            writer.Write(buffer.AsSpan(0, read));
        }
    }

    private static async Task AwaitPumpAsync(Task pump)
    {
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // 管道关闭属于正常退出路径。
        }
    }

    private static DateTimeOffset TryGetStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
