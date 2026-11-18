// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Diagnostics;

/// <summary>世界诊断定位粒度。Region 级定位不得被提升为具体 Chunk（ADR 0030/0031）。</summary>
public enum DiagnosticWorldGranularity
{
    /// <summary>没有可定位坐标（只有维度或完全未知）。</summary>
    Unknown = 0,

    /// <summary>只有 Region 级线索；不得据此生成区块或地图标记。</summary>
    Region = 1,

    /// <summary>可定位到具体区块。</summary>
    Chunk = 2,

    /// <summary>可定位到具体方块坐标；区块坐标由方块坐标确定。</summary>
    Block = 3,
}

/// <summary>
/// 世界诊断定位目标（F-10 §4.4）。把游戏日志/崩溃报告中的世界线索收敛成结构化目标，
/// 并集中表达“是否允许在地图上标记”的硬约束：
/// 只有“明确维度 + 明确区块坐标 + 非推断维度”才允许标记；
/// Region 级、玩家位置级或维度推断的事件保持未定位，不允许提升为具体区块。
/// </summary>
public sealed record DiagnosticWorldTarget
{
    /// <summary>世界目录名（不含本机路径）；未知时为 <c>null</c>，不得推断为某个存档。</summary>
    public string? WorldFolder { get; init; }

    public string? DimensionId { get; init; }

    /// <summary>维度是否由名称线索推断（而非日志显式字段）。推断维度不得用于地图标记。</summary>
    public bool DimensionInferred { get; init; }

    public DiagnosticWorldGranularity Granularity { get; init; } = DiagnosticWorldGranularity.Unknown;

    public int? RegionX { get; init; }

    public int? RegionZ { get; init; }

    public int? ChunkX { get; init; }

    public int? ChunkZ { get; init; }

    public int? BlockX { get; init; }

    public int? BlockY { get; init; }

    public int? BlockZ { get; init; }

    public DiagnosticConfidence Confidence { get; init; } = DiagnosticConfidence.Undetermined;

    /// <summary>维度身份是否可用：必须显式出现且不是推断。</summary>
    public bool HasExplicitDimension =>
        !string.IsNullOrWhiteSpace(DimensionId) && !DimensionInferred;

    public bool HasChunk => ChunkX.HasValue && ChunkZ.HasValue;

    /// <summary>
    /// 地图标记的唯一判定入口（ADR 0030 §7 / ADR 0031 §7）：
    /// 明确维度 + 非推断 + 区块/方块粒度 + 区块坐标 + 至少“高度疑似”的定位置信度。
    /// 玩家位置与 Region 级线索永远不满足（它们不是“出问题的区块”）。
    /// </summary>
    public bool SupportsMapHighlight =>
        HasExplicitDimension &&
        Granularity is DiagnosticWorldGranularity.Chunk or DiagnosticWorldGranularity.Block &&
        HasChunk &&
        Confidence >= DiagnosticConfidence.HighlyLikely;

    /// <summary>面向用户/日志的定位摘要；不包含本机绝对路径。</summary>
    public string Describe()
    {
        var dimension = HasExplicitDimension
            ? DimensionId!
            : string.IsNullOrWhiteSpace(DimensionId)
                ? "未知维度"
                : $"{DimensionId}（维度推断）";
        var location = Granularity switch
        {
            DiagnosticWorldGranularity.Block when BlockX.HasValue && BlockY.HasValue && BlockZ.HasValue =>
                $"Block ({BlockX}, {BlockY}, {BlockZ})",
            DiagnosticWorldGranularity.Chunk when HasChunk => $"Chunk ({ChunkX}, {ChunkZ})",
            DiagnosticWorldGranularity.Region when RegionX.HasValue && RegionZ.HasValue =>
                $"Region r.{RegionX}.{RegionZ}",
            _ => "未定位",
        };
        var world = string.IsNullOrWhiteSpace(WorldFolder) ? string.Empty : $"{WorldFolder} · ";
        return $"{world}{dimension} · {location}";
    }
}
