// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace Ecl.Deployment;

/// <summary>
/// 部署版本号。采用语义化版本的核心子集：major.minor.patch[-prerelease]，忽略 build metadata。
/// 版本号同时用于目录名，因此解析成功即保证不含路径分隔符。
/// </summary>
public sealed partial record ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, string? preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public string? PreRelease { get; }

    public bool IsPreRelease => PreRelease is not null;

    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = VersionRegex().Match(value.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var preRelease = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;
        version = new ReleaseVersion(major, minor, patch, preRelease);
        return true;
    }

    public static ReleaseVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException($"无效的版本号：{value}");

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var numeric = Major.CompareTo(other.Major);
        if (numeric != 0)
        {
            return numeric;
        }

        numeric = Minor.CompareTo(other.Minor);
        if (numeric != 0)
        {
            return numeric;
        }

        numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0)
        {
            return numeric;
        }

        if (PreRelease is null)
        {
            return other.PreRelease is null ? 0 : 1;
        }

        if (other.PreRelease is null)
        {
            return -1;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    public override string ToString() =>
        PreRelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    private static int ComparePreRelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            var leftPart = leftParts[index];
            var rightPart = rightParts[index];
            var leftNumeric = int.TryParse(leftPart, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(rightPart, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            if (leftNumeric && rightNumeric)
            {
                var numeric = leftNumber.CompareTo(rightNumber);
                if (numeric != 0)
                {
                    return numeric;
                }
            }
            else if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }
            else
            {
                var text = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
                if (text != 0)
                {
                    return text;
                }
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    [GeneratedRegex(
        "^(?<major>0|[1-9]\\d*)\\.(?<minor>0|[1-9]\\d*)\\.(?<patch>0|[1-9]\\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
