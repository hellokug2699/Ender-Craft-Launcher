// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Shell;

/// <summary>
/// 旧导航 Key 与深链接兼容（迁移规划 §5.8）。
///
/// <c>java</c> 已被规划移除，Java 管理迁入设置中心的 <c>java</c> 二级页：
/// 一旦 <c>java</c> 不再是导航目的地，旧深链接与 <c>ECL_START_PAGE</c> 必须重定向到 <c>settings</c>，
/// 而不是落到空白页面。别名只在**当前导航里不存在同名 Key** 时生效，
/// 因此迁移期间（B0–B2，<c>java</c> 仍是真实页面）行为完全不变。
/// </summary>
public static class NavigationAliases
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["java"] = "settings",
    };

    /// <summary>旧 Key → 目标 Key。</summary>
    public static IReadOnlyDictionary<string, string> LegacyAliases => Aliases;

    /// <summary>按导航 Key / ToolId / 旧别名解析；命中返回 true。</summary>
    public static bool TryResolve(string? rawKey, IReadOnlyCollection<string> knownKeys, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return false;
        }

        var trimmed = rawKey.Trim();

        foreach (var candidate in knownKeys)
        {
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                key = candidate;
                return true;
            }
        }

        if (!Aliases.TryGetValue(trimmed, out var aliasTarget))
        {
            return false;
        }

        foreach (var candidate in knownKeys)
        {
            if (string.Equals(candidate, aliasTarget, StringComparison.OrdinalIgnoreCase))
            {
                key = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 解析启动目的地：未设置时使用第一个目的地；无法解析时回退到第一个目的地，
    /// 并通过 <paramref name="usedFallback"/> 通知调用方记录日志（不得白屏）。
    /// </summary>
    public static string ResolveStartPage(
        string? rawKey,
        IReadOnlyList<string> orderedKeys,
        out bool usedFallback)
    {
        ArgumentNullException.ThrowIfNull(orderedKeys);
        if (orderedKeys.Count == 0)
        {
            throw new ArgumentException("至少需要一个导航目的地。", nameof(orderedKeys));
        }

        usedFallback = false;
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return orderedKeys[0];
        }

        if (TryResolve(rawKey, orderedKeys, out var resolved))
        {
            return resolved;
        }

        usedFallback = true;
        return orderedKeys[0];
    }
}
