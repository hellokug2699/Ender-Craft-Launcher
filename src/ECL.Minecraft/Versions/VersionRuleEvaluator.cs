// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace Ecl.Minecraft.Versions;

/// <summary>
/// 计算版本 JSON / 库条目中的 rules。rules 的语义与 Mojang 版本清单一致：
/// 没有任何 rule 时默认允许；按顺序应用匹配的 rule，最后一次匹配的 action 生效。
/// </summary>
public static class VersionRuleEvaluator
{
    public static bool IsAllowed(
        IReadOnlyList<VersionRule>? rules,
        string osName,
        string osArch,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, osName, osArch, features))
            {
                continue;
            }

            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    public static bool Matches(
        VersionRule rule,
        string osName,
        string osArch,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rule.Os is not null)
        {
            if (!string.IsNullOrWhiteSpace(rule.Os.Name) &&
                !OsNameMatches(rule.Os.Name, osName, osArch))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(rule.Os.Arch) &&
                !ArchMatches(rule.Os.Arch, osArch))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(rule.Os.Version) &&
                !OsVersionMatches(rule.Os.Version))
            {
                return false;
            }
        }

        if (rule.Features is not null)
        {
            foreach (var feature in rule.Features)
            {
                // 未显式启用的 feature 视为 false；启动器目前不主动开启 demo/quick play 等特性。
                if (features is null ||
                    !features.TryGetValue(feature.Key, out var actual) ||
                    actual != feature.Value)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static bool OsKeyMatches(string candidate, string osName)
    {
        var normalized = candidate.Trim().ToLowerInvariant();
        var actual = osName.Trim().ToLowerInvariant();

        if (actual is "osx" or "macos")
        {
            return normalized is "osx" or "macos" or "mac-os" or "mac";
        }

        return string.Equals(normalized, actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>os.name</c> 还可能出现「OS-架构」组合写法（PCL 对 Apple Silicon 会写 <c>osx-arm64</c>）。
    /// 这类 token 以前永远不匹配，导致规则被静默忽略：<c>allow osx-arm64</c> 不生效、
    /// <c>disallow osx-arm64</c> 也不生效。这里拆成 OS 段与架构段分别判断。
    /// </summary>
    public static bool OsNameMatches(string name, string osName, string osArch)
    {
        var normalized = name.Trim().ToLowerInvariant();
        var separator = normalized.LastIndexOf('-');
        if (separator > 0)
        {
            var archPart = normalized[(separator + 1)..];
            if (IsKnownArchToken(archPart))
            {
                return OsKeyMatches(normalized[..separator], osName) &&
                       ArchMatches(archPart, osArch);
            }
        }

        return OsKeyMatches(normalized, osName);
    }

    private static bool IsKnownArchToken(string token) =>
        token is "x86_64" or "x86-64" or "x64" or "amd64" or "64"
            or "x86" or "i386" or "i686" or "32"
            or "arm64" or "aarch64" or "arm_64" or "aarch_64" or "armv8"
            or "arm32" or "armv7" or "arm";

    public static bool ArchMatches(string candidate, string osArch) =>
        string.Equals(NormalizeArch(candidate), NormalizeArch(osArch), StringComparison.Ordinal);

    public static string NormalizeArch(string arch) =>
        arch.Trim().ToLowerInvariant() switch
        {
            "x86_64" or "x86-64" or "amd64" or "x64" or "64" => "x86_64",
            "x86" or "i386" or "i686" or "32" => "x86",
            "arm64" or "aarch64" or "armv8" => "arm64",
            "arm32" or "armv7" or "arm" => "arm32",
            var other => other,
        };

    private static bool OsVersionMatches(string pattern)
    {
        try
        {
            return Regex.IsMatch(
                Environment.OSVersion.Version.ToString(),
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            // 版本 JSON 中的 os.version 非法时按“不匹配”处理，避免注入错误参数。
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
