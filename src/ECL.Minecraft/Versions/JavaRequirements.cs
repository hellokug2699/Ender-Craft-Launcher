// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using Ecl.Java;

namespace Ecl.Minecraft.Versions;

public static partial class JavaRequirements
{
    public static JavaRequirement Resolve(MinecraftVersion version, string gameVersion)
    {
        var component = version.JavaVersion?.Component;
        var major = version.JavaVersion?.MajorVersion ?? 0;

        if (major > 0)
        {
            return new JavaRequirement(major, component, major <= 8 ? 8 : null);
        }

        var (minimum, maximum) = InferFromVersionId(gameVersion);
        return new JavaRequirement(minimum, component, maximum);
    }

    private static (int Minimum, int? Maximum) InferFromVersionId(string gameVersion)
    {
        var modern = ModernVersionRegex().Match(gameVersion);
        if (modern.Success)
        {
            var minor = int.Parse(modern.Groups[1].Value, CultureInfo.InvariantCulture);
            var patch = modern.Groups[2].Success
                ? int.Parse(modern.Groups[2].Value, CultureInfo.InvariantCulture)
                : 0;

            if (minor < 17)
            {
                return (8, 8);
            }

            if (minor == 17)
            {
                return (16, null);
            }

            if (minor < 20 || (minor == 20 && patch < 5))
            {
                return (17, null);
            }

            return (21, null);
        }

        var snapshot = SnapshotRegex().Match(gameVersion);
        if (snapshot.Success && int.TryParse(snapshot.Groups[1].Value, out var year))
        {
            return year >= 24 ? (21, null) : year >= 17 ? (17, null) : (8, 8);
        }

        return (21, null);
    }

    [GeneratedRegex(@"^1\.(\d+)(?:\.(\d+))?")]
    private static partial Regex ModernVersionRegex();

    [GeneratedRegex(@"^(\d{2})w\d{2}[a-z]$", RegexOptions.IgnoreCase)]
    private static partial Regex SnapshotRegex();
}
