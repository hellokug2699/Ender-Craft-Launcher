// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO.Compression;
using Ecl.Core;
using Ecl.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Sessions;

public sealed class SessionTests : IDisposable
{
    private readonly TestEnvironment _environment = new();

    private GameSessionService CreateService() =>
        new(_environment.Paths, NullLogger<GameSessionService>.Instance);

    private SessionCreateRequest Request(string instanceId = "demo-abc123") => new()
    {
        InstanceId = instanceId,
        InstanceName = "Demo",
        GameDirectory = Path.Combine(_environment.Root, "games", instanceId),
        MinecraftVersion = "1.21.1",
        Loader = "vanilla",
        LogIsolation = "direct",
    };

    [Fact]
    public void CreatePersistsManifestBeforeAnyProcessStarts()
    {
        var service = CreateService();

        var session = service.Create(Request());

        Assert.True(File.Exists(session.ManifestPath));
        Assert.Equal(GameSessionState.Preparing, session.Manifest.State);
        Assert.True(Directory.Exists(session.ArtifactsDirectory));
        var events = SessionJournal.ReadAll(session.JournalPath, out var torn);
        Assert.False(torn);
        Assert.Contains(events, entry => entry.Type == "session.created");
    }

    [Fact]
    public void AttachProcessRecordsIdentityAndRunningState()
    {
        var service = CreateService();
        var session = service.Create(Request());
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-5);

        service.AttachProcess(session.SessionId, 1234, startedAt, @"C:\Java\bin\javaw.exe", @"C:\Games");

        var updated = service.Get(session.SessionId)!;
        Assert.Equal(1234, updated.Manifest.ProcessId);
        Assert.Equal(GameSessionState.Running, updated.Manifest.State);
        Assert.Equal(startedAt, updated.Manifest.ProcessStartedAtUtc);
        Assert.True(updated.Manifest.LastDurableSequence > 0);
    }

    [Fact]
    public void JournalIgnoresOnlyTornFinalRecord()
    {
        var service = CreateService();
        var session = service.Create(Request());
        service.MarkState(session.SessionId, GameSessionState.Starting);

        File.AppendAllText(session.JournalPath, "{ not json");

        var events = SessionJournal.ReadAll(session.JournalPath, out var torn);

        Assert.True(torn);
        Assert.Equal(2, events.Count);
        Assert.Equal([1L, 2L], events.Select(entry => entry.Sequence));
    }

    [Fact]
    public void RecoveryMarksMissingProcessAsInterruptedAndIsIdempotent()
    {
        var service = CreateService();
        var session = service.Create(Request());
        service.AttachProcess(session.SessionId, int.MaxValue, DateTimeOffset.UtcNow, "java.exe", _environment.Root);

        var report = service.Recover();
        var recovered = service.Get(session.SessionId)!;

        Assert.Equal(1, report.Interrupted);
        Assert.Equal(GameSessionState.Interrupted, recovered.Manifest.State);

        var second = service.Recover();
        Assert.Equal(0, second.Interrupted);
        Assert.Equal(GameSessionState.Interrupted, service.Get(session.SessionId)!.Manifest.State);
    }

    [Fact]
    public void RecoveryReattachesMatchingLiveProcess()
    {
        using var current = Process.GetCurrentProcess();
        var executable = current.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        var service = CreateService();
        var session = service.Create(Request());
        service.AttachProcess(
            session.SessionId,
            current.Id,
            new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero),
            executable,
            _environment.Root);

        var report = service.Recover();

        Assert.Equal(1, report.Reattached);
        Assert.Equal(GameSessionState.Running, service.Get(session.SessionId)!.Manifest.State);
        Assert.Contains("session.recovered.reattached", service.Get(session.SessionId)!.Manifest.Warnings);
    }

    [Fact]
    public async Task ExportProducesSessionScopedArchiveWithMissingList()
    {
        var service = CreateService();
        var session = service.Create(Request());
        await File.WriteAllTextAsync(session.StdoutPath, "hello from game\n");
        var exporter = new SessionExportService(service, _environment.Paths);
        var output = Path.Combine(_environment.Root, "export.zip");

        var result = await exporter.ExportAsync(session.SessionId, output);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(File.Exists(output));
        using var archive = ZipFile.OpenRead(output);
        Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "session-summary.txt");
        Assert.Contains(archive.Entries, entry => entry.FullName == "logs/game.stdout.log");
        Assert.Contains(archive.Entries, entry => entry.FullName == "artifacts.json");
        Assert.Contains(SessionArtifactKinds.ClientCrashReport, result.Value!.Missing);
    }

    [Fact]
    public void TwoLaunchesOfSameInstanceGetDistinctSessions()
    {
        var service = CreateService();

        var first = service.Create(Request());
        var second = service.Create(Request());

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
        Assert.Equal(2, service.List().Count);
    }

    [Fact]
    public void RecoveryMarksIdentityMismatchAsUnknownInsteadOfInterrupted()
    {
        using var current = Process.GetCurrentProcess();
        var executable = current.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        var service = CreateService();
        var session = service.Create(Request());
        // PID 存活但启动时间不匹配：PID 复用/错误附着的典型情形。
        service.AttachProcess(
            session.SessionId,
            current.Id,
            new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero).AddHours(-1),
            executable,
            _environment.Root);

        var report = service.Recover();

        Assert.Equal(1, report.Unknown);
        Assert.Equal(GameSessionState.ProcessStateUnknown, service.Get(session.SessionId)!.Manifest.State);
    }

    [Fact]
    public void CorruptManifestDoesNotBreakListingOrRecovery()
    {
        var service = CreateService();
        var valid = service.Create(Request());
        var corrupt = service.Create(Request("demo-other"));
        File.WriteAllText(corrupt.ManifestPath, "{ not json");

        var list = service.List();
        var report = service.Recover();

        Assert.Contains(list, session => session.SessionId == valid.SessionId);
        Assert.DoesNotContain(list, session => session.SessionId == corrupt.SessionId);
        Assert.True(File.Exists(corrupt.ManifestPath));
        Assert.Equal(1, report.Scanned);
    }

    [Fact]
    public void HostLossMarksRunningSessionIncompleteWithoutFakingSuccess()
    {
        var service = CreateService();
        var session = service.Create(Request());
        service.AttachProcess(session.SessionId, 4242, DateTimeOffset.UtcNow, "java.exe", _environment.Root);

        service.MarkCaptureHostLost(session.SessionId, "LogHost 崩溃");

        var manifest = service.Get(session.SessionId)!.Manifest;
        Assert.Equal(GameSessionState.CaptureIncomplete, manifest.State);
        Assert.Contains("LogHost 崩溃", manifest.CaptureFault);
        Assert.Contains("session.capture.fault", manifest.Warnings);
    }

    [Fact]
    public void HostLossDoesNotRewriteCompletedSession()
    {
        var service = CreateService();
        var session = service.Create(Request());
        service.MarkState(session.SessionId, GameSessionState.CaptureCompleted);

        service.MarkCaptureHostLost(session.SessionId, "迟到的宿主退出");

        Assert.Equal(GameSessionState.CaptureCompleted, service.Get(session.SessionId)!.Manifest.State);
    }

    public void Dispose() => _environment.Dispose();
}
