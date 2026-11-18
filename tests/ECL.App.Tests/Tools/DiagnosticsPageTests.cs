// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Diagnostics;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Instances;
using Xunit;

namespace Ecl.App.Tests.Tools;

/// <summary>
/// 工具中心「本地诊断」页：没有知识库时明确只做结构化解析；有结果时同时呈现
/// 证据、候选原因与历史；分析只经过注入的服务，不访问网络或枚举实例目录。
/// </summary>
public sealed class DiagnosticsPageTests
{
    [Fact]
    public async Task WithoutKnowledgeBaseThePageSaysStructuredParsingOnly()
    {
        var host = new Host(NewReport(withKnowledgeBase: false));

        await host.Page.ActivateAsync();

        Assert.Single(host.Page.Instances);
        Assert.Equal("demo", host.Page.SelectedInstance!.Id);
        Assert.Contains("未加载知识库", host.Page.KnowledgeNote, StringComparison.Ordinal);
        Assert.Empty(host.Page.Findings);
        Assert.Empty(host.Page.History);
        Assert.Equal(0, host.Analysis.AnalyzeCalls);
        Assert.Equal(1, host.Analysis.HistoryCalls);
    }

    [Fact]
    public async Task AnalysisRendersFindingsEvidenceHypothesesAndHistoryFromInjectedService()
    {
        var host = new Host(NewReport(withKnowledgeBase: true));
        host.Analysis.History = [NewReport(withKnowledgeBase: true)];

        await host.Page.ActivateAsync();
        await host.Page.AnalyzeCommand.ExecuteAsync(null);

        // 只经过注入的假服务：没有网络客户端，也没有实例目录读取。
        Assert.Equal(1, host.Analysis.AnalyzeCalls);
        Assert.Equal("demo", host.Analysis.LastInstanceId);

        Assert.Single(host.Page.Findings);
        var finding = host.Page.Findings[0];
        Assert.Equal("缺少前置模组", finding.Title);
        Assert.Equal("错误", finding.SeverityText);
        Assert.Equal("高度疑似", finding.ConfidenceText);
        Assert.Equal("mods", finding.Category);
        Assert.Equal("latest.log:3  evidence", Assert.Single(finding.EvidenceLines));
        Assert.Single(finding.Hypotheses);
        Assert.Contains("候选原因", finding.Hypotheses[0], StringComparison.Ordinal);
        Assert.Equal("更多日志", Assert.Single(finding.MissingEvidence));
        Assert.Contains("安装缺失的前置模组", finding.SuggestedActions);
        Assert.Contains("说明", finding.Explanation, StringComparison.Ordinal);

        Assert.Single(host.Page.History);
        Assert.Contains("结论 1 条", host.Page.History[0].Summary, StringComparison.Ordinal);
        Assert.Contains("2026.11.1", host.Page.History[0].Summary, StringComparison.Ordinal);

        Assert.Contains("知识库 2026.11.1", host.Page.KnowledgeNote, StringComparison.Ordinal);
        Assert.Contains("结论 1 条", host.Page.Summary, StringComparison.Ordinal);
        Assert.Contains("分析完成", host.Page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorldTargetsRenderWithMapConstraintAndAnalysisRequestsWorldEvidence()
    {
        var report = NewReport(withKnowledgeBase: true) with
        {
            WorldTargets =
            [
                new DiagnosticWorldTarget
                {
                    DimensionId = "minecraft:overworld",
                    Granularity = DiagnosticWorldGranularity.Chunk,
                    ChunkX = 12,
                    ChunkZ = -4,
                    Confidence = DiagnosticConfidence.DirectEvidence,
                },
                new DiagnosticWorldTarget
                {
                    DimensionId = "minecraft:the_nether",
                    DimensionInferred = true,
                    Granularity = DiagnosticWorldGranularity.Chunk,
                    ChunkX = 1,
                    ChunkZ = 2,
                    Confidence = DiagnosticConfidence.HighlyLikely,
                },
            ],
        };
        var host = new Host(report);

        await host.Page.ActivateAsync();
        await host.Page.AnalyzeCommand.ExecuteAsync(null);

        // 诊断页必须显式请求世界证据，否则世界定位永远为空。
        Assert.True(host.Analysis.LastOptions!.IncludeWorldEvents);
        Assert.True(host.Page.HasWorldTargets);
        Assert.Equal(2, host.Page.WorldTargets.Count);
        Assert.Contains("Chunk (12, -4)", host.Page.WorldTargets[0].Summary, StringComparison.Ordinal);
        Assert.Contains("可标记到地图", host.Page.WorldTargets[0].MapNote, StringComparison.Ordinal);
        Assert.Contains("不标记到地图", host.Page.WorldTargets[1].MapNote, StringComparison.Ordinal);
        Assert.False(host.Page.WorldTargets[1].CanMap);
    }

    [Fact]
    public void TheToolsViewKeepsTheDiagnosticsCardAndAddsFindingsAndHistoryLists()
    {
        var xaml = ReadSource("src/ECL.App/Views/ToolsView.xaml");

        Assert.Contains("本地诊断中心", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding Findings}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding History}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding WorldTargets}", xaml, StringComparison.Ordinal);
        Assert.Contains("diagnostics.findings.title", xaml, StringComparison.Ordinal);
        Assert.Contains("diagnostics.worldTargets.title", xaml, StringComparison.Ordinal);
        Assert.Contains("diagnostics.history.title", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding KnowledgeNote}", xaml, StringComparison.Ordinal);
        Assert.Contains("RefreshHistoryCommand", xaml, StringComparison.Ordinal);
    }

    private static DiagnosticReport NewReport(bool withKnowledgeBase) => new()
    {
        SchemaVersion = DiagnosticReport.CurrentSchemaVersion,
        ReportId = "report1",
        GeneratedAtUtc = DateTimeOffset.Parse("2026-11-03T12:00:00Z"),
        InstanceId = "demo",
        SessionId = "session-1",
        KnowledgeBase = withKnowledgeBase
            ? new KnowledgeBaseReference
            {
                SchemaVersion = 1,
                KnowledgeVersion = "2026.11.1",
                PublishedAtUtc = DateTimeOffset.Parse("2026-11-01T00:00:00Z"),
                RuleCount = 1,
                EnabledRuleCount = 1,
            }
            : null,
        Findings = withKnowledgeBase
            ?
            [
                new DiagnosticFinding
                {
                    RuleId = "mods.missing-dependency",
                    RuleVersion = 1,
                    KnowledgeVersion = "2026.11.1",
                    Title = "缺少前置模组",
                    Severity = DiagnosticSeverity.Error,
                    Confidence = DiagnosticConfidence.HighlyLikely,
                    Category = "mods",
                    Occurrences = 1,
                    Evidence = [new DiagnosticEvidence { File = "latest.log", Line = 3, Excerpt = "evidence" }],
                    SuggestedActions = ["安装缺失的前置模组"],
                    Explanation = "说明：日志明确报告缺少前置模组。",
                    Hypotheses =
                    [
                        new DiagnosticHypothesis(
                            "候选原因：前置模组未安装。",
                            DiagnosticConfidence.HighlyLikely,
                            [],
                            ["更多日志"]),
                    ],
                },
            ]
            : [],
        OverallConfidence = withKnowledgeBase
            ? DiagnosticConfidence.HighlyLikely
            : DiagnosticConfidence.Undetermined,
        UncertaintyNote = withKnowledgeBase ? null : "未命中规则，无法确定根因。",
    };

    private sealed class Host
    {
        public Host(DiagnosticReport report)
        {
            var instance = new GameInstance
            {
                Id = "demo",
                Name = "Demo",
                GameVersion = "1.21.1",
                VersionId = "1.21.1",
            };
            var instances = new FakeInstanceService();
            instances.Instances.Add(instance);
            Analysis = new FakeAnalysisService { Report = report };
            Page = new DiagnosticsViewModel(
                instances,
                Analysis,
                new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));
        }

        public FakeAnalysisService Analysis { get; }

        public DiagnosticsViewModel Page { get; }
    }

    private sealed class FakeAnalysisService : IDiagnosticAnalysisService
    {
        public DiagnosticReport? Report { get; set; }

        public IReadOnlyList<DiagnosticReport> History { get; set; } = [];

        public int AnalyzeCalls { get; private set; }

        public int HistoryCalls { get; private set; }

        public string? LastInstanceId { get; private set; }

        public DiagnosticAnalysisOptions? LastOptions { get; private set; }

        public Task<Result<DiagnosticReport>> AnalyzeAsync(
            string instanceId,
            DiagnosticAnalysisOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            AnalyzeCalls++;
            LastInstanceId = instanceId;
            LastOptions = options;
            return Task.FromResult(Result<DiagnosticReport>.Ok(Report!));
        }

        public Task<Result<IReadOnlyList<DiagnosticReport>>> LoadHistoryAsync(
            string instanceId,
            CancellationToken cancellationToken = default)
        {
            HistoryCalls++;
            return Task.FromResult(
                Result<IReadOnlyList<DiagnosticReport>>.Ok(History));
        }

        public Task<Result> DeleteHistoryAsync(
            string instanceId,
            string reportId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Ok());
    }

    private static string ReadSource(string relativePath)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ECL.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"找不到页面源文件：{path}");
        return File.ReadAllText(path);
    }
}
