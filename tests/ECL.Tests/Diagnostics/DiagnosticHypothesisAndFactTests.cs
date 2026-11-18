// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Diagnostics;
using Xunit;

namespace Ecl.Tests.Diagnostics;

/// <summary>
/// 事实约束与候选原因契约：规则要求的会话事实缺失时不命中；满足后 finding 携带
/// 候选原因并在 SourceIds 中追加事实来源；performance/启发式事实不得升级为直接证据。
/// </summary>
public sealed class DiagnosticHypothesisAndFactTests
{
    private const string KnowledgeJson = """
        {
          "schemaVersion": 1,
          "knowledgeVersion": "2026.11.1",
          "publishedAtUtc": "2026-11-01T00:00:00Z",
          "rules": [
            {
              "id": "session.abnormal-exit",
              "version": 2,
              "title": "会话异常退出",
              "severity": "error",
              "category": "session",
              "pattern": "session ended unexpectedly",
              "minOccurrences": 1,
              "confidence": "highlyLikely",
              "explanation": "会话在未记录正常退出流程的情况下结束，需要结合退出码与采集计量判断。",
              "requiresFacts": ["session.exit_code"],
              "hypotheses": ["Java 运行时异常退出（候选原因，仍需其他证据）。"],
              "sourceIds": ["adr-0001"]
            }
          ],
          "sources": [
            { "id": "adr-0001", "title": "ADR 0001" }
          ]
        }
        """;

    private const string GameLog = """
        [10:00:00] [main/INFO]: Launching game
        [10:00:01] [main/ERROR]: session ended unexpectedly
        """;

    [Fact]
    public void RuleWithMissingFactDoesNotMatch()
    {
        var report = Analyze(facts: []);

        Assert.Empty(report.Findings);
        Assert.Empty(report.Facts);
    }

    [Fact]
    public void RuleWithFactMatchesAndCarriesHypothesesAndSourceId()
    {
        var fact = DiagnosticFact.Create(
            DiagnosticFactKinds.ExitCode,
            "1",
            DiagnosticFactConfidence.SessionManifest);

        var report = Analyze([fact]);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("session.abnormal-exit", finding.RuleId);
        Assert.Contains("adr-0001", finding.SourceIds);
        Assert.Contains(DiagnosticFactConfidence.SessionManifest, finding.SourceIds);

        var hypothesis = Assert.Single(finding.Hypotheses);
        Assert.Equal("Java 运行时异常退出（候选原因，仍需其他证据）。", hypothesis.Statement);
        Assert.Equal(DiagnosticConfidence.HighlyLikely, hypothesis.Confidence);
        Assert.Single(hypothesis.SupportingEvidence);
        Assert.Empty(hypothesis.MissingEvidence);

        var reportedFact = Assert.Single(report.Facts);
        Assert.Equal(DiagnosticFactKinds.ExitCode, reportedFact.Kind);
        Assert.Equal("1", reportedFact.Subject);
        Assert.Equal(DiagnosticConfidence.DirectEvidence, reportedFact.Confidence);
    }

    [Fact]
    public void PerformanceAndHeuristicFactsCannotExceedHighlyLikely()
    {
        var performance = DiagnosticFact.Create(
            "performance.tps",
            "12",
            DiagnosticFactConfidence.Performance,
            confidence: DiagnosticConfidence.DirectEvidence);
        var heuristic = DiagnosticFact.Create(
            "crash.suspect",
            "examplemod",
            DiagnosticFactConfidence.CrashHeuristic,
            confidence: DiagnosticConfidence.DirectEvidence);
        var session = DiagnosticFact.Create(
            DiagnosticFactKinds.ExitCode,
            "0",
            DiagnosticFactConfidence.SessionManifest,
            confidence: DiagnosticConfidence.DirectEvidence);

        Assert.Equal(DiagnosticConfidence.HighlyLikely, performance.Confidence);
        Assert.Equal(DiagnosticConfidence.HighlyLikely, heuristic.Confidence);
        Assert.Equal(DiagnosticConfidence.DirectEvidence, session.Confidence);
        Assert.True(DiagnosticFactConfidence.IsInferenceSource(DiagnosticFactConfidence.Performance));
        Assert.True(DiagnosticFactConfidence.IsInferenceSource("heuristic.anything"));
        Assert.False(DiagnosticFactConfidence.IsInferenceSource(DiagnosticFactConfidence.SessionManifest));
    }

    [Fact]
    public void EngineNormalizesFactsConstructedWithoutFactory()
    {
        // 故意把 Confidence 写在 SourceId 之前，绕开 init 上限；引擎仍必须压回 HighlyLikely。
        var uncapped = new DiagnosticFact
        {
            Kind = "performance.memory",
            Subject = "512",
            Confidence = DiagnosticConfidence.DirectEvidence,
            SourceId = DiagnosticFactConfidence.Performance,
        };

        var report = Analyze([uncapped]);

        var fact = Assert.Single(report.Facts);
        Assert.Equal(DiagnosticConfidence.HighlyLikely, fact.Confidence);
    }

    [Fact]
    public void KnowledgeLoaderReadsOptionalFactAndHypothesisFields()
    {
        var knowledge = LoadKnowledge();

        var rule = Assert.Single(knowledge.Rules);
        Assert.Equal(["session.exit_code"], rule.RequiresFacts);
        Assert.Single(rule.Hypotheses);
        Assert.Equal(DiagnosticConfidence.HighlyLikely, rule.Confidence);
    }

    private static DiagnosticReport Analyze(IReadOnlyList<DiagnosticFact> facts)
    {
        var events = LatestLogParser.Parse("latest.log", GameLog).Events;
        return new DiagnosticEngine().Analyze(new DiagnosticAnalysisRequest
        {
            Events = events,
            KnowledgeBase = LoadKnowledge(),
            Facts = facts,
            InstanceId = "demo",
            SessionId = "session-1",
            AnalyzedAtUtc = DateTimeOffset.Parse("2026-11-03T00:00:00Z"),
        });
    }

    private static KnowledgeBase LoadKnowledge()
    {
        var loaded = KnowledgeBaseLoader.Load(KnowledgeJson);
        Assert.True(loaded.IsSuccess, loaded.Error?.ToString());
        return loaded.Value!;
    }
}
