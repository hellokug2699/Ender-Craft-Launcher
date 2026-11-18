// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using Ecl.Cli;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Diagnostics;
using Ecl.Minecraft.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Cli;

/// <summary>
/// CLI diagnose 契约：--json 输出 schemaVersion=2 与 findings；未知实例返回非 0 与稳定错误码；
/// --no-save 正确传入编排选项。用假服务验证命令编排，不启动宿主、不访问网络。
/// </summary>
public sealed class DiagnoseCommandTests
{
    [Fact]
    public async Task DiagnoseJsonContainsSchemaVersionTwoAndFindings()
    {
        var fake = new FakeAnalysisService { Report = NewReport() };

        var (exitCode, output, _) = await RunAsync(["diagnose", "demo", "--json"], fake);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output);
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var findings = document.RootElement.GetProperty("findings");
        Assert.Equal(1, findings.GetArrayLength());
        Assert.Equal("测试结论", findings[0].GetProperty("title").GetString());
        Assert.Equal("demo", fake.LastInstanceId);
        Assert.NotNull(fake.LastOptions);
        Assert.True(fake.LastOptions!.SaveHistory);
    }

    [Fact]
    public async Task NoSaveFlagDisablesHistoryWriteOption()
    {
        var fake = new FakeAnalysisService { Report = NewReport() };

        var (exitCode, _, _) = await RunAsync(["diagnose", "demo", "--no-save", "--json"], fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.LastOptions);
        Assert.False(fake.LastOptions!.SaveHistory);
    }

    [Fact]
    public async Task UnknownInstanceReturnsNonZeroAndStableErrorCode()
    {
        var fake = new FakeAnalysisService
        {
            AnalyzeError = new EclError("instance.not_found", "实例不存在。"),
        };

        var (exitCode, _, error) = await RunAsync(["diagnose", "missing"], fake);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("instance.not_found", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryModePrintsSavedReports()
    {
        var fake = new FakeAnalysisService { History = [NewReport()] };

        var (exitCode, output, _) = await RunAsync(["diagnose", "demo", "--history"], fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("诊断历史", output, StringComparison.Ordinal);
        Assert.Contains("结论 1 条", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryJsonSerializesReportArray()
    {
        var fake = new FakeAnalysisService { History = [NewReport()] };

        var (exitCode, output, _) = await RunAsync(["diagnose", "demo", "--history", "--json"], fake);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(2, document.RootElement[0].GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task WorldFlagRequestsWorldEvidenceFromAnalysisService()
    {
        var fake = new FakeAnalysisService { Report = NewReport() };

        var (exitCode, _, _) = await RunAsync(["diagnose", "demo", "--world", "--no-save", "--json"], fake);

        Assert.Equal(0, exitCode);
        Assert.NotNull(fake.LastOptions);
        Assert.True(fake.LastOptions!.IncludeWorldEvents);
    }

    [Fact]
    public async Task ReportPrintsWorldTargetsWithMapConstraint()
    {
        var report = NewReport() with
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
        var fake = new FakeAnalysisService { Report = report };

        var (exitCode, output, _) = await RunAsync(["diagnose", "demo", "--world"], fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("世界定位", output, StringComparison.Ordinal);
        Assert.Contains("Chunk (12, -4)", output, StringComparison.Ordinal);
        Assert.Contains("可标记到地图", output, StringComparison.Ordinal);
        Assert.Contains("不标记到地图", output, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string[] arguments,
        FakeAnalysisService service)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDiagnosticAnalysisService>(service);
        services.AddSingleton<ILocalizer>(new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));
        await using var provider = services.BuildServiceProvider();
        var application = new CliApplication(provider, ParsedCommandLine.Parse(arguments));

        await ConsoleTestGate.Gate.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await application.RunAsync();
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleTestGate.Gate.Release();
        }
    }

    private static DiagnosticReport NewReport() => new()
    {
        SchemaVersion = DiagnosticReport.CurrentSchemaVersion,
        ReportId = "report1",
        GeneratedAtUtc = DateTimeOffset.Parse("2026-11-03T12:00:00Z"),
        InstanceId = "demo",
        SessionId = "session-1",
        KnowledgeBase = new KnowledgeBaseReference
        {
            SchemaVersion = 1,
            KnowledgeVersion = "2026.11.1",
            PublishedAtUtc = DateTimeOffset.Parse("2026-11-01T00:00:00Z"),
            RuleCount = 1,
            EnabledRuleCount = 1,
        },
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
                SuggestedActions = ["检查日志"],
                Explanation = "用于测试的命令输出。",
                Hypotheses = [new DiagnosticHypothesis("候选原因", DiagnosticConfidence.HighlyLikely, [], ["更多日志"])],
            },
        ],
        OverallConfidence = DiagnosticConfidence.HighlyLikely,
    };

    private sealed class FakeAnalysisService : IDiagnosticAnalysisService
    {
        public DiagnosticReport? Report { get; set; }

        public EclError? AnalyzeError { get; set; }

        public IReadOnlyList<DiagnosticReport> History { get; set; } = [];

        public string? LastInstanceId { get; private set; }

        public DiagnosticAnalysisOptions? LastOptions { get; private set; }

        public Task<Result<DiagnosticReport>> AnalyzeAsync(
            string instanceId,
            DiagnosticAnalysisOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastInstanceId = instanceId;
            LastOptions = options;
            return Task.FromResult(AnalyzeError is null
                ? Result<DiagnosticReport>.Ok(Report!)
                : Result<DiagnosticReport>.Fail(AnalyzeError));
        }

        public Task<Result<IReadOnlyList<DiagnosticReport>>> LoadHistoryAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<DiagnosticReport>>.Ok(History));

        public Task<Result> DeleteHistoryAsync(
            string instanceId,
            string reportId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Ok());
    }
}
