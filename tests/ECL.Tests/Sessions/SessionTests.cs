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
    public void CaptureDegradationIsRecordedAsDegradedNotFault()
    {
        var service = CreateService();
        var session = service.Create(Request());

        service.MarkCaptureDegraded(session.SessionId, "未找到 ECL.LogHost.exe");

        var manifest = service.Get(session.SessionId)!.Manifest;
        Assert.Equal("未找到 ECL.LogHost.exe", manifest.CaptureDegraded);
        Assert.Null(manifest.CaptureFault);
        Assert.Contains("session.capture.degraded", manifest.Warnings);
        Assert.DoesNotContain("session.capture.fault", manifest.Warnings);
        Assert.Contains(service.ReadEvents(session.SessionId), entry => entry.Type == "capture.degraded");
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

    [Fact]
    public void RetentionOnlyDeletesFinishedSessionsPastTheAge()
    {
        var service = CreateService();
        var retention = new SessionRetentionService(
            _environment.Paths,
            NullLogger<SessionRetentionService>.Instance);

        var completed = service.Create(Request());
        service.MarkState(completed.SessionId, GameSessionState.CaptureCompleted);
        var running = service.Create(Request());
        service.AttachProcess(running.SessionId, 4242, DateTimeOffset.UtcNow, "java.exe", _environment.Root);
        var unknown = service.Create(Request());
        service.MarkState(unknown.SessionId, GameSessionState.ProcessStateUnknown);

        var tooYoung = retention.Plan(TimeSpan.FromHours(1));
        Assert.Empty(tooYoung.SessionIds);
        Assert.Contains(tooYoung.ProtectedReasons, reason => reason.Contains(running.SessionId));

        var plan = retention.Plan(TimeSpan.Zero);
        Assert.Contains(completed.SessionId, plan.SessionIds);
        Assert.DoesNotContain(running.SessionId, plan.SessionIds);
        Assert.DoesNotContain(unknown.SessionId, plan.SessionIds);

        Assert.True(retention.Apply(plan).IsSuccess);
        Assert.False(Directory.Exists(completed.DirectoryPath));
        Assert.True(Directory.Exists(running.DirectoryPath));
        Assert.True(Directory.Exists(unknown.DirectoryPath));
    }

    [Fact]
    public void RecoveryUsesIsolatedFileWhenCaptureHostIsGone()
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
            _environment.Root,
            "loghost",
            int.MaxValue);
        var isolated = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllText(isolated, "session log");

        service.Recover();

        var manifest = service.Get(session.SessionId)!.Manifest;
        Assert.Equal(GameSessionState.Running, manifest.State);
        Assert.Contains("session.recovered.hostUnverified", manifest.Warnings);
        Assert.Contains(manifest.Artifacts, artifact =>
            artifact.Kind == SessionArtifactKinds.GameLog
            && artifact.Ownership == ArtifactOwnership.Direct
            && artifact.CapturedPath == isolated);
    }

    [Fact]
    public void RecoveryKeepsLiveCaptureHostQuiet()
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
            _environment.Root,
            "loghost",
            current.Id);

        service.Recover();

        var manifest = service.Get(session.SessionId)!.Manifest;
        Assert.Equal(GameSessionState.Running, manifest.State);
        Assert.DoesNotContain("session.recovered.hostUnverified", manifest.Warnings);
    }

    /// <summary>
    /// F-09 S7：导出副本把本地绝对路径替换为占位符；原始 manifest/日志文件保持原样，
    /// 这样用户仍能在本机看到真实路径，而分享出去的诊断包不含本机目录。
    /// </summary>
    [Fact]
    public async Task ExportRedactsLocalAbsolutePathsOnlyInTheArchiveCopy()
    {
        var service = CreateService();
        var session = service.Create(Request());
        const string rawLog = "C:\\Users\\player\\game\\crash.log\nno path here\n";
        await File.WriteAllTextAsync(session.StdoutPath, rawLog);
        var crashPath = Path.Combine(_environment.Root, "crash.txt");
        await File.WriteAllTextAsync(crashPath, "crash");
        service.RegisterArtifact(session.SessionId, new SessionArtifact
        {
            Id = "crash",
            Kind = SessionArtifactKinds.ClientCrashReport,
            SourcePath = crashPath,
            CapturedPath = crashPath,
            Ownership = ArtifactOwnership.Direct,
        });

        var exporter = new SessionExportService(service, _environment.Paths);
        var output = Path.Combine(_environment.Root, "redacted.zip");
        var result = await exporter.ExportAsync(session.SessionId, output);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        using var archive = ZipFile.OpenRead(output);
        var manifest = await ReadEntryAsync(archive, "manifest.json");
        var artifacts = await ReadEntryAsync(archive, "artifacts.json");
        var stdout = await ReadEntryAsync(archive, "logs/game.stdout.log");

        Assert.Contains("<local-path>", manifest);
        Assert.Contains("<local-path>", stdout);
        Assert.Contains("<local-path>", artifacts);
        Assert.DoesNotContain("C:", manifest);
        Assert.DoesNotContain("C:", artifacts);

        // 原始文件不被改写：脱敏只作用于导出副本。
        Assert.Contains("C:", await File.ReadAllTextAsync(session.ManifestPath));
        Assert.Equal(rawLog, await File.ReadAllTextAsync(session.StdoutPath));
    }

    /// <summary>
    /// F-09 S3：采集计量写入 manifest 与 journal；丢弃字节必须产生警告，
    /// 让“日志不完整”在没有 UI 的情况下也可复核。
    /// </summary>
    [Fact]
    public void CaptureAccountingPersistsBytesAndWarnsWhenDropped()
    {
        var service = CreateService();
        var session = service.Create(Request());

        service.SetCaptureAccounting(session.SessionId, SessionCaptureModes.Redirect, 4096, 512);

        var manifest = service.Get(session.SessionId)!.Manifest;
        Assert.Equal(SessionCaptureModes.Redirect, manifest.CaptureMode);
        Assert.Equal(4096, manifest.CaptureWrittenBytes);
        Assert.Equal(512, manifest.CaptureDroppedBytes);
        Assert.NotNull(manifest.CaptureAccountedAtUtc);
        Assert.Contains(SessionWarnings.CaptureDropped, manifest.Warnings);

        var events = SessionJournal.ReadAll(session.JournalPath, out _);
        Assert.Contains(events, entry => entry.Type == "capture.accounting");
    }

    /// <summary>
    /// F-09 S9：控制台模式没有重定向，会话必须显式记录“未采集”而不是 0 字节的完整采集。
    /// </summary>
    [Fact]
    public void ConsoleCaptureModeIsRecordedAsEvidenceGap()
    {
        var service = CreateService();
        var session = service.Create(Request());

        service.SetCaptureAccounting(
            session.SessionId,
            SessionCaptureModes.Console,
            0,
            0,
            SessionWarnings.CaptureConsole);

        var manifest = service.Get(session.SessionId)!.Manifest;
        Assert.Equal(SessionCaptureModes.Console, manifest.CaptureMode);
        Assert.Contains(SessionWarnings.CaptureConsole, manifest.Warnings);
        Assert.DoesNotContain(SessionWarnings.CaptureDropped, manifest.Warnings);
    }

    [Fact]
    public void ProcessMonitorListsAliveGameSessionsAndTerminatesThem()
    {
        var service = CreateService();
        var session = service.Create(Request());
        var started = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        service.AttachProcess(session.SessionId, 4242, started, "java.exe", _environment.Root);

        var killed = new List<int>();
        var monitor = new GameProcessMonitor(
            service,
            logger: null,
            probe: pid => pid == 4242 ? new GameProcessInfo(4242, started) : null,
            kill: pid =>
            {
                killed.Add(pid);
                return true;
            });

        var snapshot = Assert.Single(monitor.Scan());
        Assert.Equal(session.SessionId, snapshot.SessionId);
        Assert.False(snapshot.RecoveredAfterRestart);

        Assert.True(monitor.TryTerminate(session.SessionId, out var error), error);
        Assert.Equal([4242], killed);
        Assert.Equal(GameSessionState.Interrupted, service.Get(session.SessionId)!.Manifest.State);
    }

    [Fact]
    public void ProcessMonitorIgnoresPidReuseByStartTimeMismatch()
    {
        var service = CreateService();
        var session = service.Create(Request());
        var started = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        service.AttachProcess(session.SessionId, 4243, started, "java.exe", _environment.Root);

        var monitor = new GameProcessMonitor(
            service,
            logger: null,
            probe: _ => new GameProcessInfo(4243, started.AddHours(-2)),
            kill: _ => true);

        Assert.Empty(monitor.Scan());
    }

    [Fact]
    public void ProcessMonitorMarksRecoveredSessions()
    {
        var service = CreateService();
        var session = service.Create(Request());
        var started = DateTimeOffset.UtcNow;
        service.AttachProcess(session.SessionId, 4244, started, "java.exe", _environment.Root);
        service.MarkState(session.SessionId, GameSessionState.ProcessStateUnknown);

        var monitor = new GameProcessMonitor(
            service,
            logger: null,
            probe: _ => new GameProcessInfo(4244, started),
            kill: _ => true);

        var snapshot = Assert.Single(monitor.Scan());
        Assert.True(snapshot.RecoveredAfterRestart);
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        return await reader.ReadToEndAsync();
    }

    public void Dispose() => _environment.Dispose();
}
