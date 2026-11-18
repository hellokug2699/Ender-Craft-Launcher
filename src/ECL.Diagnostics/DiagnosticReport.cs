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

    /// <summary>
    /// 规则声明的候选原因（推断）。与证据链分开保存，UI/CLI 必须同时展示置信度与缺失证据，
    /// 不能把候选原因渲染成已确认结论。
    /// </summary>
    public IReadOnlyList<DiagnosticHypothesis> Hypotheses { get; init; } = [];
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
    /// <summary>当前写入版本。schema 2 增加实例/会话归属、事实与候选原因；schema 1 历史仍可读取。</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>仍可读取的最旧 schema。读取方不得把旧报告静默改写为新版本。</summary>
    public const int MinSupportedSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string ReportId { get; init; } = "";

    public DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>报告归属的实例；旧 schema 1 报告为 <c>null</c>，表示保存在历史根目录。</summary>
    public string? InstanceId { get; init; }

    /// <summary>报告归属的游戏会话；无会话时仍可生成报告。</summary>
    public string? SessionId { get; init; }

    public KnowledgeBaseReference? KnowledgeBase { get; init; }

    public DiagnosticEnvironment? Environment { get; init; }

    /// <summary>本次分析使用的结构化事实；规则通过这些事实约束命中条件。</summary>
    public IReadOnlyList<DiagnosticFact> Facts { get; init; } = [];

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

    /// <summary>编排层已经确认的警告（例如共享日志归属、缺知识库）；引擎会原样保留并追加自身警告。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>实例归属；仅用于报告与实例级历史，不参与规则匹配。</summary>
    public string? InstanceId { get; init; }

    /// <summary>会话归属；仅用于引用会话 manifest 事实与报告来源。</summary>
    public string? SessionId { get; init; }

    /// <summary>会话 manifest/采集计量等结构化事实；规则可要求某些事实存在才命中。</summary>
    public IReadOnlyList<DiagnosticFact> Facts { get; init; } = [];

    /// <summary>已解析的 JVM 致命错误（hs_err）；引擎会转换成事件供规则匹配。</summary>
    public IReadOnlyList<JvmFatalErrorReport> JvmFatalErrors { get; init; } = [];

    /// <summary>仅用于测试/可重现分析；为空时使用当前 UTC 时间。</summary>
    public DateTimeOffset? AnalyzedAtUtc { get; init; }
}
