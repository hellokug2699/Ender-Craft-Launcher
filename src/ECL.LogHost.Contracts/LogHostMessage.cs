// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.LogHost.Contracts;

/// <summary>
/// LogHost 与主程序之间的控制消息。只传状态与命令，不传历史日志内容；
/// 日志正文始终由文件承载，UI 按偏移读取。
/// </summary>
public sealed record LogHostMessage
{
    public required string Type { get; init; }

    public int ProtocolVersion { get; init; } = LogHostProtocol.Version;

    public string? RequestId { get; init; }

    public string? SessionId { get; init; }

    public string? InstanceId { get; init; }

    public string? Executable { get; init; }

    public string? WorkingDirectory { get; init; }

    public IReadOnlyList<string>? Arguments { get; init; }

    public int? ProcessId { get; init; }

    public int? ExitCode { get; init; }

    public string? State { get; init; }

    public string? Message { get; init; }

    public long? WrittenBytes { get; init; }

    public long? DroppedBytes { get; init; }

    public long? Sequence { get; init; }

    public static LogHostMessage Request(string type, string requestId) => new()
    {
        Type = type,
        RequestId = requestId,
    };

    public LogHostMessage Reply(string type) => new()
    {
        Type = type,
        RequestId = RequestId,
        SessionId = SessionId,
    };
}

public static class LogHostMessageTypes
{
    public const string Hello = "hello";
    public const string HelloAck = "helloAck";
    public const string StartProcess = "startProcess";
    public const string ProcessStarted = "processStarted";
    public const string GetState = "getState";
    public const string State = "state";
    public const string Flush = "flush";
    public const string Flushed = "flushed";
    public const string Shutdown = "shutdown";
    public const string ShuttingDown = "shuttingDown";
    public const string ProcessExited = "processExited";
    public const string CaptureFault = "captureFault";
    public const string Error = "error";
}
