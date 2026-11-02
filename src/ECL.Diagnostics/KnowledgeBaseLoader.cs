// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using Ecl.Core;
using Ecl.Core.Json;

namespace Ecl.Diagnostics;

/// <summary>
/// 诊断知识库加载与校验（Data Addition 数据模型）。
/// <para>JSON 顶层字段：<c>schemaVersion</c>、<c>knowledgeVersion</c>、<c>publishedAtUtc</c>、<c>rules[]</c>、<c>sources[]</c>。</para>
/// <para>
/// 加载时校验 schema 版本、必填字段、规则版本、枚举值以及全部正则表达式。
/// 任何格式错误都以 <see cref="Result{T}"/> 返回明确错误，不抛异常阻塞宿主；
/// 不兼容或损坏的知识库只会让本次诊断降级为“无法确定”，不会阻止启动器运行。
/// </para>
/// <para>该过程完全在本地进行，不下载、不上传，也不访问来源 URL。</para>
/// </summary>
public static class KnowledgeBaseLoader
{
    /// <summary>当前支持的唯一 schema 版本。不兼容版本将被明确拒绝。</summary>
    public const int CurrentSchemaVersion = 1;

    public const int DefaultMaxBytes = 4 * 1024 * 1024;
    public const int DefaultMaxLines = 100_000;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>读取并校验本地知识库文件。读取失败或内容损坏都返回错误结果。</summary>
    public static Result<KnowledgeBase> LoadFile(
        string path,
        int maxBytes = DefaultMaxBytes,
        int maxLines = DefaultMaxLines)
    {
        var read = BoundedTextReader.ReadFile(path, maxBytes, maxLines);
        if (!read.IsSuccess)
        {
            return Result<KnowledgeBase>.Fail(read.Error!);
        }

        return Load(read.Value!.Text, path);
    }

    /// <summary>校验并加载知识库 JSON。不会抛异常。</summary>
    public static Result<KnowledgeBase> Load(string json, string? sourceName = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Result<KnowledgeBase>.Fail(
                "knowledge.empty",
                "诊断知识库为空。",
                SourceDetail(sourceName, null));
        }

        KnowledgeBaseDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<KnowledgeBaseDocument>(json, EclJson.Default);
        }
        catch (JsonException exception)
        {
            return Result<KnowledgeBase>.Fail(
                "knowledge.invalid_json",
                "诊断知识库 JSON 无法解析。",
                SourceDetail(sourceName, exception.Message));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result<KnowledgeBase>.Fail(
                "knowledge.invalid_json",
                "诊断知识库无法解析。",
                SourceDetail(sourceName, exception.Message));
        }

        if (document is null)
        {
            return Result<KnowledgeBase>.Fail(
                "knowledge.invalid",
                "诊断知识库内容为空。",
                SourceDetail(sourceName, null));
        }

        if (document.SchemaVersion is null)
        {
            return Result<KnowledgeBase>.Fail(
                "knowledge.schema_missing",
                "诊断知识库缺少 schemaVersion。",
                SourceDetail(sourceName, null));
        }

        if (document.SchemaVersion.Value != CurrentSchemaVersion)
        {
            return Result<KnowledgeBase>.Fail(
                "knowledge.schema_incompatible",
                $"诊断知识库 schemaVersion {document.SchemaVersion.Value} 不受支持，当前只支持 {CurrentSchemaVersion}。",
                SourceDetail(sourceName, null));
        }

        var knowledgeVersion = document.KnowledgeVersion?.Trim();
        if (string.IsNullOrEmpty(knowledgeVersion))
        {
            return Result<KnowledgeBase>.Fail(
                "knowledge.invalid",
                "诊断知识库缺少 knowledgeVersion。",
                SourceDetail(sourceName, "knowledgeVersion"));
        }

        if (knowledgeVersion.Length > 128 || knowledgeVersion.Any(char.IsWhiteSpace))
        {
            return Result<KnowledgeBase>.Fail(
                "knowledge.invalid",
                "knowledgeVersion 格式无效。",
                SourceDetail(sourceName, "knowledgeVersion"));
        }

        if (document.PublishedAtUtc is null || document.PublishedAtUtc == default)
        {
            return Result<KnowledgeBase>.Fail(
                "knowledge.invalid",
                "诊断知识库缺少 publishedAtUtc。",
                SourceDetail(sourceName, "publishedAtUtc"));
        }

        if (document.Rules is null)
        {
            return Result<KnowledgeBase>.Fail(
                "knowledge.invalid",
                "诊断知识库缺少 rules 数组。",
                SourceDetail(sourceName, "rules"));
        }

        if (document.Sources is null)
        {
            return Result<KnowledgeBase>.Fail(
                "knowledge.invalid",
                "诊断知识库缺少 sources 数组。",
                SourceDetail(sourceName, "sources"));
        }

        var warnings = new List<string>();
        var sources = new List<KnowledgeSource>();
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.Sources.Count; index++)
        {
            var source = document.Sources[index];
            if (source is null)
            {
                return Invalid(sourceName, $"sources[{index}]", "来源条目为 null。");
            }

            var sourceId = source.Id?.Trim();
            if (string.IsNullOrEmpty(sourceId))
            {
                return Invalid(sourceName, $"sources[{index}].id", "来源缺少 id。");
            }

            if (!sourceIds.Add(sourceId))
            {
                return Invalid(sourceName, $"sources[{index}].id", $"来源 id 重复：{sourceId}。");
            }

            var title = source.Title?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                return Invalid(sourceName, $"sources[{index}].title", $"来源 {sourceId} 缺少 title。");
            }

            sources.Add(new KnowledgeSource
            {
                Id = sourceId,
                Title = title,
                Publisher = TrimmedOrNull(source.Publisher),
                Url = TrimmedOrNull(source.Url),
                RetrievedAtUtc = source.RetrievedAtUtc,
            });
        }

        var rules = new List<KnowledgeRule>();
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.Rules.Count; index++)
        {
            var rule = document.Rules[index];
            if (rule is null)
            {
                return Invalid(sourceName, $"rules[{index}]", "规则条目为 null。");
            }

            var rulePath = $"rules[{index}]";
            var ruleId = rule.Id?.Trim();
            if (string.IsNullOrEmpty(ruleId))
            {
                return Invalid(sourceName, $"{rulePath}.id", "规则缺少 id。");
            }

            if (!ruleIds.Add(ruleId))
            {
                return Invalid(sourceName, $"{rulePath}.id", $"规则 id 重复：{ruleId}。");
            }

            if (rule.Version is null || rule.Version.Value <= 0)
            {
                return Invalid(sourceName, $"{rulePath}.version", $"规则 {ruleId} 的 version 必须大于 0。");
            }

            var title = rule.Title?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                return Invalid(sourceName, $"{rulePath}.title", $"规则 {ruleId} 缺少 title。");
            }

            var category = rule.Category?.Trim();
            if (string.IsNullOrEmpty(category))
            {
                return Invalid(sourceName, $"{rulePath}.category", $"规则 {ruleId} 缺少 category。");
            }

            var pattern = rule.Pattern?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                return Invalid(sourceName, $"{rulePath}.pattern", $"规则 {ruleId} 缺少 pattern。");
            }

            if (!TryCompile(pattern, out _, out var patternError))
            {
                return Invalid(
                    sourceName,
                    $"{rulePath}.pattern",
                    $"规则 {ruleId} 的正则无效：{patternError}");
            }

            var requiredAny = NormalizePatterns(rule.RequiredAny);
            foreach (var required in requiredAny)
            {
                if (!TryCompile(required, out _, out var requiredError))
                {
                    return Invalid(
                        sourceName,
                        $"{rulePath}.requiredAny",
                        $"规则 {ruleId} 的 requiredAny 正则无效：{requiredError}");
                }
            }

            var requiredAll = NormalizePatterns(rule.RequiredAll);
            foreach (var required in requiredAll)
            {
                if (!TryCompile(required, out _, out var requiredError))
                {
                    return Invalid(
                        sourceName,
                        $"{rulePath}.requiredAll",
                        $"规则 {ruleId} 的 requiredAll 正则无效：{requiredError}");
                }
            }

            var minOccurrences = rule.MinOccurrences ?? 1;
            if (minOccurrences <= 0)
            {
                return Invalid(
                    sourceName,
                    $"{rulePath}.minOccurrences",
                    $"规则 {ruleId} 的 minOccurrences 必须大于 0。");
            }

            if (rule.Severity is { } severity && !Enum.IsDefined(severity))
            {
                return Invalid(sourceName, $"{rulePath}.severity", $"规则 {ruleId} 的 severity 无效。");
            }

            if (rule.Confidence is { } confidence && !Enum.IsDefined(confidence))
            {
                return Invalid(sourceName, $"{rulePath}.confidence", $"规则 {ruleId} 的 confidence 无效。");
            }

            var explanation = rule.Explanation?.Trim();
            if (string.IsNullOrEmpty(explanation))
            {
                return Invalid(sourceName, $"{rulePath}.explanation", $"规则 {ruleId} 缺少 explanation。");
            }

            var ruleSources = NormalizeStrings(rule.SourceIds);
            foreach (var sourceId in ruleSources)
            {
                if (!sourceIds.Contains(sourceId))
                {
                    warnings.Add($"规则 {ruleId} 引用了不存在的来源：{sourceId}。");
                }
            }

            rules.Add(new KnowledgeRule
            {
                Id = ruleId,
                Version = rule.Version.Value,
                Title = title,
                Severity = rule.Severity ?? DiagnosticSeverity.Warning,
                Category = category,
                Pattern = pattern,
                RequiredAny = requiredAny,
                RequiredAll = requiredAll,
                MinOccurrences = minOccurrences,
                Confidence = rule.Confidence ?? DiagnosticConfidence.Undetermined,
                SuggestedActions = NormalizeStrings(rule.SuggestedActions),
                Explanation = explanation,
                SourceIds = ruleSources,
                Disabled = rule.Disabled ?? false,
            });
        }

        if (rules.Count > 0 && rules.All(rule => rule.Disabled))
        {
            warnings.Add("知识库中的所有规则均已停用，本次诊断将无法命中规则。");
        }

        return Result<KnowledgeBase>.Ok(new KnowledgeBase
        {
            SchemaVersion = CurrentSchemaVersion,
            KnowledgeVersion = knowledgeVersion,
            PublishedAtUtc = document.PublishedAtUtc.Value,
            Rules = rules,
            Sources = sources,
            Warnings = warnings,
        });
    }

    /// <summary>供引擎复用的安全正则编译；失败返回 <c>false</c> 而不是抛异常。</summary>
    internal static bool TryCompile(string pattern, out Regex? regex, out string? error)
    {
        try
        {
            regex = new Regex(
                pattern,
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                RegexTimeout);
            error = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            regex = null;
            error = exception.Message;
            return false;
        }
        catch (RegexMatchTimeoutException exception)
        {
            regex = null;
            error = exception.Message;
            return false;
        }
    }

    private static IReadOnlyList<string> NormalizePatterns(IReadOnlyList<string>? patterns) =>
        NormalizeStrings(patterns);

    private static IReadOnlyList<string> NormalizeStrings(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static string? TrimmedOrNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static Result<KnowledgeBase> Invalid(string? sourceName, string field, string message) =>
        Result<KnowledgeBase>.Fail(
            "knowledge.invalid",
            "诊断知识库格式无效。",
            SourceDetail(sourceName, $"{field}: {message}"));

    private static string? SourceDetail(string? sourceName, string? detail)
    {
        var file = string.IsNullOrWhiteSpace(sourceName)
            ? null
            : BoundedTextReader.SafeFileName(sourceName);
        if (string.IsNullOrEmpty(file))
        {
            return detail;
        }

        return string.IsNullOrEmpty(detail) ? file : $"{file}: {detail}";
    }

    private sealed class KnowledgeBaseDocument
    {
        public int? SchemaVersion { get; set; }

        public string? KnowledgeVersion { get; set; }

        public DateTimeOffset? PublishedAtUtc { get; set; }

        public List<KnowledgeRuleDocument?>? Rules { get; set; }

        public List<KnowledgeSourceDocument?>? Sources { get; set; }
    }

    private sealed class KnowledgeRuleDocument
    {
        public string? Id { get; set; }

        public int? Version { get; set; }

        public string? Title { get; set; }

        public DiagnosticSeverity? Severity { get; set; }

        public string? Category { get; set; }

        public string? Pattern { get; set; }

        public List<string>? RequiredAny { get; set; }

        public List<string>? RequiredAll { get; set; }

        public int? MinOccurrences { get; set; }

        public DiagnosticConfidence? Confidence { get; set; }

        public List<string>? SuggestedActions { get; set; }

        public string? Explanation { get; set; }

        public List<string>? SourceIds { get; set; }

        public bool? Disabled { get; set; }
    }

    private sealed class KnowledgeSourceDocument
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? Publisher { get; set; }

        public string? Url { get; set; }

        public DateTimeOffset? RetrievedAtUtc { get; set; }
    }
}
