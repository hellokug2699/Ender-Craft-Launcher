// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch;

internal sealed class SystemProcessLauncher(
    IGameSessionService sessions,
    SessionArtifactCollector artifacts,
    ILogger<SystemProcessLauncher> logger) : IProcessLauncher
{
    private readonly ConcurrentDictionary<int, Process> _processes = new();

    public int Start(
        LaunchCommand command,
        string workingDirectory,
        bool showConsole,
        string instanceId,
        string? sessionId = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = !showConsole,
            RedirectStandardOutput = !showConsole,
            RedirectStandardError = !showConsole,
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            StartInfo = startInfo,
        };

        var session = sessionId is null ? null : sessions.Get(sessionId);
        var stdout = session is not null && !showConsole
            ? SessionLogWriter.Open(session.StdoutPath)
            : null;
        var stderr = session is not null && !showConsole
            ? SessionLogWriter.Open(session.StderrPath)
            : null;

        if (!showConsole && stdout is null)
        {
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    logger.LogInformation("[MC] {Line}", eventArgs.Data);
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    logger.LogWarning("[MC] {Line}", eventArgs.Data);
                }
            };
        }

        if (!process.Start())
        {
            stdout?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            stderr?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("游戏进程启动失败。");
        }

        var startedAt = TryGetStartTime(process);
        if (session is not null)
        {
            sessions.AttachProcess(session.SessionId, process.Id, startedAt, command.Executable, workingDirectory);
        }

        Task? stdoutPump = null;
        Task? stderrPump = null;
        if (!showConsole)
        {
            if (stdout is not null)
            {
                stdoutPump = PumpAsync(process.StandardOutput.BaseStream, stdout);
                stderrPump = PumpAsync(process.StandardError.BaseStream, stderr!);
            }
            else
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
        }

        _processes[process.Id] = process;
        _ = MonitorAsync(process, instanceId, session, stdout, stderr, stdoutPump, stderrPump);
        logger.LogInformation("已启动实例 {InstanceId} 的游戏进程，PID {Pid}。", instanceId, process.Id);
        return process.Id;
    }

    private async Task MonitorAsync(
        Process process,
        string instanceId,
        GameLogSession? session,
        SessionLogWriter? stdout,
        SessionLogWriter? stderr,
        Task? stdoutPump,
        Task? stderrPump)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            process.WaitForExit();

            if (stdoutPump is not null)
            {
                await AwaitPumpAsync(stdoutPump).ConfigureAwait(false);
            }

            if (stderrPump is not null)
            {
                await AwaitPumpAsync(stderrPump).ConfigureAwait(false);
            }

            if (stdout is not null)
            {
                await stdout.DisposeAsync().ConfigureAwait(false);
            }

            if (stderr is not null)
            {
                await stderr.DisposeAsync().ConfigureAwait(false);
            }

            if (session is not null)
            {
                sessions.SetExitCode(session.SessionId, process.ExitCode);
                sessions.MarkState(
                    session.SessionId,
                    GameSessionState.GameExitedDraining,
                    $"exit={process.ExitCode}");

                var dropped = (stdout?.DroppedBytes ?? 0) + (stderr?.DroppedBytes ?? 0);
                if (dropped > 0)
                {
                    sessions.SetCaptureFault(
                        session.SessionId,
                        $"stdout/stderr 采集队列饱和，丢弃 {dropped} 字节。");
                }

                var current = sessions.Get(session.SessionId)?.Manifest;
                if (current is { LogIsolation: "pending" })
                {
                    var isolated = Path.Combine(session.DirectoryPath, "game.log");
                    sessions.SetLogIsolation(
                        session.SessionId,
                        File.Exists(isolated) ? "direct" : "shared",
                        null,
                        File.Exists(isolated) ? null : "session.log_isolation_unverified");
                }

                artifacts.CollectAfterExit(session);

                var finalState = dropped > 0
                    ? GameSessionState.CaptureIncomplete
                    : GameSessionState.CaptureCompleted;
                sessions.MarkState(session.SessionId, finalState);
            }

            logger.LogInformation(
                "实例 {InstanceId} 的游戏进程已结束，PID {Pid}，退出码 {ExitCode}。",
                instanceId,
                process.Id,
                process.ExitCode);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "监控游戏进程失败。");
            if (session is not null)
            {
                try
                {
                    sessions.SetCaptureFault(session.SessionId, exception.Message);
                    sessions.MarkState(session.SessionId, GameSessionState.CaptureIncomplete);
                }
                catch (InvalidOperationException)
                {
                    // 会话可能已被清理；监控失败不应影响进程本身。
                }
            }
        }
        finally
        {
            _processes.TryRemove(process.Id, out _);
            process.Dispose();
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
