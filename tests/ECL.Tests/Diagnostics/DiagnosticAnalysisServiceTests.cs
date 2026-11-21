// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Diagnostics;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Diagnostics;

/// <summary>
/// 诊断编排契约：输入只来自会话证据（不枚举实例目录）、共享日志保留归属警告、
/// 缺证据/缺知识库仍生成报告、事实源异常与历史写入失败降级为警告/日志。
/// </summary>
public sealed class DiagnosticAnalysisServiceTests : IDisposable
{
    private const string KnowledgeJson = """
        {
          "schemaVersion": 1,
          "knowledgeVersion": "2026.11.1",
          "publishedAtUtc": "2026-11-01T00:00:00Z",
          "rules": [
            {
              "id": "mods.missing-dependency",
              "version": 1,
              "title": "缺少前置模组",
              "severity": "error",
              "category": "mods",
              "pattern": "Could not find required mod",
              "minOccurrences": 1,
              "confidence": "highlyLikely",
              "explanation": "日志明确报告缺少前置模组，但具体模组仍需结合安装列表核对。",
              "suggestedActions": ["安装缺失的前置模组"],
              "sourceIds": ["adr-0001"]
            }
          ],
          "sources": [
            { "id": "adr-0001", "title": "ADR 0001" }
          ]
        }
        """;

    private const string MatchingLog = """
        [10:00:00] [main/INFO]: Launching game
        [10:00:01] [main/ERROR]: Could not find required mod: examplemod
        """;

    private const string BenignLog = """
        [10:00:00] [main/INFO]: Launching game
        [10:00:01] [main/INFO]: Done
        """;

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly GameSessionService _sessions;
    private readonly FakeInstanceService _instances;
    private readonly CountingLogReader _reader = new();
    private readonly GameInstance _instance;
    private readonly string _knowledgePath;

    public DiagnosticAnalysisServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ecl-diagnostic-service", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _sessions = new GameSessionService(_paths, NullLogger<GameSessionService>.Instance);
        _instance = new GameInstance
        {
            Id = "demo",
            Name = "Demo",
            GameVersion = "1.21.1",
            VersionId = "1.21.1",
            GameDirectory = Path.Combine(_root, "game-demo"),
        };
        _instances = new FakeInstanceService(_instance);
        _knowledgePath = Path.Combine(_root, "knowledge.json");
        File.WriteAllText(_knowledgePath, KnowledgeJson);
    }

    [Fact]
    public async Task AnalyzeReadsOnlySessionEvidenceAndSavesInstanceHistory()
    {
        var session = _sessions.Create(CreateRequest());
        _sessions.SetLaunchJava(session.SessionId, @"C:\Java\bin\java.exe", "21.0.1");
        session = AddGameLogArtifact(session, BenignLog);

        // 实例目录里放一份“能命中规则”的 latest.log；只读会话证据时它绝不能被使用。
        var instanceLogs = Path.Combine(_instance.GameDirectory!, "logs");
        Directory.CreateDirectory(instanceLogs);
        File.WriteAllText(Path.Combine(instanceLogs, "latest.log"), MatchingLog);

        var service = CreateService();
        var result = await service.AnalyzeAsync("demo", new DiagnosticAnalysisOptions
        {
            KnowledgeBasePath = _knowledgePath,
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.Empty(report.Findings);
        Assert.Equal("demo", report.InstanceId);
        Assert.Equal(session.SessionId, report.SessionId);
        Assert.Equal("1.21.1", report.Environment!.MinecraftVersion);
        Assert.Equal("fabric", report.Environment.Loader);
        Assert.Equal("21.0.1", report.Environment.JavaVersion);
        Assert.Equal(1, _reader.DescribeEvidenceCalls);
        Assert.Equal(1, _reader.ReadEvidenceCalls);

        var history = await service.LoadHistoryAsync("demo");
        Assert.True(history.IsSuccess, history.Error?.ToString());
        Assert.Single(history.Value!);
    }

    [Fact]
    public async Task SharedOrAmbiguousGameLogKeepsOwnershipWarning()
    {
        var session = _sessions.Create(CreateRequest());
        session = AddGameLogArtifact(session, MatchingLog, ArtifactOwnership.SharedOrAmbiguous);

        var result = await CreateService().AnalyzeAsync("demo", new DiagnosticAnalysisOptions
        {
            KnowledgeBasePath = _knowledgePath,
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.Single(report.Findings);
        Assert.Equal(1, _reader.ReadEvidenceCalls);
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("共享日志归属不确定", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingEvidenceProducesInputIssueButStillReturnsReport()
    {
        var session = _sessions.Create(CreateRequest());
        _sessions.RegisterArtifact(session.SessionId, new SessionArtifact
        {
            Id = "game_log:missing.log",
            Kind = SessionArtifactKinds.GameLog,
            SourcePath = Path.Combine(_root, "does-not-exist.log"),
            Ownership = ArtifactOwnership.Direct,
        });

        var result = await CreateService().AnalyzeAsync("demo", new DiagnosticAnalysisOptions
        {
            KnowledgeBasePath = _knowledgePath,
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.Empty(report.Findings);
        Assert.Contains(report.InputIssues, issue => issue.Code == "diagnostics.evidence_missing");
        Assert.Equal(0, _reader.ReadEvidenceCalls);
    }

    [Fact]
    public async Task MissingKnowledgeBaseIsReportedAndProducesNoFindings()
    {
        var session = _sessions.Create(CreateRequest());
        session = AddGameLogArtifact(session, MatchingLog);

        var result = await CreateService().AnalyzeAsync("demo", new DiagnosticAnalysisOptions
        {
            KnowledgeBasePath = Path.Combine(_root, "missing-knowledge.json"),
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.Null(report.KnowledgeBase);
        Assert.Empty(report.Findings);
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("未加载知识库", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FactSourceExceptionIsDegradedToWarning()
    {
        var session = _sessions.Create(CreateRequest());
        session = AddGameLogArtifact(session, BenignLog);

        var service = CreateService([new ThrowingFactSource()]);
        var result = await service.AnalyzeAsync("demo", new DiagnosticAnalysisOptions
        {
            KnowledgeBasePath = _knowledgePath,
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.Empty(report.Facts);
        Assert.Contains(report.Warnings, warning => warning.Contains("事实源", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HistoryWriteFailureDoesNotAffectReturnedReport()
    {
        var session = _sessions.Create(CreateRequest());
        session = AddGameLogArtifact(session, BenignLog);

        // 历史根目录的父路径是一个文件：Directory.CreateDirectory 必然失败。
        var blockedPath = Path.Combine(_root, "blocked");
        File.WriteAllText(blockedPath, "not a directory");
        var failingHistory = new DiagnosticHistoryStore(Path.Combine(blockedPath, "diagnostics"));

        var service = CreateService(history: failingHistory);
        var result = await service.AnalyzeAsync("demo", new DiagnosticAnalysisOptions
        {
            KnowledgeBasePath = _knowledgePath,
            SaveHistory = true,
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.NotNull(result.Value);
    }

    [Fact]
    public async Task NoSessionStillReturnsReportAndExplainsMissingEvidence()
    {
        var result = await CreateService().AnalyzeAsync("demo", new DiagnosticAnalysisOptions
        {
            KnowledgeBasePath = _knowledgePath,
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.Null(report.SessionId);
        Assert.Empty(report.Findings);
        Assert.Contains(report.InputIssues, issue => issue.Code == "diagnostics.no_session");
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("未找到该实例的游戏会话", StringComparison.Ordinal));
        Assert.Equal(0, _reader.DescribeEvidenceCalls);
    }

    [Fact]
    public async Task SessionFactsComeFromManifestAndCaptureAccounting()
    {
        var session = _sessions.Create(CreateRequest());
        _sessions.SetLaunchJava(session.SessionId, @"C:\Java\bin\java.exe", "21.0.1");
        _sessions.SetExitCode(session.SessionId, 1);
        _sessions.SetCaptureAccounting(
            session.SessionId,
            SessionCaptureModes.Console,
            writtenBytes: 0,
            droppedBytes: 0,
            warning: SessionWarnings.CaptureConsole);

        var result = await CreateService().AnalyzeAsync("demo", new DiagnosticAnalysisOptions
        {
            KnowledgeBasePath = _knowledgePath,
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var kinds = result.Value!.Facts.Select(fact => fact.Kind).ToArray();

        Assert.Contains(DiagnosticFactKinds.JavaVersion, kinds);
        Assert.Contains(DiagnosticFactKinds.Loader, kinds);
        Assert.Contains(DiagnosticFactKinds.ExitCode, kinds);
        Assert.Contains(DiagnosticFactKinds.CaptureMode, kinds);
        Assert.Contains(DiagnosticFactKinds.ConsoleCaptureGap, kinds);
        Assert.All(result.Value.Facts, fact => Assert.Equal(DiagnosticFactConfidence.SessionManifest, fact.SourceId));
    }

    [Fact]
    public async Task UnknownInstanceReturnsInstanceNotFound()
    {
        var result = await CreateService().AnalyzeAsync("missing", new DiagnosticAnalysisOptions
        {
            KnowledgeBasePath = _knowledgePath,
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("instance.not_found", result.Error!.Code);
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
        catch (IOException)
        {
            // 测试清理失败不影响结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    private SessionCreateRequest CreateRequest() => new()
    {
        InstanceId = "demo",
        InstanceName = "Demo",
        GameDirectory = _instance.GameDirectory,
        MinecraftVersion = "1.21.1",
        Loader = "fabric",
        LogIsolation = "direct",
    };

    private GameLogSession AddGameLogArtifact(
        GameLogSession session,
        string text,
        ArtifactOwnership ownership = ArtifactOwnership.Direct)
    {
        var path = Path.Combine(session.ArtifactsDirectory, "game.log");
        Directory.CreateDirectory(session.ArtifactsDirectory);
        File.WriteAllText(path, text);
        _sessions.RegisterArtifact(session.SessionId, new SessionArtifact
        {
            Id = "game_log:game.log",
            Kind = SessionArtifactKinds.GameLog,
            SourcePath = path,
            CapturedPath = path,
            Size = new FileInfo(path).Length,
            Ownership = ownership,
        });
        return _sessions.Get(session.SessionId)!;
    }

    private DiagnosticAnalysisService CreateService(
        IEnumerable<IDiagnosticFactSource>? factSources = null,
        DiagnosticHistoryStore? history = null) =>
        new(
            _instances,
            _sessions,
            _reader,
            new DiagnosticEngine(),
            factSources ?? [new SessionFactSource()],
            new StubWorldFactSource(),
            history ?? new DiagnosticHistoryStore(Path.Combine(_root, "history")),
            NullLogger<DiagnosticAnalysisService>.Instance);

    private sealed class ThrowingFactSource : IDiagnosticFactSource
    {
        public string Id => "test.throwing";

        public Task<IReadOnlyList<DiagnosticFact>> CollectAsync(
            DiagnosticFactContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("fact source failure");
    }

    /// <summary>默认世界事实源不参与断言：未显式开启 IncludeWorldEvents 时编排层不应调用它。</summary>
    private sealed class StubWorldFactSource : IDiagnosticWorldFactSource
    {
        public int Calls { get; private set; }

        public Task<DiagnosticWorldFactResult> CollectAsync(
            DiagnosticFactContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new DiagnosticWorldFactResult([], [], []));
        }
    }

    private sealed class CountingLogReader : ISessionLogReader
    {
        private readonly SessionLogReader _inner = new();

        public int DescribeEvidenceCalls { get; private set; }

        public int ReadEvidenceCalls { get; private set; }

        public IReadOnlyList<SessionLogSourceInfo> DescribeSources(GameLogSession session) =>
            _inner.DescribeSources(session);

        public IReadOnlyList<SessionEvidenceItem> DescribeEvidence(GameLogSession session)
        {
            DescribeEvidenceCalls++;
            return _inner.DescribeEvidence(session);
        }

        public Task<SessionLogChunk> ReadLatestAsync(
            GameLogSession session,
            SessionLogSourceKind kind,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default) =>
            _inner.ReadLatestAsync(session, kind, maxBytes, cancellationToken);

        public Task<SessionLogChunk> ReadFromAsync(
            GameLogSession session,
            SessionLogSourceKind kind,
            long offset,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default) =>
            _inner.ReadFromAsync(session, kind, offset, maxBytes, cancellationToken);

        public Task<SessionLogChunk> ReadBeforeAsync(
            GameLogSession session,
            SessionLogSourceKind kind,
            long beforeOffset,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default) =>
            _inner.ReadBeforeAsync(session, kind, beforeOffset, maxBytes, cancellationToken);

        public Task<SessionLogChunk> ReadEvidenceAsync(
            GameLogSession session,
            SessionArtifact artifact,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            SessionEvidenceAnchor anchor = SessionEvidenceAnchor.Tail,
            CancellationToken cancellationToken = default)
        {
            ReadEvidenceCalls++;
            return _inner.ReadEvidenceAsync(session, artifact, maxBytes, anchor, cancellationToken);
        }

        public Task<SessionLogChunk> ReadEvidenceBeforeAsync(
            GameLogSession session,
            SessionArtifact artifact,
            long beforeOffset,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default) =>
            _inner.ReadEvidenceBeforeAsync(session, artifact, beforeOffset, maxBytes, cancellationToken);

        public Task<SessionLogChunk> ReadEvidenceAfterAsync(
            GameLogSession session,
            SessionArtifact artifact,
            long afterOffset,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default) =>
            _inner.ReadEvidenceAfterAsync(session, artifact, afterOffset, maxBytes, cancellationToken);
    }

    private sealed class FakeInstanceService(GameInstance? instance) : IInstanceService
    {
        public Task<IReadOnlyList<GameInstance>> GetInstancesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameInstance>>(instance is null ? [] : [instance]);

        public Task<GameInstance?> GetAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(instance?.Id == instanceId ? instance : null);

        public Task<Result<GameInstance>> CreateAsync(
            string name,
            string gameVersion,
            string loader = "vanilla",
            string? loaderVersion = null,
            string? fabricApiVersion = null,
            string? optiFineVersion = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> UpdateConfigurationAsync(
            string instanceId,
            string gameVersion,
            string loader,
            string? loaderVersion = null,
            string? fabricApiVersion = null,
            string? optiFineVersion = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> RestoreConfigurationAsync(
            GameInstance restorePoint,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> DeleteAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstanceDeletionPlan>> GetDeletionPlanAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> DeleteAsync(
            string instanceId,
            InstanceDeleteOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DetectedVersion>> ScanVersionsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> ImportAsync(
            string versionId,
            string? name = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task TouchLastPlayedAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> RelocateAsync(
            string instanceId,
            string newGameDirectory,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> CloneAsync(
            string sourceInstanceId,
            string name,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
