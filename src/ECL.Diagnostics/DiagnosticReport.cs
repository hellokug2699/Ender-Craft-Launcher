// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Diagnostics;

/// <summary>证据链中的一条可定位证据：来源文件、1 基行号和截断后的原文摘录。</summary>
public sealed record DiagnosticEvidence
{
    /// <summary>来源文件名（不保存完整本机路径）。</summary>
    public string File { get; init; } = "";

    public int Line { get; init; }

    public DiagnosticSourceKind SourceKind { get; init; }

    /// <summary>匹配行摘录，长度受限；不会把整份日志写入报告或历史。</summary>
    public string Excerpt { get; init; } = "";
}

/// <summary>
/// 一条诊断结论。包含规则版本、知识库版本、置信度、严重度、证据链、建议和解释，
/// 用户应能据此复核规则为什么命中，而不是只看到一个“罪魁祸首”。
/// </summary>
public sealed record DiagnosticFinding
{
    public string RuleId { get; init; } = "";

    public int RuleVersion { get; init; }

    /// <summary>本次分析使用的知识库版本，历史记录不会随后续知识库更新而改变。</summary>
    public string KnowledgeVersion { get; init; } = "";

    public string Title { get; init; } = "";

    public DiagnosticSeverity Severity { get; init; }

    public DiagnosticConfidence Confidence { get; init; }

    public string Category { get; init; } = "";

    /// <summary>命中的事件数量（同一条规则在同一事件内多行命中只计一次）。</summary>
    public int Occurrences { get; init; }

    /// <summary>证据链，按命中顺序保留；受每结论证据上限约束。</summary>
    public IReadOnlyList<DiagnosticEvidence> Evidence { get; init; } = [];

    public IReadOnlyList<string> SuggestedActions { get; init; } = [];

    public string Explanation { get; init; } = "";

    public IReadOnlyList<string> SourceIds { get; init; } = [];
}

/// <summary>诊断报告引用的知识库元数据，用于解释与历史比较。</summary>
public sealed record KnowledgeBaseReference
{
    public int SchemaVersion { get; init; }

    public string KnowledgeVersion { get; init; } = "";

    public DateTimeOffset PublishedAtUtc { get; init; }

    public int RuleCount { get; init; }

    public int EnabledRuleCount { get; init; }
}

/// <summary>
/// 一次诊断的完整结果。包含环境摘要、知识库版本、findings、未确定说明、输入问题和生成时间。
/// <para>默认只在本地生成与保存，不包含网络调用；历史存储只保存结论与证据摘录，不保存原始日志副本。</para>
/// </summary>
public sealed record DiagnosticReport
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string ReportId { get; init; } = "";

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public KnowledgeBaseReference? KnowledgeBase { get; init; }

    public DiagnosticEnvironment? Environment { get; init; }

    public IReadOnlyList<DiagnosticFinding> Findings { get; init; } = [];

    /// <summary>所有结论中的最高置信度；没有结论时必须是“无法确定”。</summary>
    public DiagnosticConfidence OverallConfidence { get; init; } = DiagnosticConfidence.Undetermined;

    /// <summary>无法确定时的说明；存在直接证据时可为空。</summary>
    public string? UncertaintyNote { get; init; }

    /// <summary>读取/解析阶段的文件级问题；单个文件失败不影响其他输入。</summary>
    public IReadOnlyList<DiagnosticInputIssue> InputIssues { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>本次分析使用的知识库版本（便捷读取；同时保存在 <see cref="KnowledgeBase"/> 中）。</summary>
    public string? KnowledgeVersion => KnowledgeBase?.KnowledgeVersion;
}

/// <summary>诊断引擎输入：已解析事件、可选崩溃报告、知识库与环境上下文。</summary>
public sealed record DiagnosticAnalysisRequest
{
    public IReadOnlyList<ParsedLogEvent> Events { get; init; } = [];

    public IReadOnlyList<CrashReport> CrashReports { get; init; } = [];

    public KnowledgeBase? KnowledgeBase { get; init; }

    public DiagnosticEnvironment? Environment { get; init; }

    public IReadOnlyList<DiagnosticInputIssue> InputIssues { get; init; } = [];

    /// <summary>仅用于测试/可重现分析；为空时使用当前 UTC 时间。</summary>
    public DateTimeOffset? AnalyzedAtUtc { get; init; }
}
