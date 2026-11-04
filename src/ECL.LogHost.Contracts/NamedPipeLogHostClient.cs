// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.IO.Pipes;

namespace Ecl.LogHost.Contracts;

/// <summary>
/// LogHost 客户端。一次连接只发送控制消息；断线后按需重连，读取方（UI）
/// 始终以会话文件为日志权威来源。
/// </summary>
public sealed class NamedPipeLogHostClient : ILogHostClient
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<LogHostMessage>> _pending = new(StringComparer.Ordinal);
    private NamedPipeClientStream? _pipe;
    private Task? _reader;

    public NamedPipeLogHostClient(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? LogHostProtocol.PipeName : pipeName;
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            _pipe?.Dispose();
            var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(15000, cancellationToken).ConfigureAwait(false);
            _pipe = pipe;
            _reader = ReadLoopAsync(pipe, CancellationToken.None);

            var hello = await SendAsync(LogHostMessage.Request(LogHostMessageTypes.Hello, NewRequestId()), cancellationToken)
                .ConfigureAwait(false);
            if (hello.Type != LogHostMessageTypes.HelloAck)
            {
                throw new InvalidOperationException($"LogHost 握手失败：{hello.Type}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LogHostStartResult> StartProcessAsync(
        LogHostStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var message = new LogHostMessage
        {
            Type = LogHostMessageTypes.StartProcess,
            RequestId = NewRequestId(),
            SessionId = request.SessionId,
            InstanceId = request.InstanceId,
            Executable = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            Arguments = request.Arguments,
        };

        var reply = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (reply.Type == LogHostMessageTypes.Error)
        {
            return new LogHostStartResult(false, null, reply.Message);
        }

        return new LogHostStartResult(true, reply.ProcessId, null);
    }

    public async Task<LogHostSessionState?> GetStateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync(
                new LogHostMessage
                {
                    Type = LogHostMessageTypes.GetState,
                    RequestId = NewRequestId(),
                    SessionId = sessionId,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (reply.Type != LogHostMessageTypes.State)
        {
            return null;
        }

        return new LogHostSessionState(
            sessionId,
            reply.State ?? "unknown",
            reply.ProcessId,
            reply.ExitCode,
            reply.WrittenBytes ?? 0,
            reply.DroppedBytes ?? 0);
    }

    public async Task FlushAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await SendAsync(
                new LogHostMessage
                {
                    Type = LogHostMessageTypes.Flush,
                    RequestId = NewRequestId(),
                    SessionId = sessionId,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(
                new LogHostMessage
                {
                    Type = LogHostMessageTypes.Shutdown,
                    RequestId = NewRequestId(),
                },
                cancellationToken)
            .ConfigureAwait(false);
        Dispose();
    }

    private async Task<LogHostMessage> SendAsync(LogHostMessage message, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        var pipe = _pipe ?? throw new InvalidOperationException("LogHost 未连接。");
        var completion = new TaskCompletionSource<LogHostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = message.RequestId ?? NewRequestId();
        _pending[requestId] = completion;

        try
        {
            await LogHostProtocol.WriteAsync(pipe, message with { RequestId = requestId }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var message = await LogHostProtocol.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                if (message.RequestId is not null && _pending.TryGetValue(message.RequestId, out var completion))
                {
                    completion.TrySetResult(message);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // 连接断开：等待中的请求由超时结束。
        }
        finally
        {
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(new IOException("LogHost 连接已断开。"));
            }

            _pending.Clear();
        }
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _pipe = null;
        _gate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }

        Dispose();
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N")[..12];
}
