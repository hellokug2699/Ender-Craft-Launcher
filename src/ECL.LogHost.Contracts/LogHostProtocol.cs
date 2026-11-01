// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text.Json;
using Ecl.Core.Json;

namespace Ecl.LogHost.Contracts;

/// <summary>
/// 命名管道上的长度前缀 JSON 帧。单帧上限防止恶意/异常消息耗尽内存；
/// 管道名只允许当前用户访问。
/// </summary>
public static class LogHostProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 1024 * 1024;
    public const string PipeName = "ecl-loghost-v1";

    public static async Task WriteAsync(Stream stream, LogHostMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, EclJson.Default);
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException("LogHost 消息超过帧上限。");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<LogHostMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var read = await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!read)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"非法的 LogHost 帧长度：{length}");
        }

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return JsonSerializer.Deserialize<LogHostMessage>(payload, EclJson.Default);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            offset += count;
        }

        return true;
    }
}

public sealed record LogHostStartRequest(
    string SessionId,
    string InstanceId,
    string Executable,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments);

public sealed record LogHostStartResult(bool Success, int? ProcessId, string? Error);

public sealed record LogHostSessionState(
    string SessionId,
    string State,
    int? ProcessId,
    int? ExitCode,
    long WrittenBytes,
    long DroppedBytes);

public interface ILogHostClient : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<LogHostStartResult> StartProcessAsync(LogHostStartRequest request, CancellationToken cancellationToken = default);

    Task<LogHostSessionState?> GetStateAsync(string sessionId, CancellationToken cancellationToken = default);

    Task FlushAsync(string sessionId, CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
