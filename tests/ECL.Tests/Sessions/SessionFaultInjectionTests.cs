// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Sessions;

/// <summary>
/// 崩溃/断电点的故障注入：直接构造“写到一半”的会话目录，验证恢复既不丢证据，
/// 也不把未确认的退出伪装成正常完成。
/// </summary>
public sealed class SessionFaultInjectionTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly GameSessionService _service;

    public SessionFaultInjectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ecl-session-faults", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _service = new GameSessionService(_paths, NullLogger<GameSessionService>.Instance);
    }

    private SessionCreateRequest Request() => new()
    {
        InstanceId = "demo-abc123",
        InstanceName = "Demo",
        GameDirectory = Path.Combine(_root, "games"),
        LogIsolation = "direct",
    };

    private void RewriteManifest(GameLogSession session, SessionManifest manifest) =>
        File.WriteAllText(session.ManifestPath, JsonSerializer.Serialize(manifest, EclJson.Indented));

    [Fact]
    public void CrashBeforePidCheckpointIsRecoveredAsInterrupted()
    {
        var session = _service.Create(Request());
        RewriteManifest(session, session.Manifest with
        {
            State = GameSessionState.Starting,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        var report = _service.Recover();

        Assert.Equal(1, report.Interrupted);
        var manifest = _service.Get(session.SessionId)!.Manifest;
        Assert.Equal(GameSessionState.Interrupted, manifest.State);
        Assert.Contains("session.recovered.interrupted", manifest.Warnings);
    }

    [Fact]
    public void CrashBeforeFinalStateKeepsEvidenceButMarksCaptureIncomplete()
    {
        var session = _service.Create(Request());
        var stdout = Path.Combine(session.DirectoryPath, "game.stdout.log");
        File.WriteAllText(stdout, "complete output");
        RewriteManifest(session, session.Manifest with
        {
            State = GameSessionState.GameExitedDraining,
            ProcessId = 424242,
            ExitCode = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        _service.Recover();

        var manifest = _service.Get(session.SessionId)!.Manifest;
        Assert.Equal(GameSessionState.CaptureIncomplete, manifest.State);
        Assert.Equal(1, manifest.ExitCode);
        Assert.True(File.Exists(stdout));
        Assert.Equal("complete output", File.ReadAllText(stdout));
    }

    [Fact]
    public void TornJournalKeepsPriorEventsAndStillRecovers()
    {
        var session = _service.Create(Request());
        _service.MarkState(session.SessionId, GameSessionState.Starting);
        File.AppendAllText(session.JournalPath, "{\"sequence\":99,\"type\":\"half");
        RewriteManifest(session, session.Manifest with
        {
            State = GameSessionState.Starting,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        var report = _service.Recover();
        var events = SessionJournal.ReadAll(session.JournalPath, out var torn);

        Assert.Equal(1, report.Interrupted);
        Assert.True(torn);
        Assert.Equal([1L, 2L], events.Select(entry => entry.Sequence));
        Assert.Equal(GameSessionState.Interrupted, _service.Get(session.SessionId)!.Manifest.State);
    }

    [Fact]
    public void OrphanSessionDirectoryIsPreservedAndIgnored()
    {
        var orphan = Path.Combine(_paths.LogsDirectory, "sessions", "20260101-000000-orphan");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "game.stdout.log"), "orphan evidence");

        var list = _service.List();
        var report = _service.Recover();

        Assert.Empty(list);
        Assert.Equal(0, report.Scanned);
        Assert.True(File.Exists(Path.Combine(orphan, "game.stdout.log")));
    }

    [Fact]
    public async Task BoundedWriterAccountsForEveryByte()
    {
        var path = Path.Combine(_root, "logs", "bounded.log");
        var writer = SessionLogWriter.Open(path, capacity: 4);
        try
        {
            var chunk = Encoding.UTF8.GetBytes(new string('x', 1024));
            const int chunks = 2000;
            for (var index = 0; index < chunks; index++)
            {
                writer.Write(chunk);
            }

            await writer.CompleteAsync();

            var total = (long)chunks * chunk.Length;
            // 完成时会写入一条缺口标记，因此允许略大于输入总量。
            Assert.InRange(writer.WrittenBytes + writer.DroppedBytes, total, total + 512);
            Assert.True(writer.DroppedBytes > 0, "队列应发生可观测的丢弃");
            Assert.Equal(writer.WrittenBytes, new FileInfo(path).Length);
        }
        finally
        {
            await writer.DisposeAsync();
        }
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
