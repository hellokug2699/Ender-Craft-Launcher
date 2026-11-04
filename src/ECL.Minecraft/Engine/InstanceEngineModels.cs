// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Engine;

/// <summary>
/// 证据可信度（ADR 0045 §5）。扫描与判断必须区分“已确认”“推断”“未知”“未扫描”，
/// 不能用一个布尔值把未读取或未完成的结果伪装成安全/兼容。
/// </summary>
public enum EvidenceConfidence
{
    Confirmed,
    Inferred,
    Unknown,
    NotScanned,
    Partial,
    Failed,
}

/// <summary>一个可追溯的扫描证据。Source 与 Rule 用于在结果变化时判断缓存是否仍然适用。</summary>
public sealed record EvidenceItem(
    string Source,
    string Rule,
    EvidenceConfidence Confidence,
    string Summary)
{
    public static EvidenceItem Confirmed(string source, string rule, string summary) =>
        new(source, rule, EvidenceConfidence.Confirmed, summary);

    public static EvidenceItem Unknown(string source, string rule, string summary) =>
        new(source, rule, EvidenceConfidence.Unknown, summary);
}

/// <summary>Instance Engine 的规则/扫描器版本。缓存必须包含这些版本，规则变化后不得复用旧结论。</summary>
public static class InstanceEngineVersions
{
    public const string Engine = "1.0.0";

    public const string Scanner = "1.0.0";

    public const string Rules = "2026-11-03";

    public const string InventorySchema = "1";

    public const string SnapshotSchema = "1";

    public const string TransactionSchema = "1";

    public const string IdentitySchema = "1";
}
