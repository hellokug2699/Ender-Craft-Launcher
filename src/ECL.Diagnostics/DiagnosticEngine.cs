// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace Ecl.Diagnostics;

/// <summary>
/// 确定性诊断规则引擎。
/// <para>
/// 输入是结构化事件（来自日志/崩溃报告解析器）、已校验知识库和可选环境上下文；
/// 输出带证据链、规则版本和知识库版本的 <see cref="DiagnosticFinding"/> 列表。
/// </para>
/// <para>
/// 结论只来自知识库规则：引擎不会仅因为某异常、某模组或某行位于日志末尾/崩溃位置就推断根因。
/// 此外，如果规则只命中异常类行且没有任何额外上下文约束，即使规则声明 DirectEvidence，
/// 引擎也会将结论降为“无法确定”，因为“最后一个异常类”本身不能证明根因。
/// </para>
/// <para>
/// 所有分析在本地完成：不发起网络请求，不上传日志、崩溃报告、环境信息或诊断历史。
/// 引擎对损坏输入只记录问题，不抛异常阻塞宿主。
/// </para>
/// </summary>
public sealed class DiagnosticEngine
{
    public const int DefaultMaxEvidencePerFinding = 8;
    public const int DefaultMaxExcerptLength = 300;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>只匹配“整行就是异常头”的情况，用于识别仅凭异常类得出结论的证据。</summary>
    private static readonly Regex ExceptionOnlyPattern = new(
        @"^(?:(?:Caused by|Suppressed):\s*)?(?:Exception in thread ""[^""]*""\s+)?(?:[A-Za-z_$][A-Za-z0-9_$]*\.)*[A-Z][A-Za-z0-9_$]*(?:Exception|Error|Throwable)(?::.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private readonly int _maxEvidencePerFinding;
    private readonly int _maxExcerptLength;

    public DiagnosticEngine(
        int maxEvidencePerFinding = DefaultMaxEvidencePerFinding,
        int maxExcerptLength = DefaultMaxExcerptLength)
    {
        _maxEvidencePerFinding = maxEvidencePerFinding > 0
            ? maxEvidencePerFinding
            : DefaultMaxEvidencePerFinding;
        _maxExcerptLength = maxExcerptLength > 0 ? maxExcerptLength : DefaultMaxExcerptLength;
    }

    /// <summary>执行一次本地诊断。不会因知识库/输入问题抛出异常。</summary>
    public DiagnosticReport Analyze(DiagnosticAnalysisRequest request)
    {
        if (request is null)
        {
            request = new DiagnosticAnalysisRequest();
        }

        var generatedAt = request.AnalyzedAtUtc ?? DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        var events = new List<ParsedLogEvent>();

        if (request.Events is { Count: > 0 })
        {
            events.AddRange(request.Events);
        }

        if (request.CrashReports is { Count: > 0 })
        {
            foreach (var crashReport in request.CrashReports)
            {
                if (crashReport is null)
                {
                    warnings.Add("跳过一个为 null 的崩溃报告输入。");
                    continue;
                }

                events.AddRange(CrashReportParser.ToEvents(crashReport));
            }
        }

        if (events.Count == 0)
        {
            warnings.Add("没有可分析的日志或崩溃报告事件。");
        }

        if (request.Environment is null)
        {
            warnings.Add("未提供环境上下文，诊断只基于日志/崩溃报告证据。");
        }

        var knowledgeBase = request.KnowledgeBase;
        if (knowledgeBase is null)
        {
            warnings.Add("未加载诊断知识库，无法匹配任何诊断规则。");
        }
        else
        {
            foreach (var knowledgeWarning in knowledgeBase.Warnings)
            {
                warnings.Add($"知识库：{knowledgeWarning}");
            }

            var disabledCount = knowledgeBase.Rules.Count(rule => rule.Disabled);
            if (disabledCount > 0)
            {
                warnings.Add($"知识库中有 {disabledCount} 条规则已停用或撤回，未参与本次诊断。");
            }
        }

        var findings = new List<DiagnosticFinding>();
        if (knowledgeBase is not null)
        {
            foreach (var rule in knowledgeBase.Rules)
            {
                if (rule.Disabled)
                {
                    continue;
                }

                if (!KnowledgeBaseLoader.TryCompile(rule.Pattern, out var pattern, out var error))
                {
                    warnings.Add($"规则 {rule.Id} 的正则无效，已跳过：{error}");
                    continue;
                }

                var requiredAny = CompileAll(rule.RequiredAny, rule.Id, "requiredAny", warnings);
                var requiredAll = CompileAll(rule.RequiredAll, rule.Id, "requiredAll", warnings);
                var compiled = new CompiledRule(rule, pattern!, requiredAny, requiredAll);
                var finding = AnalyzeRule(compiled, events, knowledgeBase.KnowledgeVersion, warnings);
                if (finding is not null)
                {
                    findings.Add(finding);
                }
            }
        }

        var ordered = findings
            .OrderByDescending(finding => finding.Confidence)
            .ThenByDescending(finding => finding.Severity)
            .ThenByDescending(finding => finding.Occurrences)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ToArray();

        var overallConfidence = ordered.Length == 0
            ? DiagnosticConfidence.Undetermined
            : ordered.Max(finding => finding.Confidence);

        return new DiagnosticReport
        {
            SchemaVersion = DiagnosticReport.CurrentSchemaVersion,
            ReportId = Guid.NewGuid().ToString("N"),
            GeneratedAtUtc = generatedAt,
            KnowledgeBase = knowledgeBase is null
                ? null
                : new KnowledgeBaseReference
                {
                    SchemaVersion = knowledgeBase.SchemaVersion,
                    KnowledgeVersion = knowledgeBase.KnowledgeVersion,
                    PublishedAtUtc = knowledgeBase.PublishedAtUtc,
                    RuleCount = knowledgeBase.Rules.Count,
                    EnabledRuleCount = knowledgeBase.EnabledRuleCount,
                },
            Environment = request.Environment,
            Findings = ordered,
            OverallConfidence = overallConfidence,
            UncertaintyNote = BuildUncertaintyNote(ordered, overallConfidence),
            InputIssues = request.InputIssues ?? [],
            Warnings = warnings,
        };
    }

    private DiagnosticFinding? AnalyzeRule(
        CompiledRule compiled,
        IReadOnlyList<ParsedLogEvent> events,
        string knowledgeVersion,
        List<string> warnings)
    {
        var rule = compiled.Rule;
        var evidence = new List<DiagnosticEvidence>();
        var occurrences = 0;
        var allExceptionOnly = true;
        var timedOut = false;

        foreach (var logEvent in events)
        {
            var match = Match(compiled, logEvent, out var eventTimedOut);
            if (eventTimedOut)
            {
                timedOut = true;
                continue;
            }

            if (match is null)
            {
                continue;
            }

            occurrences++;
            if (!IsExceptionOnly(logEvent, match))
            {
                allExceptionOnly = false;
            }

            if (evidence.Count < _maxEvidencePerFinding)
            {
                var line = match[0];
                evidence.Add(new DiagnosticEvidence
                {
                    File = logEvent.File,
                    Line = line.Number,
                    SourceKind = logEvent.SourceKind,
                    Excerpt = TruncateExcerpt(line.Text),
                });
            }
        }

        if (timedOut)
        {
            warnings.Add($"规则 {rule.Id} 在匹配某个事件时正则超时，该事件已跳过。");
        }

        if (occurrences == 0 || occurrences < rule.MinOccurrences)
        {
            return null;
        }

        // 仅命中异常类行且规则没有额外上下文约束时，不足以确认根因。
        // 规则可通过 requiredAny/requiredAll 提供上下文，或改为 Undetermined/HighlyLikely。
        var confidence = allExceptionOnly && !compiled.HasContextConstraints
            ? DiagnosticConfidence.Undetermined
            : rule.Confidence;

        return new DiagnosticFinding
        {
            RuleId = rule.Id,
            RuleVersion = rule.Version,
            KnowledgeVersion = knowledgeVersion,
            Title = rule.Title,
            Severity = rule.Severity,
            Confidence = confidence,
            Category = rule.Category,
            Occurrences = occurrences,
            Evidence = evidence,
            SuggestedActions = rule.SuggestedActions,
            Explanation = rule.Explanation,
            SourceIds = rule.SourceIds,
        };
    }

    private static IReadOnlyList<SourceLine>? Match(
        CompiledRule compiled,
        ParsedLogEvent logEvent,
        out bool timedOut)
    {
        timedOut = false;
        var lines = EffectiveLines(logEvent);
        if (lines.Count == 0)
        {
            return null;
        }

        var matched = new List<SourceLine>();
        foreach (var line in lines)
        {
            if (TryIsMatch(compiled.Pattern, line.Text, out var lineTimedOut))
            {
                matched.Add(line);
            }
            else if (lineTimedOut)
            {
                timedOut = true;
            }
        }

        if (timedOut || matched.Count == 0)
        {
            return null;
        }

        foreach (var required in compiled.RequiredAll)
        {
            var satisfied = false;
            foreach (var line in lines)
            {
                if (TryIsMatch(required, line.Text, out var lineTimedOut))
                {
                    satisfied = true;
                    break;
                }

                if (lineTimedOut)
                {
                    timedOut = true;
                }
            }

            if (timedOut || !satisfied)
            {
                return null;
            }
        }

        if (compiled.RequiredAny.Count > 0)
        {
            var satisfied = false;
            foreach (var line in lines)
            {
                foreach (var required in compiled.RequiredAny)
                {
                    if (TryIsMatch(required, line.Text, out var lineTimedOut))
                    {
                        satisfied = true;
                        break;
                    }

                    if (lineTimedOut)
                    {
                        timedOut = true;
                    }
                }

                if (satisfied || timedOut)
                {
                    break;
                }
            }

            if (timedOut || !satisfied)
            {
                return null;
            }
        }

        return matched;
    }

    private bool IsExceptionOnly(ParsedLogEvent logEvent, IReadOnlyList<SourceLine> matched)
    {
        foreach (var line in matched)
        {
            var text = line.Number == logEvent.Line && logEvent.Message.Length > 0
                ? logEvent.Message
                : line.Text;
            try
            {
                if (!ExceptionOnlyPattern.IsMatch(text.Trim()))
                {
                    return false;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return true;
    }

    private string TruncateExcerpt(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= _maxExcerptLength
            ? trimmed
            : trimmed[.._maxExcerptLength] + "…";
    }

    private static IReadOnlyList<SourceLine> EffectiveLines(ParsedLogEvent logEvent)
    {
        if (logEvent.Lines is { Count: > 0 })
        {
            return logEvent.Lines;
        }

        return string.IsNullOrEmpty(logEvent.Message)
            ? []
            : [new SourceLine(logEvent.Line, logEvent.Message)];
    }

    private static bool TryIsMatch(Regex regex, string text, out bool timedOut)
    {
        timedOut = false;
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
            return false;
        }
    }

    private static IReadOnlyList<Regex> CompileAll(
        IReadOnlyList<string> patterns,
        string ruleId,
        string field,
        List<string> warnings)
    {
        if (patterns.Count == 0)
        {
            return [];
        }

        var compiled = new List<Regex>(patterns.Count);
        foreach (var pattern in patterns)
        {
            if (KnowledgeBaseLoader.TryCompile(pattern, out var regex, out var error))
            {
                compiled.Add(regex!);
            }
            else
            {
                warnings.Add($"规则 {ruleId} 的 {field} 正则无效，已跳过该约束：{error}");
            }
        }

        return compiled;
    }

    private static string? BuildUncertaintyNote(
        IReadOnlyList<DiagnosticFinding> findings,
        DiagnosticConfidence overallConfidence)
    {
        if (findings.Count == 0)
        {
            return "未命中任何已启用的诊断规则，无法确定根因。仅凭最后一个异常类、最后一个模组或日志末尾位置不能推断根因。";
        }

        if (overallConfidence != DiagnosticConfidence.DirectEvidence)
        {
            return "现有证据只能支持“高度疑似”或“无法确定”，尚不足以确认根因。请根据证据链和后续建议继续验证。";
        }

        return null;
    }

    private sealed record CompiledRule(
        KnowledgeRule Rule,
        Regex Pattern,
        IReadOnlyList<Regex> RequiredAny,
        IReadOnlyList<Regex> RequiredAll)
    {
        public bool HasContextConstraints => RequiredAny.Count > 0 || RequiredAll.Count > 0;
    }
}
