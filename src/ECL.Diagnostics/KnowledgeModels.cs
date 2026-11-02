// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Diagnostics;

/// <summary>知识库来源，用于追踪规则依据。URL 仅作为文本保存，诊断过程不会访问它。</summary>
public sealed record KnowledgeSource
{
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public string? Publisher { get; init; }

    /// <summary>来源链接，仅用于展示与审计；加载或诊断时不会发起网络请求。</summary>
    public string? Url { get; init; }

    public DateTimeOffset? RetrievedAtUtc { get; init; }
}

/// <summary>
/// 一条版本化诊断规则。JSON schema 使用 camelCase，枚举使用 camelCase 字符串。
/// <para><see cref="Pattern"/> 只作用于单行；<see cref="RequiredAny"/> / <see cref="RequiredAll"/> 是可选上下文约束。</para>
/// </summary>
public sealed record KnowledgeRule
{
    /// <summary>稳定规则 ID；同一知识库内必须唯一。</summary>
    public string Id { get; init; } = "";

    /// <summary>规则内容版本；修订规则语义时应递增。必须大于 0。</summary>
    public int Version { get; init; } = 1;

    public string Title { get; init; } = "";

    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Warning;

    /// <summary>分类，例如 java、loader、mods、rendering、mixin。</summary>
    public string Category { get; init; } = "";

    /// <summary>单行正则表达式。</summary>
    public string Pattern { get; init; } = "";

    /// <summary>可选：同一事件中至少有一条行匹配其中任意一个正则，规则才生效。</summary>
    public IReadOnlyList<string> RequiredAny { get; init; } = [];

    /// <summary>可选：同一事件中每个正则都至少匹配一行，规则才生效。</summary>
    public IReadOnlyList<string> RequiredAll { get; init; } = [];

    /// <summary>至少命中多少个事件才输出结论；默认 1。</summary>
    public int MinOccurrences { get; init; } = 1;

    public DiagnosticConfidence Confidence { get; init; } = DiagnosticConfidence.Undetermined;

    public IReadOnlyList<string> SuggestedActions { get; init; } = [];

    /// <summary>面向用户的解释，必须说明规则依据，不允许只给关键词。</summary>
    public string Explanation { get; init; } = "";

    public IReadOnlyList<string> SourceIds { get; init; } = [];

    /// <summary>停用或撤回的规则仍保留在知识库中，但不参与诊断。</summary>
    public bool Disabled { get; init; }
}

/// <summary>
/// 已通过加载校验的诊断知识库。知识库独立于引擎版本化，诊断报告始终记录本次使用的知识库版本。
/// </summary>
public sealed record KnowledgeBase
{
    public int SchemaVersion { get; init; } = KnowledgeBaseLoader.CurrentSchemaVersion;

    public string KnowledgeVersion { get; init; } = "";

    public DateTimeOffset PublishedAtUtc { get; init; }

    public IReadOnlyList<KnowledgeRule> Rules { get; init; } = [];

    public IReadOnlyList<KnowledgeSource> Sources { get; init; } = [];

    /// <summary>加载期间产生的非致命警告，例如引用了不存在的来源。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>参与诊断的规则数量（不含 disabled）。</summary>
    public int EnabledRuleCount => Rules.Count(rule => !rule.Disabled);
}
