// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Diagnostics;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorldDiagnostics = Ecl.World.Diagnostics;

namespace Ecl.Minecraft.Diagnostics;

/// <summary>世界事实源收集结果：事实、定位目标与本次收集的警告（不伪造“没有世界问题”）。</summary>
public sealed record DiagnosticWorldFactResult(
    IReadOnlyList<DiagnosticFact> Facts,
    IReadOnlyList<DiagnosticWorldTarget> Targets,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 世界诊断证据源（F-10 S6）。输入只来自会话登记的证据文件，不枚举实例目录、不读取世界存档；
/// 输出结构化事实与 <see cref="DiagnosticWorldTarget"/>，定位是否可用于地图标记由
/// <see cref="DiagnosticWorldTarget.SupportsMapHighlight"/> 统一判定。
/// </summary>
public interface IDiagnosticWorldFactSource
{
    Task<DiagnosticWorldFactResult> CollectAsync(
        DiagnosticFactContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// 把 <c>Ecl.World.Diagnostics</c> 的日志事件映射成统一诊断事实与定位目标。
/// 只读取会话登记的证据文件；不把 Region 级线索、玩家位置或推断维度提升为区块定位。
/// </summary>
public sealed class WorldEventFactSource(
    WorldDiagnostics.WorldDiagnosticsParser parser,
    ILogger<WorldEventFactSource>? logger = null) : IDiagnosticWorldFactSource
{
    private const int MaxFacts = 256;
    private const int MaxTargets = 128;

    private readonly ILogger<WorldEventFactSource> _logger = logger ?? NullLogger<WorldEventFactSource>.Instance;

    public async Task<DiagnosticWorldFactResult> CollectAsync(
        DiagnosticFactContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var warnings = new List<string>();
        var files = ResolveEvidenceFiles(context.Evidence);
        if (files.Count == 0)
        {
            warnings.Add("会话没有登记可读取的日志/崩溃报告，世界诊断未执行。");
            return new DiagnosticWorldFactResult([], [], warnings);
        }

        WorldDiagnostics.DiagnosticAnalysisResult analyzed;
        try
        {
            analyzed = await parser
                .AnalyzeAsync(
                    context.InstanceId,
                    context.Session.SessionId,
                    files,
                    cancellationToken,
                    worldFolder: null)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"世界诊断读取证据失败：{exception.Message}");
            return new DiagnosticWorldFactResult([], [], warnings);
        }

        if (!string.IsNullOrWhiteSpace(analyzed.Error))
        {
            warnings.Add($"世界诊断未完整执行：{analyzed.Error}");
        }

        if (analyzed.Truncated)
        {
            warnings.Add("世界诊断证据超过单文件读取上限，已截断；未分析的行不会被推断为“没有异常”。");
        }

        var facts = new List<DiagnosticFact>();
        var targets = new List<DiagnosticWorldTarget>();
        foreach (var worldEvent in analyzed.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (facts.Count >= MaxFacts)
            {
                warnings.Add($"世界诊断事件超过 {MaxFacts} 条，已截断；不得按截断结果声称完整。");
                break;
            }

            var target = WorldDiagnosticTargetMapping.TryCreate(worldEvent);
            facts.Add(ToFact(worldEvent, target));

            if (target is not null && targets.Count < MaxTargets &&
                (target.DimensionId is not null || target.HasChunk ||
                 target.Granularity == DiagnosticWorldGranularity.Region))
            {
                targets.Add(target);
            }
        }

        if (facts.Count == 0)
        {
            warnings.Add("未发现可定位的世界诊断事件；只报告证据中出现过的线索，不推断未出现的区块或维度。");
        }

        _logger.LogInformation(
            "世界诊断事实源完成：实例 {InstanceId}，证据文件 {Files}，扫描行 {Lines}，事实 {Facts}，定位目标 {Targets}。",
            context.InstanceId,
            files.Count,
            analyzed.LinesScanned,
            facts.Count,
            targets.Count);

        return new DiagnosticWorldFactResult(facts, targets, warnings);
    }

    private static IReadOnlyList<string> ResolveEvidenceFiles(IReadOnlyList<SessionEvidenceItem> evidence)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in evidence)
        {
            if (item.Availability is SessionSourceAvailability.Missing
                or SessionSourceAvailability.NotGenerated
                or SessionSourceAvailability.Unreadable)
            {
                continue;
            }

            var path = item.Path ?? item.Artifact.CapturedPath ?? item.Artifact.SourcePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !seen.Add(path))
            {
                continue;
            }

            files.Add(path);
        }

        return files;
    }

    private static DiagnosticFact ToFact(WorldDiagnostics.DiagnosticEvent worldEvent, DiagnosticWorldTarget? target)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["category"] = worldEvent.Category.ToString(),
            ["severity"] = worldEvent.Severity.ToString(),
            ["occurrences"] = worldEvent.OccurrenceCount.ToString(CultureInfo.InvariantCulture),
            ["locationConfidence"] = target?.Confidence.ToString() ?? DiagnosticConfidence.Undetermined.ToString(),
            ["supportsMapHighlight"] = (target?.SupportsMapHighlight ?? false) ? "true" : "false",
        };

        if (!string.IsNullOrWhiteSpace(worldEvent.ExceptionType))
        {
            attributes["exceptionType"] = worldEvent.ExceptionType!;
        }

        if (!string.IsNullOrWhiteSpace(worldEvent.WorldFolder))
        {
            attributes["worldFolder"] = worldEvent.WorldFolder!;
        }

        if (target is not null)
        {
            if (!string.IsNullOrWhiteSpace(target.DimensionId))
            {
                attributes["dimension"] = target.DimensionId!;
            }

            attributes["dimensionInferred"] = target.DimensionInferred ? "true" : "false";
            attributes["granularity"] = target.Granularity.ToString();
            AddCoordinate(attributes, "regionX", target.RegionX);
            AddCoordinate(attributes, "regionZ", target.RegionZ);
            AddCoordinate(attributes, "chunkX", target.ChunkX);
            AddCoordinate(attributes, "chunkZ", target.ChunkZ);
        }

        // 事件本身是直接证据；只有定位信息不完整（维度推断/Region 级/无法定位）时才降为“高度疑似”。
        var confidence = target is not null && target.HasExplicitDimension &&
                         target.Granularity is not DiagnosticWorldGranularity.Unknown
            ? DiagnosticConfidence.DirectEvidence
            : DiagnosticConfidence.HighlyLikely;

        return DiagnosticFact.Create(
            KindOf(worldEvent.Category),
            Truncate(worldEvent.Summary, 200),
            DiagnosticFactConfidence.WorldDiagnostics,
            related: worldEvent.Location.DimensionId,
            attributes: attributes,
            confidence: confidence);
    }

    private static void AddCoordinate(Dictionary<string, string> attributes, string name, int? value)
    {
        if (value.HasValue)
        {
            attributes[name] = value.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string KindOf(WorldDiagnostics.DiagnosticCategory category) => category switch
    {
        WorldDiagnostics.DiagnosticCategory.ChunkLoad or WorldDiagnostics.DiagnosticCategory.ChunkDecode =>
            DiagnosticFactKinds.WorldChunkAnomaly,
        WorldDiagnostics.DiagnosticCategory.NbtDecode => DiagnosticFactKinds.WorldNbtAnomaly,
        WorldDiagnostics.DiagnosticCategory.EntityTick or WorldDiagnostics.DiagnosticCategory.EntityRender =>
            DiagnosticFactKinds.WorldEntityAnomaly,
        WorldDiagnostics.DiagnosticCategory.RegionAccess => DiagnosticFactKinds.WorldRegionAnomaly,
        _ => DiagnosticFactKinds.WorldUnknownAnomaly,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

/// <summary>
/// <c>Ecl.World.Diagnostics.DiagnosticLocation</c> → <see cref="DiagnosticWorldTarget"/> 的唯一映射。
/// 世界地图高亮与诊断报告共用同一判定，避免两处各自解释“什么才算可定位区块”。
/// </summary>
public static class WorldDiagnosticTargetMapping
{
    /// <summary>把世界诊断事件映射为定位目标；完全无定位信息时返回 <c>null</c>。</summary>
    public static DiagnosticWorldTarget? TryCreate(WorldDiagnostics.DiagnosticEvent worldEvent)
    {
        ArgumentNullException.ThrowIfNull(worldEvent);
        var location = worldEvent.Location;
        var granularity = GranularityOf(location);
        if (granularity == DiagnosticWorldGranularity.Unknown && location.DimensionId is null)
        {
            return null;
        }

        var hasChunk = granularity is DiagnosticWorldGranularity.Chunk or DiagnosticWorldGranularity.Block;
        var hasBlock = granularity == DiagnosticWorldGranularity.Block;
        return new DiagnosticWorldTarget
        {
            WorldFolder = worldEvent.WorldFolder,
            DimensionId = location.DimensionId,
            DimensionInferred = location.DimensionInferred,
            Granularity = granularity,
            RegionX = location.RegionX,
            RegionZ = location.RegionZ,
            // Region/未知粒度不保留区块坐标：玩家位置或 Region 级线索不得被读成“出问题的区块”。
            ChunkX = hasChunk ? location.ChunkX : null,
            ChunkZ = hasChunk ? location.ChunkZ : null,
            BlockX = hasBlock ? location.BlockX : null,
            BlockY = hasBlock ? location.BlockY : null,
            BlockZ = hasBlock ? location.BlockZ : null,
            Confidence = ConfidenceOf(location.Confidence),
        };
    }

    /// <summary>是否允许把该事件标记到世界地图上（统一入口）。</summary>
    public static bool SupportsMapHighlight(WorldDiagnostics.DiagnosticEvent worldEvent) =>
        TryCreate(worldEvent)?.SupportsMapHighlight == true;

    private static DiagnosticWorldGranularity GranularityOf(WorldDiagnostics.DiagnosticLocation location) =>
        location.Confidence switch
        {
            WorldDiagnostics.DiagnosticConfidence.Exact
                when location.ChunkX.HasValue && location.ChunkZ.HasValue =>
                location.BlockX.HasValue ? DiagnosticWorldGranularity.Block : DiagnosticWorldGranularity.Chunk,
            WorldDiagnostics.DiagnosticConfidence.Derived when location.BlockX.HasValue =>
                DiagnosticWorldGranularity.Block,
            // LocationOnly 覆盖 Region 正则与玩家坐标：两者都不能定位“出问题的区块”。
            WorldDiagnostics.DiagnosticConfidence.LocationOnly => DiagnosticWorldGranularity.Region,
            _ => DiagnosticWorldGranularity.Unknown,
        };

    private static DiagnosticConfidence ConfidenceOf(WorldDiagnostics.DiagnosticConfidence confidence) =>
        confidence switch
        {
            WorldDiagnostics.DiagnosticConfidence.Exact => DiagnosticConfidence.DirectEvidence,
            WorldDiagnostics.DiagnosticConfidence.Derived => DiagnosticConfidence.DirectEvidence,
            WorldDiagnostics.DiagnosticConfidence.Inferred => DiagnosticConfidence.HighlyLikely,
            WorldDiagnostics.DiagnosticConfidence.LocationOnly => DiagnosticConfidence.HighlyLikely,
            _ => DiagnosticConfidence.Undetermined,
        };
}
