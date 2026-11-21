// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Ecl.Core.Localization;
using Ecl.Minecraft.Security;

namespace Ecl.App.Services;

/// <summary>
/// 安全发现的呈现格式：等级本地化与实例内相对路径规范化。
/// 安全中心与启动前风险确认共用同一套规则，避免一处显示完整磁盘路径。
/// </summary>
public static class SecurityFindingPresentation
{
    /// <summary>等级键，与 <c>security.finding.level.*</c> 文案一一对应。</summary>
    public static string LevelKey(ThreatLevel level) => level switch
    {
        ThreatLevel.ConfirmedThreat => "confirmed",
        ThreatLevel.HighRisk => "high",
        ThreatLevel.Suspicious => "suspicious",
        ThreatLevel.Notice => "notice",
        ThreatLevel.Unknown => "unknown",
        _ => "clean",
    };

    public static string LevelText(ThreatLevel level, ILocalizer localizer) =>
        localizer.Get($"security.finding.level.{LevelKey(level)}");

    /// <summary>
    /// 规范化显示用的相对路径：统一为反斜杠、去掉当前目录前缀，并防御性拒绝盘符、
    /// UNC 根与上跳段；异常输入只保留文件名，避免泄露完整路径。
    /// </summary>
    public static string FormatRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath.Replace('/', '\\').Trim();
        while (normalized.StartsWith(".\\", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (normalized.Contains(':', StringComparison.Ordinal) ||
            normalized.StartsWith("\\\\", StringComparison.Ordinal) ||
            normalized.Split('\\').Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            return Path.GetFileName(normalized);
        }

        return normalized;
    }
}
