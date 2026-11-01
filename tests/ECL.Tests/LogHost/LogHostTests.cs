// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using Ecl.Core;
using Ecl.LogHost;
using Ecl.LogHost.Contracts;
using Ecl.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.LogHost;

public sealed class LogHostTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly GameSessionService _sessions;

    public LogHostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ecl-loghost-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _sessions = new GameSessionService(_paths, NullLogger<GameSessionService>.Instance);
    }

    [Fact]
    public async Task ProtocolRoundTripsMessages()
    {
        using var stream = new MemoryStream();
        var message = new LogHostMessage
        {
            Type = LogHostMessageTypes.StartProcess,
            RequestId = "req-1",
            SessionId = "session-1",
            Executable = "java",
            WorkingDirectory = "/tmp",
            Arguments = ["-version"],
        };

        await LogHostProtocol.WriteAsync(stream, message);
        stream.Position = 0;
        var received = await LogHostProtocol.ReadAsync(stream);

        Assert.NotNull(received);
        Assert.Equal(message.Type, received.Type);
        Assert.Equal("req-1", received.RequestId);
        Assert.Equal("session-1", received.SessionId);
        Assert.Equal(["-version"], received.Arguments!);
    }

    [Fact]
    public async Task ProtocolRejectsOversizedFrame()
    {
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, LogHostProtocol.MaxFrameBytes + 1);
        stream.Write(header);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => LogHostProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task ServerStartsProcessAndCapturesSessionOutput()
    {
        var pipeName = "ecl-test-" + Guid.NewGuid().ToString("N")[..10];
        var artifacts = new SessionArtifactCollector(_sessions, NullLogger<SessionArtifactCollector>.Instance);
        var server = new LogHostServer(_sessions, artifacts, NullLogger<LogHostServer>.Instance, pipeName);
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);

        var session = _sessions.Create(new SessionCreateRequest
        {
            InstanceId = "demo-abc123",
            InstanceName = "Demo",
            GameDirectory = Path.Combine(_root, "games"),
            LogIsolation = "direct",
        });
        Directory.CreateDirectory(Path.Combine(_root, "games"));

        var (executable, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>)["/c", "echo hello-out & echo hello-err 1>&2"])
            : ("/bin/sh", (IReadOnlyList<string>)["-c", "echo hello-out; echo hello-err >&2"]);

        await using var client = new NamedPipeLogHostClient(pipeName);
        await client.ConnectAsync();
        var started = await client.StartProcessAsync(new LogHostStartRequest(
            session.SessionId,
            "demo-abc123",
            executable,
            Path.Combine(_root, "games"),
            arguments));

        Assert.True(started.Success, started.Error);
        Assert.NotNull(started.ProcessId);

        var completed = await WaitForStateAsync(session.SessionId, GameSessionState.CaptureCompleted, TimeSpan.FromSeconds(15));

        Assert.True(completed, $"会话未完成，当前状态：{_sessions.Get(session.SessionId)?.Manifest.State}");
        var stdout = await File.ReadAllTextAsync(session.StdoutPath);
        Assert.Contains("hello-out", stdout);
        var stderr = await File.ReadAllTextAsync(session.StderrPath);
        Assert.Contains("hello-err", stderr);
        Assert.NotNull(_sessions.Get(session.SessionId)!.Manifest.ExitCode);

        cancellation.Cancel();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
    }

    private async Task<bool> WaitForStateAsync(string sessionId, GameSessionState state, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_sessions.Get(sessionId)?.Manifest.State == state)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }
}
