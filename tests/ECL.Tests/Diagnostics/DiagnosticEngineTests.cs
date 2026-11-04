// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using Ecl.Diagnostics;
using Xunit;

namespace Ecl.Tests.Diagnostics;

public sealed class DiagnosticEngineTests : IDisposable
{
    private readonly string _root;

    public DiagnosticEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ecl-diagnostics-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ParsesMultiLineStackTraceWithCorrectFileAndLineNumbers()
    {
        const string log = """
            [10:00:00] [main/INFO]: Starting integrated server
            [10:00:01] [Render thread/ERROR]: Encountered an unexpected exception
            java.lang.NullPointerException: Cannot invoke "Foo.bar()"
                at com.example.Foo.bar(Foo.java:10)
                at com.example.Baz.run(Baz.java:20)
                Suppressed: java.lang.IllegalStateException: inner
                    at com.example.Inner.tick(Inner.java:5)
            [10:00:02] [main/INFO]: Done
            """;

        var parsed = LatestLogParser.Parse("logs/latest.log", log);

        Assert.Equal("latest.log", parsed.File);
        Assert.Equal(3, parsed.Events.Count);

        var first = parsed.Events[0];
        Assert.Equal(1, first.Line);
        Assert.Equal(1, first.EndLine);

        // 堆栈行必须归属于异常事件，而不是各自成为新事件。
        var error = parsed.Events[1];
        Assert.Equal(2, error.Line);
        Assert.Equal(7, error.EndLine);
        Assert.Equal(new TimeOnly(10, 0, 1), error.Time);
        Assert.Equal("Render thread", error.Thread);
        Assert.Equal("ERROR", error.Level);
        Assert.Equal("java.lang.NullPointerException", error.ExceptionType);
        Assert.Equal("Cannot invoke \"Foo.bar()\"", error.ExceptionMessage);
        Assert.Equal(6, error.Lines.Count);
        Assert.Equal(3, error.Lines[1].Number);
        Assert.Contains("com.example.Foo.bar", error.Lines[2].Text);
        Assert.Equal(6, error.Lines[4].Number);
        Assert.Contains("IllegalStateException", error.ExceptionTypes[1]);

        Assert.Equal(8, parsed.Events[2].Line);
        Assert.Equal("Done", parsed.Events[2].Message);
    }

    [Fact]
    public void RuleMatchProducesEvidenceConfidenceAndKnowledgeVersion()
    {
        var knowledge = LoadKnowledge(KnowledgeJson(RuleJson(
            id: "java.unsupported-class-version",
            pattern: "UnsupportedClassVersionError",
            title: "Java 版本过低",
            category: "java",
            severity: "error",
            confidence: "directEvidence",
            version: 3,
            requiredAny: ["class file version"],
            suggestedActions: ["升级实例使用的 Java"],
            explanation: "模组使用了更高版本的 Java 编译，当前运行时无法加载。",
            sourceIds: ["adr-0033"])));

        const string log = """
            [10:00:00] [main/INFO]: Launching game
            [10:00:01] [main/ERROR]: Failed to launch
            java.lang.UnsupportedClassVersionError: com/example/Main has been compiled by a more recent version of the Java Runtime (class file version 61.0)
                at java.base/java.lang.ClassLoader.defineClass1(Native Method)
            """;

        var parsed = LatestLogParser.Parse("latest.log", log);
        var report = new DiagnosticEngine().Analyze(new DiagnosticAnalysisRequest
        {
            Events = parsed.Events,
            KnowledgeBase = knowledge,
            Environment = new DiagnosticEnvironment
            {
                MinecraftVersion = "1.21.1",
                Loader = "Fabric",
                LoaderVersion = "0.16.0",
                JavaVersion = "17.0.9",
                ModCount = 12,
            },
            AnalyzedAtUtc = DateTimeOffset.Parse("2026-11-03T00:00:00Z"),
        });

        var finding = Assert.Single(report.Findings);
        Assert.Equal("java.unsupported-class-version", finding.RuleId);
        Assert.Equal(3, finding.RuleVersion);
        Assert.Equal("2026.11.1", finding.KnowledgeVersion);
        Assert.Equal("Java 版本过低", finding.Title);
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Equal(DiagnosticConfidence.DirectEvidence, finding.Confidence);
        Assert.Equal("java", finding.Category);
        Assert.Equal(1, finding.Occurrences);
        Assert.Equal(["升级实例使用的 Java"], finding.SuggestedActions);
        Assert.Contains("更高版本的 Java", finding.Explanation);

        var evidence = Assert.Single(finding.Evidence);
        Assert.Equal("latest.log", evidence.File);
        Assert.Equal(DiagnosticSourceKind.GameLog, evidence.SourceKind);
        Assert.Equal(3, evidence.Line);
        Assert.Contains("UnsupportedClassVersionError", evidence.Excerpt);

        Assert.Equal("2026.11.1", report.KnowledgeVersion);
        Assert.Equal(DiagnosticConfidence.DirectEvidence, report.OverallConfidence);
        Assert.Null(report.UncertaintyNote);
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T00:00:00Z"), report.GeneratedAtUtc);
        Assert.NotNull(report.Environment);
        Assert.Contains("Minecraft 1.21.1", report.Environment!.Summary);
        Assert.Contains("Fabric 0.16.0", report.Environment.Summary);
        Assert.Contains("Java 17.0.9", report.Environment.Summary);
    }

    [Fact]
    public void SameRuleMatchingMultipleEventsIsCounted()
    {
        var knowledge = LoadKnowledge(KnowledgeJson(RuleJson(
            id: "mods.missing-dependency",
            pattern: "Could not find required mod",
            title: "缺少前置模组",
            confidence: "highlyLikely",
            minOccurrences: 2)));

        const string log = """
            [10:00:00] [main/ERROR]: Could not find required mod: examplemod
            [10:00:01] [main/INFO]: Retrying launch
            [10:00:02] [main/ERROR]: Could not find required mod: othermod
            """;

        var parsed = LatestLogParser.Parse("latest.log", log);
        var report = new DiagnosticEngine().Analyze(new DiagnosticAnalysisRequest
        {
            Events = parsed.Events,
            KnowledgeBase = knowledge,
        });

        var finding = Assert.Single(report.Findings);
        Assert.Equal(2, finding.Occurrences);
        Assert.Equal(2, finding.Evidence.Count);
        Assert.Equal(1, finding.Evidence[0].Line);
        Assert.Equal(3, finding.Evidence[1].Line);
        Assert.Equal(DiagnosticConfidence.HighlyLikely, finding.Confidence);
        Assert.Equal(DiagnosticConfidence.HighlyLikely, report.OverallConfidence);
        Assert.Contains("无法确定", report.UncertaintyNote);
    }

    [Fact]
    public void RuleBelowMinOccurrencesDoesNotProduceFinding()
    {
        var knowledge = LoadKnowledge(KnowledgeJson(RuleJson(
            id: "mods.missing-dependency",
            pattern: "Could not find required mod",
            confidence: "highlyLikely",
            minOccurrences: 3)));

        const string log = """
            [10:00:00] [main/ERROR]: Could not find required mod: examplemod
            [10:00:02] [main/ERROR]: Could not find required mod: othermod
            """;

        var parsed = LatestLogParser.Parse("latest.log", log);
        var report = new DiagnosticEngine().Analyze(new DiagnosticAnalysisRequest
        {
            Events = parsed.Events,
            KnowledgeBase = knowledge,
        });

        Assert.Empty(report.Findings);
        Assert.Equal(DiagnosticConfidence.Undetermined, report.OverallConfidence);
        Assert.Contains("无法确定", report.UncertaintyNote);
    }

    [Fact]
    public void LoneFinalNullPointerExceptionNeverDeterminesRootCause()
    {
        const string log = """
            [10:00:00] [main/INFO]: Starting game
            [10:00:05] [Server thread/ERROR]: Encountered an unexpected exception
            java.lang.NullPointerException: Cannot invoke "java.lang.String.length()" because "value" is null
                at com.example.Tick.run(Tick.java:42)
            """;

        var parsed = LatestLogParser.Parse("latest.log", log);

        // 没有规则命中时只能报告“无法确定”，不能猜根因。
        var noRules = new DiagnosticEngine().Analyze(new DiagnosticAnalysisRequest
        {
            Events = parsed.Events,
            KnowledgeBase = LoadKnowledge(KnowledgeJson(string.Empty)),
        });

        Assert.Empty(noRules.Findings);
        Assert.Equal(DiagnosticConfidence.Undetermined, noRules.OverallConfidence);
        Assert.Contains("无法确定", noRules.UncertaintyNote);

        // 即使知识库声明“直接证据”，仅命中异常类行且没有上下文约束时也必须降级为无法确定。
        var npeRule = LoadKnowledge(KnowledgeJson(RuleJson(
            id: "runtime.null-pointer",
            pattern: "NullPointerException",
            confidence: "directEvidence",
            explanation: "空指针异常。")));

        var ruleBased = new DiagnosticEngine().Analyze(new DiagnosticAnalysisRequest
        {
            Events = parsed.Events,
            KnowledgeBase = npeRule,
        });

        var finding = Assert.Single(ruleBased.Findings);
        Assert.Equal(DiagnosticConfidence.Undetermined, finding.Confidence);
        Assert.NotEqual(DiagnosticConfidence.DirectEvidence, finding.Confidence);
        Assert.NotEqual(DiagnosticConfidence.HighlyLikely, finding.Confidence);
        Assert.Equal(DiagnosticConfidence.Undetermined, ruleBased.OverallConfidence);
        Assert.Contains("无法确定", ruleBased.UncertaintyNote);
    }

    [Fact]
    public void DisabledRulesDoNotParticipateButKnowledgeVersionIsRecorded()
    {
        const string log = """
            [10:00:00] [main/ERROR]: Could not find required mod: examplemod
            """;

        var disabled = LoadKnowledge(KnowledgeJson(RuleJson(
            id: "mods.missing-dependency",
            pattern: "Could not find required mod",
            confidence: "highlyLikely",
            disabled: true)));

        var parsed = LatestLogParser.Parse("latest.log", log);
        var disabledReport = new DiagnosticEngine().Analyze(new DiagnosticAnalysisRequest
        {
            Events = parsed.Events,
            KnowledgeBase = disabled,
        });

        Assert.Empty(disabledReport.Findings);
        Assert.Equal("2026.11.1", disabledReport.KnowledgeVersion);
        Assert.Contains(disabledReport.Warnings, warning => warning.Contains("停用", StringComparison.Ordinal));

        var enabled = LoadKnowledge(KnowledgeJson(RuleJson(
            id: "mods.missing-dependency",
            pattern: "Could not find required mod",
            confidence: "highlyLikely")));

        var enabledReport = new DiagnosticEngine().Analyze(new DiagnosticAnalysisRequest
        {
            Events = parsed.Events,
            KnowledgeBase = enabled,
        });

        Assert.Single(enabledReport.Findings);
        Assert.Equal("2026.11.1", enabledReport.KnowledgeVersion);
    }

    [Fact]
    public void KnowledgeBaseLoaderRejectsMissingFieldsBadRegexAndIncompatibleSchema()
    {
        var missingPattern = KnowledgeBaseLoader.Load(KnowledgeJson("""
            {
              "id": "missing.pattern",
              "version": 1,
              "title": "缺少 pattern",
              "severity": "warning",
              "category": "test",
              "explanation": "缺少 pattern 的规则应被拒绝。",
              "disabled": false
            }
            """));

        Assert.False(missingPattern.IsSuccess);
        Assert.Contains("pattern", missingPattern.Error!.Detail!, StringComparison.Ordinal);

        var badRegex = KnowledgeBaseLoader.Load(KnowledgeJson(RuleJson(pattern: "(unclosed")));
        Assert.False(badRegex.IsSuccess);
        Assert.Contains("正则", badRegex.Error!.Detail!, StringComparison.Ordinal);

        var incompatible = KnowledgeBaseLoader.Load(
            KnowledgeJson(RuleJson(), schemaVersion: 99));
        Assert.False(incompatible.IsSuccess);
        Assert.Equal("knowledge.schema_incompatible", incompatible.Error!.Code);

        var malformed = KnowledgeBaseLoader.Load("{ this is not json");
        Assert.False(malformed.IsSuccess);
        Assert.Equal("knowledge.invalid_json", malformed.Error!.Code);

        var corruptFile = WriteFile("knowledge.json", "not a knowledge base");
        var corrupt = KnowledgeBaseLoader.LoadFile(corruptFile);
        Assert.False(corrupt.IsSuccess);
        Assert.Equal("knowledge.invalid_json", corrupt.Error!.Code);
    }

    [Fact]
    public void ParsesCrashReportSectionsAndToleratesMissingFields()
    {
        const string reportText = """
            ---- Minecraft Crash Report ----
            // Don't be sad, have a hug!
            Time: 2026-10-29 10:00:00
            Description: Rendering overlay

            java.lang.NoSuchMethodError: examplemod.Foo.bar()V
                at examplemod.Foo.bar(Foo.java:10)
                at net.minecraft.client.renderer.GameRenderer.render(GameRenderer.java:1)
            Caused by: java.lang.NullPointerException
                at othermod.Helper.tick(Helper.java:20)

            -- System Details --
            Details:
                Minecraft Version: 1.21.1
                Java Version: 21.0.7
                Fabric Mods:
                    examplemod: Example Mod 1.0.0
                    othermod: Other Mod 2.0.0
            """;

        var parsed = CrashReportParser.Parse("crash-reports/crash-2026-10-29.txt", reportText);

        Assert.True(parsed.HasHeader);
        Assert.Equal("crash-2026-10-29.txt", parsed.File);
        Assert.Equal("Rendering overlay", parsed.Description);
        Assert.Equal("1.21.1", parsed.MinecraftVersion);
        Assert.Equal("21.0.7", parsed.JavaVersion);
        Assert.Equal("2026-10-29", parsed.Time?.ToString("yyyy-MM-dd"));
        Assert.Equal("java.lang.NoSuchMethodError", parsed.ExceptionType);
        Assert.Equal(2, parsed.Mods.Count);
        Assert.Equal("examplemod", parsed.Mods[0].Id);
        Assert.Equal("Example Mod", parsed.Mods[0].Name);
        Assert.Equal("1.0.0", parsed.Mods[0].Version);
        Assert.Contains(parsed.StackTrace, line => line.Number == 9);
        Assert.Contains(parsed.Sections, section => section.Name == "System Details");

        var minimal = CrashReportParser.Parse("crash.txt", "some random text without crash sections");
        Assert.False(minimal.HasHeader);
        Assert.Null(minimal.Description);
        Assert.Null(minimal.MinecraftVersion);
        Assert.Null(minimal.ExceptionType);
        Assert.Empty(minimal.Mods);
    }

    [Fact]
    public void BoundedReadingCapsLinesAndReportsTruncation()
    {
        var content = string.Join(
            '\n',
            Enumerable.Range(1, 20).Select(index => $"[10:00:00] [main/INFO]: line {index}"));

        var path = WriteFile("big.log", content);
        var parsed = LatestLogParser.ParseFile(path, maxBytes: 1024 * 1024, maxLines: 5);

        Assert.True(parsed.IsSuccess);
        Assert.True(parsed.Value!.Truncated);
        Assert.Equal(5, parsed.Value.LinesRead);
        Assert.Equal(5, parsed.Value.Events.Count);
        Assert.Contains(parsed.Value.Warnings, warning => warning.Contains("上限", StringComparison.Ordinal));
    }

    [Fact]
    public void UnreadableFileDoesNotBlockOtherDiagnosticInputs()
    {
        var goodLog = WriteFile("latest.log", """
            [10:00:00] [main/ERROR]: Could not find required mod: examplemod
            """);

        var missingLog = Path.Combine(_root, "missing.log");
        var missingCrash = Path.Combine(_root, "crash-reports", "missing.txt");

        var input = new DiagnosticInputLoader().Load([goodLog, missingLog], [missingCrash]);

        Assert.Single(input.Events);
        Assert.Equal(2, input.Issues.Count);
        Assert.Contains(input.Issues, issue => issue.File == "missing.log");
        Assert.Contains(input.Issues, issue => issue.File == "missing.txt");
        Assert.All(input.Issues, issue => Assert.False(string.IsNullOrWhiteSpace(issue.Code)));
    }

    [Fact]
    public void HistoryRoundTripsWithKnowledgeVersionAndToleratesCorruption()
    {
        var knowledge = LoadKnowledge(KnowledgeJson(RuleJson(
            id: "mods.missing-dependency",
            pattern: "Could not find required mod",
            confidence: "highlyLikely",
            suggestedActions: ["安装缺失的前置模组"])));

        const string log = """
            [10:00:00] [main/INFO]: UNRELATED_RAW_MARKER should not be persisted
            [10:00:01] [main/ERROR]: Could not find required mod: examplemod
            """;

        var parsed = LatestLogParser.Parse("latest.log", log);
        var report = new DiagnosticEngine().Analyze(new DiagnosticAnalysisRequest
        {
            Events = parsed.Events,
            KnowledgeBase = knowledge,
            Environment = new DiagnosticEnvironment { MinecraftVersion = "1.21.1", Loader = "Fabric" },
            AnalyzedAtUtc = DateTimeOffset.Parse("2026-11-03T12:00:00Z"),
        });

        var store = new DiagnosticHistoryStore(Path.Combine(_root, "history"));
        var saved = store.Save(report);

        Assert.True(saved.IsSuccess, saved.Error?.ToString());
        Assert.EndsWith(".json", saved.Value!, StringComparison.Ordinal);
        Assert.True(File.Exists(saved.Value));

        var savedJson = File.ReadAllText(saved.Value!);
        Assert.Contains("\"knowledgeVersion\": \"2026.11.1\"", savedJson, StringComparison.Ordinal);
        // 只保存证据摘录，不能把无关键或整份原始日志写入历史。
        Assert.DoesNotContain("UNRELATED_RAW_MARKER", savedJson, StringComparison.Ordinal);

        var loaded = store.LoadAll();
        Assert.True(loaded.IsSuccess, loaded.Error?.ToString());
        var restored = Assert.Single(loaded.Value!.Reports);
        Assert.Equal(report.ReportId, restored.ReportId);
        Assert.Equal("2026.11.1", restored.KnowledgeVersion);
        Assert.Equal(DiagnosticConfidence.HighlyLikely, restored.OverallConfidence);
        var finding = Assert.Single(restored.Findings);
        Assert.Equal("mods.missing-dependency", finding.RuleId);
        Assert.Equal("2026.11.1", finding.KnowledgeVersion);
        Assert.Equal(2, finding.Evidence[0].Line);
        Assert.Contains("Could not find required mod", finding.Evidence[0].Excerpt);
        Assert.Empty(Directory.EnumerateFiles(store.DirectoryPath, "*.tmp"));

        // 损坏文件只产生问题项，不影响已保存的历史。
        File.WriteAllText(Path.Combine(store.DirectoryPath, "broken.json"), "{ not valid json");
        var tolerant = store.LoadAll();
        Assert.True(tolerant.IsSuccess);
        Assert.Single(tolerant.Value!.Reports);
        var issue = Assert.Single(tolerant.Value.Issues);
        Assert.Equal("broken.json", issue.File);
        Assert.Equal("history.corrupt", issue.Code);

        var missing = store.Load("not-present");
        Assert.True(missing.IsSuccess);
        Assert.Null(missing.Value);
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

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static KnowledgeBase LoadKnowledge(string json)
    {
        var result = KnowledgeBaseLoader.Load(json);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!;
    }

    private static string KnowledgeJson(
        string rules,
        string knowledgeVersion = "2026.11.1",
        int schemaVersion = 1) => $$"""
        {
          "schemaVersion": {{schemaVersion}},
          "knowledgeVersion": "{{knowledgeVersion}}",
          "publishedAtUtc": "2026-11-01T00:00:00Z",
          "rules": [
        {{rules}}
          ],
          "sources": [
            { "id": "adr-0033", "title": "ADR 0033", "publisher": "ECL" }
          ]
        }
        """;

    private static string RuleJson(
        string id = "rule.test",
        string? pattern = "NullPointerException",
        string confidence = "highlyLikely",
        string severity = "error",
        int version = 1,
        int minOccurrences = 1,
        string[]? requiredAny = null,
        string[]? requiredAll = null,
        bool disabled = false,
        string title = "测试规则",
        string category = "test",
        string explanation = "用于测试的规则解释。",
        string[]? suggestedActions = null,
        string[]? sourceIds = null)
    {
        var rule = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["version"] = version,
            ["title"] = title,
            ["severity"] = severity,
            ["category"] = category,
            ["minOccurrences"] = minOccurrences,
            ["confidence"] = confidence,
            ["explanation"] = explanation,
            ["disabled"] = disabled,
        };

        if (pattern is not null)
        {
            rule["pattern"] = pattern;
        }

        if (requiredAny is not null)
        {
            rule["requiredAny"] = requiredAny;
        }

        if (requiredAll is not null)
        {
            rule["requiredAll"] = requiredAll;
        }

        if (suggestedActions is not null)
        {
            rule["suggestedActions"] = suggestedActions;
        }

        if (sourceIds is not null)
        {
            rule["sourceIds"] = sourceIds;
        }

        return JsonSerializer.Serialize(rule);
    }
}
