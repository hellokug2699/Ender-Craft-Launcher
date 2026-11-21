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
        var session = service.Create(Request() with { GameDirectory = gameDirectory, LogIsolation = "shared" });
        // 共享日志只有在本次会话创建之后被写入，才可能是本次启动的输出。
        File.WriteAllText(Path.Combine(gameDirectory, "logs", "latest.log"), "shared\n");

        var source = _reader.DescribeSources(service.Get(session.SessionId)!)
            .Single(item => item.Kind == SessionLogSourceKind.GameLog);

        Assert.Equal(ArtifactOwnership.SharedOrAmbiguous, source.Ownership);
        Assert.Equal(SessionSourceAvailability.Available, source.Availability);
        Assert.NotEmpty(source.Warning ?? string.Empty);
    }

    [Fact]
    public async Task PendingIsolationShowsTheSessionLogInsteadOfTheSharedLatestLog()
    {
        var service = Service();
        var gameDirectory = Path.Combine(_environment.Root, "pending-game");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "logs"));
        var stale = Path.Combine(gameDirectory, "logs", "latest.log");
        File.WriteAllText(stale, "previous-run\n");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        // 启动时记录 pending：会话独立日志配置已生成，但退出前还没有复核。
        // 这不改变归属——会话目录里的 game.log 只可能由本次启动写入。
        var session = service.Create(Request() with
        {
            GameDirectory = gameDirectory,
            LogIsolation = SessionLogIsolationModes.Pending,
        });
        var isolated = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllText(isolated, "[10:00:00] [main/INFO]: current-run\n");

        var current = service.Get(session.SessionId)!;
        var source = _reader.DescribeSources(current).Single(item => item.Kind == SessionLogSourceKind.GameLog);

        Assert.Equal(SessionSourceAvailability.Available, source.Availability);
        Assert.Equal(ArtifactOwnership.Direct, source.Ownership);
        Assert.Equal(isolated, source.Path);

        var chunk = await _reader.ReadLatestAsync(current, SessionLogSourceKind.GameLog);
        Assert.Equal(["[10:00:00] [main/INFO]: current-run"], chunk.Lines);
    }

    [Fact]
    public void RegisteredDirectArtifactKeepsItsOwnershipAndWarning()
    {
        var service = Service();
        var session = service.Create(Request() with { LogIsolation = SessionLogIsolationModes.Pending });
        var isolated = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllText(isolated, "line\n");
        service.RegisterArtifact(session.SessionId, new SessionArtifact
        {
            Id = "game_log:game.log",
            Kind = SessionArtifactKinds.GameLog,
            SourcePath = isolated,
            CapturedPath = isolated,
            Ownership = ArtifactOwnership.Direct,
            Warning = "采集宿主已退出，使用会话独立文件日志作为证据。",
        });

        var source = _reader.DescribeSources(service.Get(session.SessionId)!)
            .Single(item => item.Kind == SessionLogSourceKind.GameLog);

        // 登记后不能丢掉证据自带的采集说明。
        Assert.Equal(isolated, source.Path);
        Assert.Equal(ArtifactOwnership.Direct, source.Ownership);
        Assert.Equal("采集宿主已退出，使用会话独立文件日志作为证据。", source.Warning);
    }

    [Fact]
    public void MissingRegisteredArtifactFallsBackToTheSessionLog()
    {
        var service = Service();
        var session = service.Create(Request() with { LogIsolation = SessionLogIsolationModes.Pending });
        var isolated = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllText(isolated, "line\n");
        service.RegisterArtifact(session.SessionId, new SessionArtifact
        {
            Id = "game_log:copied.log",
            Kind = SessionArtifactKinds.GameLog,
            SourcePath = Path.Combine(session.DirectoryPath, "deleted.log"),
            Ownership = ArtifactOwnership.SharedOrAmbiguous,
        });

        var source = _reader.DescribeSources(service.Get(session.SessionId)!)
            .Single(item => item.Kind == SessionLogSourceKind.GameLog);

        // 证据文件已不存在时，仍可使用的会话独立日志优先于“缺失”状态。
        Assert.Equal(isolated, source.Path);
        Assert.Equal(SessionSourceAvailability.Available, source.Availability);
        Assert.Equal(ArtifactOwnership.Direct, source.Ownership);
    }

    [Fact]
    public void EmptySessionLogIsNotReportedAsAvailable()
    {
        var service = Service();
        var session = service.Create(Request());
        var isolated = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllBytes(isolated, []);

        var source = _reader.DescribeSources(service.Get(session.SessionId)!)
            .Single(item => item.Kind == SessionLogSourceKind.GameLog);

        Assert.Equal(SessionSourceAvailability.NotGenerated, source.Availability);
        Assert.Null(source.Path);
        Assert.False(string.IsNullOrWhiteSpace(source.Warning));
    }

    [Fact]
    public void SharedLatestLogFromAnEarlierRunIsNotPresentedAsThisSession()
    {
        var service = Service();
        var gameDirectory = Path.Combine(_environment.Root, "stale-shared-game");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "logs"));
        var latest = Path.Combine(gameDirectory, "logs", "latest.log");
        File.WriteAllText(latest, "previous-run\n");
        File.SetLastWriteTimeUtc(latest, DateTime.UtcNow.AddHours(-1));

        var session = service.Create(Request() with
        {
            GameDirectory = gameDirectory,
            LogIsolation = SessionLogIsolationModes.Shared,
        });

        var source = _reader.DescribeSources(service.Get(session.SessionId)!)
            .Single(item => item.Kind == SessionLogSourceKind.GameLog);

        // 上一次启动的 latest.log 不能冒充本次会话日志。
        Assert.Equal(SessionSourceAvailability.NotGenerated, source.Availability);
        Assert.Null(source.Path);
        Assert.False(string.IsNullOrWhiteSpace(source.Warning));
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
