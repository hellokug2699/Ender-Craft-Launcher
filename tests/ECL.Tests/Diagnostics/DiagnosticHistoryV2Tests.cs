// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Json;
using Ecl.Diagnostics;
using Xunit;

namespace Ecl.Tests.Diagnostics;

/// <summary>
/// 实例级诊断历史（schema 2）：实例目录隔离、schema 1/2 兼容、损坏跳过、
/// 每实例 50 条裁剪与原子写入不残留 .tmp。
/// </summary>
public sealed class DiagnosticHistoryV2Tests : IDisposable
{
    private readonly string _root;
    private readonly DiagnosticHistoryStore _store;

    public DiagnosticHistoryV2Tests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ecl-diagnostic-history-v2", Guid.NewGuid().ToString("N"));
        _store = new DiagnosticHistoryStore(Path.Combine(_root, "diagnostics"));
    }

    [Fact]
    public void InstanceReportsLiveUnderTheirOwnDirectoryAndStayIsolated()
    {
        var alpha = NewReport("alpha", "a1", DateTimeOffset.Parse("2026-11-01T10:00:00Z"));
        var beta = NewReport("beta", "b1", DateTimeOffset.Parse("2026-11-01T11:00:00Z"));

        var savedAlpha = _store.Save(alpha);
        var savedBeta = _store.Save(beta);

        Assert.True(savedAlpha.IsSuccess, savedAlpha.Error?.ToString());
        Assert.True(savedBeta.IsSuccess, savedBeta.Error?.ToString());
        Assert.Contains(Path.Combine("alpha", "a1.json"), savedAlpha.Value!, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("beta", "b1.json"), savedBeta.Value!, StringComparison.Ordinal);

        var loadedAlpha = _store.LoadAll("alpha");
        Assert.True(loadedAlpha.IsSuccess, loadedAlpha.Error?.ToString());
        Assert.Equal(["a1"], loadedAlpha.Value!.Reports.Select(report => report.ReportId));
        var alphaReport = Assert.Single(loadedAlpha.Value.Reports);
        var fact = Assert.Single(alphaReport.Facts);
        Assert.Equal(DiagnosticFactKinds.JavaVersion, fact.Kind);
        Assert.Equal("21.0.1", fact.Subject);
        Assert.Equal("C:\\Java\\bin\\java.exe", fact.Attributes["javaPath"]);
        var hypothesis = Assert.Single(Assert.Single(alphaReport.Findings).Hypotheses);
        Assert.Equal("候选原因", hypothesis.Statement);
        Assert.Equal(["更多日志"], hypothesis.MissingEvidence);

        var loadedBeta = _store.LoadAll("beta");
        Assert.True(loadedBeta.IsSuccess, loadedBeta.Error?.ToString());
        Assert.Equal(["b1"], loadedBeta.Value!.Reports.Select(report => report.ReportId));

        // 根目录只保留没有实例归属的旧报告，不会把所有实例历史混在一起。
        var loadedRoot = _store.LoadAll();
        Assert.True(loadedRoot.IsSuccess, loadedRoot.Error?.ToString());
        Assert.Empty(loadedRoot.Value!.Reports);
    }

    [Fact]
    public void SchemaOneAndTwoReportsRemainReadableWithoutMigrationWrites()
    {
        var current = _store.Save(NewReport("alpha", "v2", DateTimeOffset.Parse("2026-11-01T12:00:00Z")));
        Assert.True(current.IsSuccess, current.Error?.ToString());

        var legacy = new DiagnosticReport
        {
            SchemaVersion = 1,
            ReportId = "legacy1",
            GeneratedAtUtc = DateTimeOffset.Parse("2026-10-01T09:00:00Z"),
        };
        var legacyPath = Path.Combine(_store.DirectoryPath, "legacy1.json");
        Directory.CreateDirectory(_store.DirectoryPath);
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy, EclJson.Indented));
        var legacyBytes = File.ReadAllBytes(legacyPath);

        var loaded = _store.LoadAll();

        Assert.True(loaded.IsSuccess, loaded.Error?.ToString());
        var report = Assert.Single(loaded.Value!.Reports);
        Assert.Equal("legacy1", report.ReportId);
        Assert.Equal(1, report.SchemaVersion);

        // 读取旧 schema 不得原地改写为 schema 2。
        Assert.Equal(legacyBytes, File.ReadAllBytes(legacyPath));
    }

    [Fact]
    public void CorruptAndFutureSchemaFilesAreSkippedWithIssues()
    {
        var saved = _store.Save(NewReport("alpha", "ok", DateTimeOffset.Parse("2026-11-01T12:00:00Z")));
        Assert.True(saved.IsSuccess, saved.Error?.ToString());
        var directory = Path.GetDirectoryName(saved.Value!)!;
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{ not valid json");

        var future = new DiagnosticReport
        {
            SchemaVersion = DiagnosticReport.CurrentSchemaVersion + 1,
            ReportId = "future",
            GeneratedAtUtc = DateTimeOffset.Parse("2026-11-02T12:00:00Z"),
        };
        File.WriteAllText(
            Path.Combine(directory, "future.json"),
            JsonSerializer.Serialize(future, EclJson.Indented));

        var loaded = _store.LoadAll("alpha");

        Assert.True(loaded.IsSuccess, loaded.Error?.ToString());
        Assert.Equal(["ok"], loaded.Value!.Reports.Select(report => report.ReportId));
        Assert.Contains(loaded.Value.Issues, issue => issue.Code == "history.corrupt");
        Assert.Contains(loaded.Value.Issues, issue => issue.Code == "history.schema_incompatible");
    }

    [Fact]
    public void InstanceHistoryIsPrunedToFiftyNewestByGeneratedAt()
    {
        var start = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        for (var index = 0; index < 55; index++)
        {
            var saved = _store.Save(NewReport(
                "alpha",
                $"report-{index:000}",
                start.AddMinutes(index)));
            Assert.True(saved.IsSuccess, saved.Error?.ToString());
        }

        var loaded = _store.LoadAll("alpha");

        Assert.True(loaded.IsSuccess, loaded.Error?.ToString());
        var reports = loaded.Value!.Reports;
        Assert.Equal(DiagnosticHistoryStore.MaxReportsPerInstance, reports.Count);
        Assert.DoesNotContain(reports, report => report.ReportId == "report-000");
        Assert.DoesNotContain(reports, report => report.ReportId == "report-004");
        Assert.Contains(reports, report => report.ReportId == "report-005");
        Assert.Contains(reports, report => report.ReportId == "report-054");
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(_store.DirectoryPath, "alpha"),
            "*.tmp"));
    }

    [Fact]
    public void DeleteRemovesOnlyTheRequestedInstancesReport()
    {
        _store.Save(NewReport("alpha", "a1", DateTimeOffset.Parse("2026-11-01T10:00:00Z")));
        _store.Save(NewReport("beta", "b1", DateTimeOffset.Parse("2026-11-01T11:00:00Z")));

        var deleted = _store.Delete("alpha", "a1");

        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.Empty(_store.LoadAll("alpha").Value!.Reports);
        Assert.Single(_store.LoadAll("beta").Value!.Reports);

        var loaded = _store.Load("alpha", "a1");
        Assert.True(loaded.IsSuccess);
        Assert.Null(loaded.Value);
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

    private static DiagnosticReport NewReport(
        string instanceId,
        string reportId,
        DateTimeOffset generatedAt) => new()
    {
        SchemaVersion = DiagnosticReport.CurrentSchemaVersion,
        ReportId = reportId,
        InstanceId = instanceId,
        SessionId = "session-" + instanceId,
        GeneratedAtUtc = generatedAt,
        Facts =
        [
            DiagnosticFact.Create(
                DiagnosticFactKinds.JavaVersion,
                "21.0.1",
                DiagnosticFactConfidence.SessionManifest,
                attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["javaPath"] = @"C:\Java\bin\java.exe",
                }),
        ],
        Findings =
        [
            new DiagnosticFinding
            {
                RuleId = "test.rule",
                RuleVersion = 1,
                KnowledgeVersion = "2026.11.1",
                Title = "测试结论",
                Severity = DiagnosticSeverity.Warning,
                Confidence = DiagnosticConfidence.HighlyLikely,
                Category = "test",
                Occurrences = 1,
                Evidence = [new DiagnosticEvidence { File = "latest.log", Line = 3, Excerpt = "evidence" }],
                Explanation = "用于测试。",
                Hypotheses =
                [
                    new DiagnosticHypothesis(
                        "候选原因",
                        DiagnosticConfidence.HighlyLikely,
                        [],
                        ["更多日志"]),
                ],
            },
        ],
    };
}
