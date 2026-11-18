// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Diagnostics;
using Ecl.Minecraft.Mods;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Diagnostics;

/// <summary>
/// Mod 关系事实源（F-10 S9）。只读取实例 <c>mods/</c> 目录中已安装文件的本地元数据
/// （fabric.mod.json / mods.toml / mcmod.info）与本地依赖检查结果，不访问网络、不读取 Mod 代码。
/// <para>
/// 事实是“本地静态元数据声明”而不是运行时结论：依赖可能由嵌套模块、条件依赖或加载器提供，
/// 因此缺失/停用依赖只到“高度疑似”，不得直接表述为“实例无法启动”。
/// </para>
/// </summary>
public sealed class ModRelationshipFactSource(
    IModService mods,
    ILogger<ModRelationshipFactSource>? logger = null) : IDiagnosticFactSource
{
    private const int MaxFacts = 512;

    private readonly IModService _mods = mods;
    private readonly ILogger<ModRelationshipFactSource> _logger = logger ?? NullLogger<ModRelationshipFactSource>.Instance;

    public string Id => DiagnosticFactConfidence.ModInventory;

    public async Task<IReadOnlyList<DiagnosticFact>> CollectAsync(
        DiagnosticFactContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        Result<ModScanReport> scan;
        try
        {
            scan = await _mods.ScanAsync(context.InstanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 事实源约定不抛异常：扫描失败只记录日志，报告仍生成，不把“没扫描到”伪装成“没有模组问题”。
            _logger.LogWarning(exception, "Mod 关系事实源扫描失败：实例 {InstanceId}", context.InstanceId);
            return [];
        }

        if (!scan.IsSuccess || scan.Value is null)
        {
            _logger.LogWarning(
                "Mod 关系事实源扫描失败：实例 {InstanceId}，{Code} {Message}",
                context.InstanceId,
                scan.Error?.Code,
                scan.Error?.Message);
            return [];
        }

        var report = scan.Value;
        var facts = new List<DiagnosticFact>();
        foreach (var entry in report.Mods)
        {
            if (facts.Count >= MaxFacts)
            {
                break;
            }

            var metadata = entry.Metadata;
            if (metadata is null)
            {
                // 无法读取元数据的事实由 report.Issues（InvalidMetadata）统一表达，避免重复记两条。
                continue;
            }

            facts.Add(InstalledFact(entry, metadata));
        }

        foreach (var issue in report.Issues)
        {
            if (facts.Count >= MaxFacts)
            {
                _logger.LogWarning("Mod 关系事实数量超过上限 {MaxFacts}，已截断报告事实。", MaxFacts);
                break;
            }

            var fact = ToFact(issue);
            if (fact is not null)
            {
                facts.Add(fact);
            }
        }

        _logger.LogInformation(
            "Mod 关系事实源完成：实例 {InstanceId}，模组 {Mods}，问题 {Issues}，事实 {Facts}。",
            context.InstanceId,
            report.Mods.Count,
            report.Issues.Count,
            facts.Count);
        return facts;
    }

    private static DiagnosticFact InstalledFact(ModEntry entry, ModMetadata metadata) =>
        DiagnosticFact.Create(
            DiagnosticFactKinds.ModInstalled,
            metadata.ModId,
            DiagnosticFactConfidence.ModInventory,
            attributes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fileName"] = entry.FileName,
                ["name"] = metadata.Name,
                ["version"] = metadata.Version,
                ["platform"] = metadata.Platform.ToString(),
                ["enabled"] = entry.Enabled ? "true" : "false",
                ["dependencyCount"] = metadata.Dependencies.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["providedIds"] = string.Join(',', metadata.ProvidedIds.Take(32)),
            });

    private static DiagnosticFact? ToFact(ModIssue issue) => issue.Kind switch
    {
        ModIssueKind.MissingDependency => DependencyFact(issue, DiagnosticFactKinds.ModMissingDependency),
        ModIssueKind.DisabledDependency => DependencyFact(issue, DiagnosticFactKinds.ModDisabledDependency),
        ModIssueKind.DuplicateModId => DiagnosticFact.Create(
            DiagnosticFactKinds.ModDuplicate,
            issue.ModId ?? string.Empty,
            DiagnosticFactConfidence.ModInventory,
            related: issue.RelatedModId,
            attributes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fileName"] = issue.FileName,
            }),
        ModIssueKind.Conflict => DiagnosticFact.Create(
            DiagnosticFactKinds.ModConflict,
            issue.ModId ?? string.Empty,
            DiagnosticFactConfidence.ModInventory,
            related: issue.RelatedModId,
            attributes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fileName"] = issue.FileName,
            }),
        ModIssueKind.LoaderMismatch => DiagnosticFact.Create(
            DiagnosticFactKinds.ModLoaderMismatch,
            issue.ModId ?? string.Empty,
            DiagnosticFactConfidence.ModInventory,
            attributes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fileName"] = issue.FileName,
                ["platform"] = issue.Detail ?? string.Empty,
            }),
        ModIssueKind.InvalidMetadata => DiagnosticFact.Create(
            DiagnosticFactKinds.ModInvalidMetadata,
            issue.ModId ?? string.Empty,
            DiagnosticFactConfidence.ModInventory,
            attributes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fileName"] = issue.FileName,
                ["detail"] = issue.Detail ?? string.Empty,
            }),
        _ => null,
    };

    private static DiagnosticFact DependencyFact(ModIssue issue, string kind)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fileName"] = issue.FileName,
        };
        if (!string.IsNullOrWhiteSpace(issue.Detail))
        {
            attributes["versionRange"] = issue.Detail;
        }

        // 本地元数据只能证明“声明了依赖”；运行时是否由其它来源满足无法仅凭静态文件确认。
        return DiagnosticFact.Create(
            kind,
            issue.ModId ?? string.Empty,
            DiagnosticFactConfidence.ModInventory,
            related: issue.RelatedModId,
            attributes: attributes,
            confidence: DiagnosticConfidence.HighlyLikely);
    }
}
