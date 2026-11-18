// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Diagnostics;

/// <summary>
/// 结构化事实：来自会话 manifest、采集计量或性能采样的“已知值”，不是规则推断出的结论。
/// <para>
/// 事实默认是直接证据，但来自性能采样或启发式排序的事实只能支持“高度疑似”，
/// 不得直接表述为根因——这正是 <see cref="DiagnosticFactConfidence.Cap"/> 存在的原因。
/// </para>
/// </summary>
public sealed record DiagnosticFact
{
    /// <summary>事实类型；规则通过 <see cref="KnowledgeRule.RequiresFacts"/> 引用它。</summary>
    public string Kind { get; init; } = "";

    /// <summary>事实主体，例如 Java 版本号、退出码或采集方式。</summary>
    public string Subject { get; init; } = "";

    /// <summary>可选关联对象（例如相关模组 ID）；没有时保持 <c>null</c>。</summary>
    public string? Related { get; init; }

    /// <summary>事实来源 ID，用于在报告里说明“这个事实从哪里来”。</summary>
    public string SourceId { get; init; } = "";

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly DiagnosticConfidence _confidence = DiagnosticConfidence.DirectEvidence;

    /// <summary>事实置信度。来自 performance/启发式来源时会被上限压到 <see cref="DiagnosticConfidence.HighlyLikely"/>。</summary>
    public DiagnosticConfidence Confidence
    {
        get => _confidence;
        init => _confidence = DiagnosticFactConfidence.Cap(SourceId, value);
    }

    /// <summary>
    /// 创建事实的唯一推荐入口：保证来源与置信度上限规则一起生效。
    /// 直接使用对象初始化器时，请把 <see cref="SourceId"/> 写在 <see cref="Confidence"/> 之前。
    /// </summary>
    public static DiagnosticFact Create(
        string kind,
        string subject,
        string sourceId,
        string? related = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        DiagnosticConfidence confidence = DiagnosticConfidence.DirectEvidence) =>
        new()
        {
            Kind = kind,
            Subject = subject,
            Related = related,
            SourceId = sourceId,
            Attributes = attributes ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Confidence = DiagnosticFactConfidence.Cap(sourceId, confidence),
        };
}

/// <summary>已知事实类型。知识库规则通过这些稳定字符串引用事实，而不是耦合到具体解析器实现。</summary>
public static class DiagnosticFactKinds
{
    public const string JavaVersion = "session.java_version";

    public const string Loader = "session.loader";

    public const string ExitCode = "session.exit_code";

    public const string CaptureMode = "session.capture_mode";

    public const string CaptureDropped = "session.capture_dropped";

    public const string ConsoleCaptureGap = "session.console_capture_gap";

    /// <summary>世界证据事实（F-10 S6）：来自会话证据中的区块/区域/实体/NBT 异常行。</summary>
    public const string WorldChunkAnomaly = "world.chunk_anomaly";

    public const string WorldNbtAnomaly = "world.nbt_anomaly";

    public const string WorldEntityAnomaly = "world.entity_anomaly";

    public const string WorldRegionAnomaly = "world.region_anomaly";

    public const string WorldUnknownAnomaly = "world.unknown_anomaly";

    /// <summary>已安装 Mod 的本地元数据事实（F-10 S9）。</summary>
    public const string ModInstalled = "content.mod";

    /// <summary>本地元数据声明的必需依赖缺失；运行时仍可能由其它模组/嵌套模块满足。</summary>
    public const string ModMissingDependency = "content.mod.missing_dependency";

    public const string ModDisabledDependency = "content.mod.disabled_dependency";

    public const string ModDuplicate = "content.mod.duplicate";

    public const string ModConflict = "content.mod.conflict";

    public const string ModLoaderMismatch = "content.mod.loader_mismatch";

    public const string ModInvalidMetadata = "content.mod.invalid_metadata";
}

/// <summary>事实来源 ID 与置信度上限规则。</summary>
public static class DiagnosticFactConfidence
{
    /// <summary>会话 manifest 是直接证据。</summary>
    public const string SessionManifest = "session.manifest";

    /// <summary>性能采样只描述观测窗口，不能单独证明根因。</summary>
    public const string Performance = "performance.metrics";

    /// <summary>崩溃嫌疑排序是启发式，不是结论。</summary>
    public const string CrashHeuristic = "heuristic.crash-suspects";

    /// <summary>世界证据来自会话登记日志的解析结果，属于直接证据来源。</summary>
    public const string WorldDiagnostics = "world.diagnostics";

    /// <summary>已安装 Mod 的本地静态元数据与本地依赖检查结果，属于直接证据来源。</summary>
    public const string ModInventory = "instance.mod_inventory";

    /// <summary>
    /// 性能与启发式来源的事实置信度不得高于 <see cref="DiagnosticConfidence.HighlyLikely"/>。
    /// 其他来源（例如会话 manifest）保持调用方给出的置信度。
    /// </summary>
    public static DiagnosticConfidence Cap(string? sourceId, DiagnosticConfidence confidence)
    {
        if (IsInferenceSource(sourceId) && confidence > DiagnosticConfidence.HighlyLikely)
        {
            return DiagnosticConfidence.HighlyLikely;
        }

        return confidence;
    }

    /// <summary>是否为不能单独支撑“直接证据”的来源。</summary>
    public static bool IsInferenceSource(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return false;
        }

        return sourceId.StartsWith("performance", StringComparison.OrdinalIgnoreCase) ||
               sourceId.StartsWith("heuristic", StringComparison.OrdinalIgnoreCase);
    }
}
