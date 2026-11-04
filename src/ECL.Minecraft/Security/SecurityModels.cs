// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Security;

public enum ThreatLevel
{
    Clean,
    Notice,
    Suspicious,
    HighRisk,
    ConfirmedThreat,
    Unknown,
}

public sealed record ThreatEvidence(
    string RuleId,
    string RuleVersion,
    ThreatLevel Level,
    string Summary,
    string? Location = null);

public sealed record ThreatFinding(
    string FindingId,
    string RelativePath,
    string? Sha256,
    ThreatLevel Level,
    IReadOnlyList<ThreatEvidence> Evidence,
    string Recommendation);

public sealed record ThreatScanReport(
    string ScanId,
    string InstanceId,
    DateTimeOffset ScannedAt,
    string ScannerVersion,
    string RulesVersion,
    IReadOnlyList<ThreatFinding> Findings,
    int FilesScanned,
    int ArchivesScanned,
    bool IsComplete,
    IReadOnlyList<string> Warnings)
{
    public ThreatLevel OverallLevel => Findings.Count == 0
        ? ThreatLevel.Clean
        : Findings.Max(finding => finding.Level);
}

public sealed record RiskAcceptanceRecord(
    string InstanceId,
    string RelativePath,
    string? Sha256,
    string RuleId,
    ThreatLevel Level,
    DateTimeOffset AcceptedAt,
    string ScannerVersion,
    string RulesVersion);

public sealed record LaunchSecurityDecision(
    bool Allowed,
    bool RequiresAcknowledgement,
    IReadOnlyList<ThreatFinding> Findings,
    string Message,
    bool AlreadyAccepted);

public sealed record QuarantineRecord(
    string QuarantineId,
    string InstanceId,
    string OriginalRelativePath,
    string Sha256,
    long Size,
    DateTimeOffset QuarantinedAt,
    ThreatLevel Level);

public sealed record SecurityScanOptions(
    bool IncludeDeepArchiveAnalysis = true,
    int MaxFiles = 5_000,
    long MaxArchiveBytes = 256L * 1024 * 1024);

public interface IThreatProtectionService
{
    Task<Result<ThreatScanReport>> ScanAsync(
        string instanceId,
        SecurityScanOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Result<ThreatScanReport?>> GetLatestScanAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>启动前门控：不执行全实例深度扫描，只消费最近扫描结果与本次启动参数。</summary>
    Task<Result<LaunchSecurityDecision>> EvaluateLaunchAsync(
        string instanceId,
        IReadOnlyList<string>? launchArguments = null,
        CancellationToken cancellationToken = default);

    /// <summary>记录用户“无视风险，继续启动”的明确选择；不会把检测结果改成 Clean。</summary>
    Task<Result> AcceptRiskAsync(
        string instanceId,
        IReadOnlyList<ThreatFinding> findings,
        CancellationToken cancellationToken = default);

    Task<Result<QuarantineRecord>> QuarantineAsync(
        string instanceId,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task<Result> RestoreQuarantineAsync(
        string instanceId,
        string quarantineId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<QuarantineRecord>>> ListQuarantineAsync(
        string instanceId,
        CancellationToken cancellationToken = default);
}
