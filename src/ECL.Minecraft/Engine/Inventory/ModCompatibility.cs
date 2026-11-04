// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;

namespace Ecl.Minecraft.Engine.Inventory;

/// <summary>
/// 模组版本范围判断。返回 <c>null</c> 表示范围无法解析，调用方必须显示 Unknown 而不是猜测。
/// 支持 Fabric/Quilt 的 <c>&gt;=1.20 &lt;1.21</c>、Maven 的 <c>[1.20,1.21)</c>、通配符与精确版本。
/// </summary>
public static class ModVersionRange
{
    public static bool? Matches(string? actualVersion, string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(actualVersion))
        {
            return null;
        }

        var trimmed = range.Trim();
        if (trimmed is "*" or "any" or "x")
        {
            return true;
        }

        var conditions = new List<string>();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('('))
        {
            var closing = trimmed.IndexOfAny([']', ')']);
            if (closing < 0)
            {
                return null;
            }

            var body = trimmed[1..closing];
            var parts = body.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2)
            {
                return null;
            }

            var lowerInclusive = trimmed[0] == '[';
            var upperInclusive = trimmed[closing] == ']';
            if (parts.Length >= 1 && parts[0].Length > 0)
            {
                conditions.Add($"{(lowerInclusive ? ">=" : ">")}{parts[0]}");
            }

            if (parts.Length == 2 && parts[1].Length > 0)
            {
                conditions.Add($"{(upperInclusive ? "<=" : "<")}{parts[1]}");
            }
        }
        else
        {
            var normalized = trimmed.Replace(',', ' ');
            conditions.AddRange(normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var condition in conditions)
        {
            var result = MatchCondition(actualVersion, condition);
            if (result is null)
            {
                return null;
            }

            if (result == false)
            {
                return false;
            }
        }

        return true;
    }

    private static bool? MatchCondition(string actual, string condition)
    {
        var operators = new[] { ">=", "<=", "==", ">", "<", "=", "~", "^" };
        foreach (var op in operators)
        {
            if (!condition.StartsWith(op, StringComparison.Ordinal))
            {
                continue;
            }

            var expected = condition[op.Length..].Trim();
            if (expected.Length == 0)
            {
                return null;
            }

            var comparison = Compare(actual, expected);
            if (comparison is null)
            {
                return null;
            }

            return op switch
            {
                ">=" => comparison >= 0,
                "<=" => comparison <= 0,
                ">" => comparison > 0,
                "<" => comparison < 0,
                "=" or "==" => comparison == 0,
                "~" => TildeMatches(actual, expected),
                "^" => CaretMatches(actual, expected),
                _ => null,
            };
        }

        if (condition.Contains('*') || condition.Contains('x') || condition.Contains('X'))
        {
            return WildcardMatches(actual, condition);
        }

        var exact = Compare(actual, condition);
        return exact is null ? null : exact == 0;
    }

    private static bool TildeMatches(string actual, string expected) =>
        ComparePrefix(actual, expected, 2);

    private static bool CaretMatches(string actual, string expected) =>
        ComparePrefix(actual, expected, 1);

    private static bool ComparePrefix(string actual, string expected, int parts)
    {
        var actualParts = SplitVersion(actual);
        var expectedParts = SplitVersion(expected);
        if (actualParts is null || expectedParts is null || expectedParts.Count < parts)
        {
            return false;
        }

        for (var index = 0; index < parts; index++)
        {
            if (index >= actualParts.Count || actualParts[index] != expectedParts[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool? WildcardMatches(string actual, string expected)
    {
        var expectedParts = expected.Split('.');
        var actualParts = SplitVersion(actual);
        if (actualParts is null)
        {
            return null;
        }

        for (var index = 0; index < expectedParts.Length; index++)
        {
            var part = expectedParts[index].Trim();
            if (part is "*" or "x" or "X")
            {
                return true;
            }

            if (index >= actualParts.Count)
            {
                return false;
            }

            if (!int.TryParse(part, out var value))
            {
                return null;
            }

            if (actualParts[index] != value)
            {
                return false;
            }
        }

        return true;
    }

    private static int? Compare(string left, string right)
    {
        var leftParts = SplitVersion(left);
        var rightParts = SplitVersion(right);
        if (leftParts is null || rightParts is null)
        {
            return null;
        }

        var count = Math.Max(leftParts.Count, rightParts.Count);
        for (var index = 0; index < count; index++)
        {
            var a = index < leftParts.Count ? leftParts[index] : 0;
            var b = index < rightParts.Count ? rightParts[index] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return 0;
    }

    private static List<int>? SplitVersion(string value)
    {
        var core = value.Trim();
        var separator = core.IndexOfAny(['-', '+']);
        if (separator >= 0)
        {
            core = core[..separator];
        }

        var parts = core.Split('.', StringSplitOptions.None);
        var result = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                return null;
            }

            if (!int.TryParse(part, out var numeric))
            {
                return null;
            }

            result.Add(numeric);
        }

        return result;
    }
}

/// <summary>基于 Loader 元数据的兼容性判断。未知时返回 Unknown，不把缺失证据当成兼容。</summary>
public static class ModCompatibility
{
    public static CompatibilityState EvaluateMinecraft(GameInstance instance, ModMetadata? metadata)
    {
        if (metadata is null)
        {
            return CompatibilityState.Unknown;
        }

        var minecraft = metadata.Dependencies.FirstOrDefault(dependency =>
            string.Equals(dependency.ModId, "minecraft", StringComparison.OrdinalIgnoreCase));
        if (minecraft is null || string.IsNullOrWhiteSpace(minecraft.VersionRange))
        {
            return CompatibilityState.Unknown;
        }

        var result = ModVersionRange.Matches(instance.GameVersion, minecraft.VersionRange);
        return result switch
        {
            true => CompatibilityState.Compatible,
            false => CompatibilityState.Incompatible,
            _ => CompatibilityState.Unknown,
        };
    }

    public static CompatibilityState EvaluateLoader(string instanceLoader, ModMetadata? metadata)
    {
        if (metadata is null)
        {
            return CompatibilityState.Unknown;
        }

        var loader = instanceLoader.Trim().ToLowerInvariant();
        return metadata.Platform switch
        {
            ModPlatform.Fabric when loader == "fabric" => CompatibilityState.Compatible,
            ModPlatform.Fabric => CompatibilityState.Incompatible,
            ModPlatform.Forge when loader is "forge" or "cleanroom" => CompatibilityState.Compatible,
            ModPlatform.LegacyForge when loader is "forge" or "cleanroom" => CompatibilityState.Compatible,
            ModPlatform.NeoForge when loader == "neoforge" => CompatibilityState.Compatible,
            ModPlatform.Forge or ModPlatform.LegacyForge or ModPlatform.NeoForge => CompatibilityState.Incompatible,
            _ => CompatibilityState.Unknown,
        };
    }

    public static IReadOnlyList<ModDependencyInfo> EvaluateDependencies(
        ModMetadata metadata,
        IReadOnlyDictionary<string, string> availableVersions)
    {
        var results = new List<ModDependencyInfo>();
        foreach (var dependency in metadata.Dependencies)
        {
            var id = dependency.ModId;
            if (string.Equals(id, "minecraft", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "java", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetVersion(availableVersions, id, out var actualVersion))
            {
                var matches = ModVersionRange.Matches(actualVersion, dependency.VersionRange);
                results.Add(new ModDependencyInfo(
                    id,
                    dependency.Mandatory,
                    dependency.VersionRange,
                    matches switch
                    {
                        true => DependencyState.Satisfied,
                        false => DependencyState.Missing,
                        _ => DependencyState.Unknown,
                    },
                    matches switch
                    {
                        true => null,
                        false => $"已安装 {actualVersion}，不满足 {dependency.VersionRange}",
                        _ => "无法解析版本范围。",
                    }));
                continue;
            }

            if (!dependency.Mandatory)
            {
                results.Add(new ModDependencyInfo(
                    id,
                    Mandatory: false,
                    dependency.VersionRange,
                    DependencyState.Satisfied,
                    "可选依赖未安装，不构成缺失。"));
                continue;
            }

            results.Add(new ModDependencyInfo(
                id,
                Mandatory: true,
                dependency.VersionRange,
                DependencyState.Missing,
                "缺少必需依赖。"));
        }

        return results;
    }

    private static bool TryGetVersion(
        IReadOnlyDictionary<string, string> availableVersions,
        string id,
        out string version)
    {
        if (availableVersions.TryGetValue(id, out version!))
        {
            return true;
        }

        version = string.Empty;
        return false;
    }
}
