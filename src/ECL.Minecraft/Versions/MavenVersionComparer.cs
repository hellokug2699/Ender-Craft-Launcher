// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Versions;

/// <summary>
/// Maven 版本比较：用于「同一构件出现多个版本时保留最高版本」。
///
/// 只做启动/安装需要的确定性比较，不追求 Maven 完整语义：
/// - 按 <c>.</c>、<c>-</c>、<c>_</c>、<c>+</c> 切段逐段比较；
/// - 两段都是数字时按数值比较（<c>2.26.0 &gt; 2.8.1</c>，字符串比较会得到相反结果）；
/// - 数字段高于文字段（<c>1.0.1 &gt; 1.0.rc</c>）；
/// - 一方结束时，若另一方剩余段是预发布限定词（SNAPSHOT/RC/alpha…），则已结束的一方更高。
/// </summary>
public static class MavenVersionComparer
{
    private static readonly HashSet<string> Qualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "snapshot",
        "rc",
        "cr",
        "alpha",
        "beta",
        "pre",
        "preview",
        "milestone",
        "dev",
        "canary",
        "ea",
        "m",
    };

    public static int Compare(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return string.IsNullOrWhiteSpace(right) ? 0 : -1;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return 1;
        }

        return CompareTokens(Tokenize(left), Tokenize(right));
    }

    private static int CompareTokens(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var length = Math.Max(left.Count, right.Count);
        for (var index = 0; index < length; index++)
        {
            var hasLeft = index < left.Count;
            var hasRight = index < right.Count;
            if (!hasLeft || !hasRight)
            {
                var remaining = hasLeft ? left[index] : right[index];
                var remainingIsHigher = hasLeft ? 1 : -1;
                return IsQualifier(remaining) ? -remainingIsHigher : remainingIsHigher;
            }

            var leftIsNumber = long.TryParse(left[index], out var leftNumber);
            var rightIsNumber = long.TryParse(right[index], out var rightNumber);
            if (leftIsNumber && rightIsNumber)
            {
                if (leftNumber != rightNumber)
                {
                    return leftNumber.CompareTo(rightNumber);
                }

                continue;
            }

            if (leftIsNumber != rightIsNumber)
            {
                return leftIsNumber ? 1 : -1;
            }

            var comparison = string.Compare(left[index], right[index], StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
            {
                return comparison > 0 ? 1 : -1;
            }
        }

        return 0;
    }

    private static bool IsQualifier(string token) => Qualifiers.Contains(token);

    private static List<string> Tokenize(string version)
    {
        var tokens = new List<string>();
        foreach (var segment in version.Trim().Split(['.', '-', '_', '+'], StringSplitOptions.RemoveEmptyEntries))
        {
            // 1.0.0-beta.2 / 1.0.0rc1 这类写法把限定词与数字拆开，便于逐段比较。
            var letters = 0;
            while (letters < segment.Length && !char.IsDigit(segment[letters]))
            {
                letters++;
            }

            if (letters > 0 && letters < segment.Length && IsQualifier(segment[..letters]))
            {
                tokens.Add(segment[..letters]);
                tokens.Add(segment[letters..]);
                continue;
            }

            tokens.Add(segment);
        }

        return tokens;
    }
}
