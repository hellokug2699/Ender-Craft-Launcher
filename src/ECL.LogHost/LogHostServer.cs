// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.IO.Pipes;
using Ecl.LogHost.Contracts;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;

namespace Ecl.LogHost;

/// <summary>
/// LogHost 主体：接受本机控制连接、按会话创建游戏进程并持有管道。
/// 只处理控制消息；日志正文直接流式写入会话文件，不回传 UI。
/// </summary>
internal sealed class LogHostServer
{
    private readonly IGameSessionService _sessions;
    private readonly SessionArtifactCollector _artifacts;
    private readonly ILogger<LogHostServer> _logger;
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<string, GameProcessSupervisor> _active = new(StringComparer.Ordinal);
    private int _clients;

    public LogHostServer(
        IGameSessionService sessions,
        SessionArtifactCollector artifacts,
        ILogger<LogHostServer> logger,
        string? pipeName = null)
    {
        _sessions = sessions;
        _artifacts = artifacts;
        _logger = logger;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? LogHostProtocol.PipeName : pipeName;
    }

    public int ActiveSessions => _active.Count;

    public int Clients => Volatile.Read(ref _clients);

    public bool IsShutdownRequested { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("LogHost 已启动，管道 {Pipe}。", _pipeName);

        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (IOException exception)
            {
                pipe.Dispose();
                _logger.LogWarning(exception, "接收 LogHost 连接失败。");
                continue;
            }

            _ = HandleConnectionAsync(pipe, cancellationToken);
        }

        _logger.LogInformation("LogHost 停止接受新连接。");
    }

    /// <summary>宿主停止前冲刷活动会话并标记中断；绝不谎报采集完成。</summary>
    public async Task StopAsync()
    {
        foreach (var (sessionId, supervisor) in _active.ToArray())
        {
            await supervisor.StopCaptureAsync("loghost.shutdown").ConfigureAwait(false);
            _active.TryRemove(sessionId, out _);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _clients);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                LogHostMessage? message;
                try
                {
                    message = await LogHostProtocol.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException)
                {
                    break;
                }

                if (message is null)
                {
                    break;
                }

                var reply = Dispatch(message);
                await LogHostProtocol.WriteAsync(pipe, reply, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // 客户端断开不影响活动会话采集。
        }
        finally
        {
            Interlocked.Decrement(ref _clients);
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private LogHostMessage Dispatch(LogHostMessage message)
    {
        switch (message.Type)
        {
            case LogHostMessageTypes.Hello:
                return message.Reply(LogHostMessageTypes.HelloAck);

            case LogHostMessageTypes.StartProcess:
                return StartProcess(message);

            case LogHostMessageTypes.GetState:
                return GetState(message);

            case LogHostMessageTypes.Flush:
                return message.Reply(LogHostMessageTypes.Flushed) with
                {
                    WrittenBytes = message.SessionId is not null && _active.TryGetValue(message.SessionId, out var supervisor)
                        ? supervisor.WrittenBytes
                        : 0,
                };

            case LogHostMessageTypes.Shutdown:
                if (ActiveSessions > 0)
                {
                    return message.Reply(LogHostMessageTypes.Error) with
                    {
                        Message = "仍有活动游戏会话，LogHost 不会在采集完成前退出。",
                    };
                }

                IsShutdownRequested = true;
                return message.Reply(LogHostMessageTypes.ShuttingDown);

            default:
                return message.Reply(LogHostMessageTypes.Error) with { Message = $"未知消息类型：{message.Type}" };
        }
    }

    private LogHostMessage StartProcess(LogHostMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.SessionId) ||
            string.IsNullOrWhiteSpace(message.Executable) ||
            string.IsNullOrWhiteSpace(message.WorkingDirectory))
        {
            return message.Reply(LogHostMessageTypes.Error) with { Message = "启动请求缺少会话、可执行文件或工作目录。" };
        }

        if (_active.ContainsKey(message.SessionId))
        {
            return message.Reply(LogHostMessageTypes.Error) with { Message = "该会话已经在采集。" };
        }

        var session = _sessions.Get(message.SessionId);
        if (session is null)
        {
            return message.Reply(LogHostMessageTypes.Error) with { Message = "会话不存在。" };
        }

        try
        {
            var supervisor = GameProcessSupervisor.Start(
                _sessions,
                _artifacts,
                _logger,
                session,
                message.Executable,
                message.WorkingDirectory,
                message.Arguments ?? []);

            _active[message.SessionId] = supervisor;
            _ = WatchAsync(message.SessionId, supervisor);

            return message.Reply(LogHostMessageTypes.ProcessStarted) with
            {
                ProcessId = supervisor.ProcessId,
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(exception, "LogHost 启动游戏进程失败：{SessionId}", message.SessionId);
            return message.Reply(LogHostMessageTypes.Error) with { Message = exception.Message };
        }
    }

    private LogHostMessage GetState(LogHostMessage message)
    {
        if (message.SessionId is null)
        {
            return message.Reply(LogHostMessageTypes.Error) with { Message = "缺少会话 ID。" };
        }

        var manifest = _sessions.Get(message.SessionId)?.Manifest;
        if (manifest is null)
        {
            return message.Reply(LogHostMessageTypes.Error) with { Message = "会话不存在。" };
        }

        var active = _active.TryGetValue(message.SessionId, out var supervisor);
        return message.Reply(LogHostMessageTypes.State) with
        {
            State = manifest.State.ToString(),
            ProcessId = manifest.ProcessId,
            ExitCode = manifest.ExitCode,
            WrittenBytes = active ? supervisor!.WrittenBytes : 0,
            DroppedBytes = active ? supervisor!.DroppedBytes : 0,
        };
    }

    private async Task WatchAsync(string sessionId, GameProcessSupervisor supervisor)
    {
        try
        {
            while (!supervisor.HasExited)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }

            _active.TryRemove(sessionId, out _);
            _logger.LogInformation("会话 {SessionId} 的采集监督结束。", sessionId);
        }
        catch (InvalidOperationException)
        {
            _active.TryRemove(sessionId, out _);
        }
    }
}
