// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Sessions;

/// <summary>
/// 日志阅读器的有界读取契约（B6 / logs.md §3.3–§3.4）：来源身份来自会话服务，
/// 共享日志保持歧义归属，按偏移增量读取，不把半行伪装成完整行。
/// </summary>
public sealed class SessionLogReaderTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly SessionLogReader _reader = new();

    private GameSessionService Service() =>
        new(_environment.Paths, NullLogger<GameSessionService>.Instance);

    private static SessionCreateRequest Request(string instanceId = "demo") => new()
    {
        InstanceId = instanceId,
        InstanceName = "Demo",
        GameDirectory = Path.Combine(Path.GetTempPath(), "ecl-missing-game"),
        MinecraftVersion = "1.21.1",
        Loader = "fabric",
        LogIsolation = "direct",
    };

    [Fact]
    public void DirectGameLogIsReportedAsDirectAndStdoutIsSeparate()
    {
        var service = Service();
        var session = service.Create(Request());
        File.WriteAllText(Path.Combine(session.DirectoryPath, "game.log"), "[10:00:00] [main/INFO]: hello\n");
        File.WriteAllText(session.StdoutPath, "stdout line\n");

        var sources = _reader.DescribeSources(service.Get(session.SessionId)!);

        var gameLog = sources.Single(source => source.Kind == SessionLogSourceKind.GameLog);
        Assert.Equal(SessionSourceAvailability.Available, gameLog.Availability);
        Assert.Equal(ArtifactOwnership.Direct, gameLog.Ownership);
        Assert.Equal(SessionSourceAvailability.Available, sources.Single(source => source.Kind == SessionLogSourceKind.Stdout).Availability);
        Assert.Equal(SessionSourceAvailability.NotGenerated, sources.Single(source => source.Kind == SessionLogSourceKind.Stderr).Availability);
    }

    [Fact]
    public async Task LatestReadReturnsTailAndSkipsPartialFirstLine()
    {
        var service = Service();
        var session = service.Create(Request());
        var path = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllText(path, string.Join('\n', Enumerable.Range(1, 1000).Select(index => $"line-{index:0000}")) + "\n");

        var chunk = await _reader.ReadLatestAsync(
            service.Get(session.SessionId)!,
            SessionLogSourceKind.GameLog,
            maxBytes: 4096);

        Assert.True(chunk.HasMoreBefore);
        Assert.True(chunk.SkippedPartialLine);
        Assert.NotEmpty(chunk.Lines);
        Assert.StartsWith("line-", chunk.Lines[0]);
        Assert.EndsWith("line-1000", chunk.Lines[^1]);
    }

    [Fact]
    public async Task IncrementalReadReturnsOnlyNewBytes()
    {
        var service = Service();
        var session = service.Create(Request());
        var path = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllText(path, "one\ntwo\n");
        var current = service.Get(session.SessionId)!;
        var initial = await _reader.ReadLatestAsync(current, SessionLogSourceKind.GameLog);

        File.AppendAllText(path, "three\n");
        var next = await _reader.ReadFromAsync(
            service.Get(session.SessionId)!,
            SessionLogSourceKind.GameLog,
            initial.EndOffset);

        Assert.Equal(["three"], next.Lines);
        Assert.Equal(initial.EndOffset, next.StartOffset);
    }

    [Fact]
    public async Task ReadFromDetectsTruncatedFileAndRestarts()
    {
        var service = Service();
        var session = service.Create(Request());
        var path = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        var current = service.Get(session.SessionId)!;

        var chunk = await _reader.ReadFromAsync(current, SessionLogSourceKind.GameLog, offset: 999);

        Assert.Equal(0, chunk.StartOffset);
        Assert.Equal(["one", "two", "three"], chunk.Lines);
    }

    [Fact]
    public void SharedLatestLogIsMarkedAmbiguousAndNeverDirect()
    {
        var service = Service();
        var gameDirectory = Path.Combine(_environment.Root, "shared-game");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "logs"));
        File.WriteAllText(Path.Combine(gameDirectory, "logs", "latest.log"), "shared\n");
        var session = service.Create(Request() with { GameDirectory = gameDirectory, LogIsolation = "shared" });

        var source = _reader.DescribeSources(service.Get(session.SessionId)!)
            .Single(item => item.Kind == SessionLogSourceKind.GameLog);

        Assert.Equal(ArtifactOwnership.SharedOrAmbiguous, source.Ownership);
        Assert.Equal(SessionSourceAvailability.Available, source.Availability);
        Assert.NotEmpty(source.Warning ?? string.Empty);
    }

    [Fact]
    public void EvidenceIncludesOwnershipAndRemoteMissingState()
    {
        var service = Service();
        var session = service.Create(Request());
        var report = Path.Combine(session.DirectoryPath, "crash.txt");
        File.WriteAllText(report, "crash");
        service.RegisterArtifact(session.SessionId, new SessionArtifact
        {
            Id = "client_crash_report:crash.txt",
            Kind = SessionArtifactKinds.ClientCrashReport,
            SourcePath = report,
            CapturedPath = report,
            Ownership = ArtifactOwnership.StronglyCorrelated,
        });
        service.RegisterArtifact(session.SessionId, new SessionArtifact
        {
            Id = "jvm_fatal_error:missing.log",
            Kind = SessionArtifactKinds.JvmFatalError,
            SourcePath = Path.Combine(session.DirectoryPath, "missing.log"),
            Ownership = ArtifactOwnership.Possible,
        });

        var evidence = _reader.DescribeEvidence(service.Get(session.SessionId)!);

        Assert.Equal(2, evidence.Count);
        Assert.Equal(SessionSourceAvailability.Available, evidence[0].Availability);
        Assert.Equal(ArtifactOwnership.StronglyCorrelated, evidence[0].Artifact.Ownership);
        Assert.Equal(SessionSourceAvailability.Missing, evidence[1].Availability);
        Assert.NotNull(evidence[1].Detail);
    }

    [Fact]
    public async Task EvidenceReadIsReadOnlyAndDoesNotModifySource()
    {
        var service = Service();
        var session = service.Create(Request());
        var report = Path.Combine(session.DirectoryPath, "crash.txt");
        await File.WriteAllTextAsync(report, "first\nsecond\n");
        var artifact = new SessionArtifact
        {
            Id = "client_crash_report:crash.txt",
            Kind = SessionArtifactKinds.ClientCrashReport,
            SourcePath = report,
            CapturedPath = report,
        };

        var chunk = await _reader.ReadEvidenceAsync(service.Get(session.SessionId)!, artifact);

        Assert.Equal(["first", "second"], chunk.Lines);
        Assert.Equal("first\nsecond\n", await File.ReadAllTextAsync(report));
    }

    public void Dispose() => _environment.Dispose();
}
