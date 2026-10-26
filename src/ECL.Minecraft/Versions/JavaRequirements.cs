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
        var declaredMajor = version.JavaVersion?.MajorVersion ?? 0;

        // 版本 JSON 是唯一权威来源；它已经覆盖 1.x、26.x 以及未来版本号格式。
        if (declaredMajor > 0)
        {
            return new JavaRequirement(declaredMajor, component);
        }

        // 只有旧版本 JSON 缺少 javaVersion 时才需要推断。
        return new JavaRequirement(InferMinimumMajor(gameVersion), component);
    }

    private static int InferMinimumMajor(string gameVersion)
    {
        var legacy = ModernVersionRegex().Match(gameVersion);
        if (legacy.Success)
        {
            var minor = int.Parse(legacy.Groups[1].Value, CultureInfo.InvariantCulture);
            var patch = legacy.Groups[2].Success
                ? int.Parse(legacy.Groups[2].Value, CultureInfo.InvariantCulture)
                : 0;

            if (minor < 17)
            {
                return 8;
            }

            if (minor == 17)
            {
                return 16;
            }

            if (minor < 20 || (minor == 20 && patch < 5))
            {
                return 17;
            }

            return 21;
        }

        var snapshot = SnapshotRegex().Match(gameVersion);
        if (snapshot.Success && int.TryParse(snapshot.Groups[1].Value, out var year))
        {
            return year >= 26 ? 25 : year >= 24 ? 21 : year >= 17 ? 17 : 8;
        }

        var yearBased = YearVersionRegex().Match(gameVersion);
        if (yearBased.Success &&
            int.TryParse(yearBased.Groups[1].Value, out var major))
        {
            // 2026 年起 Mojang 使用 26.x 版本号；26.x 当前需要 Java 25。
            return major >= 26 ? 25 : 21;
        }

        // 无法识别的自定义版本：版本 JSON 通常已提供 javaVersion，这里保守回退到 21。
        return 21;
    }

    [GeneratedRegex(@"^1\.(\d+)(?:\.(\d+))?")]
    private static partial Regex ModernVersionRegex();

    [GeneratedRegex(@"^(\d+)\.(\d+)")]
    private static partial Regex YearVersionRegex();

    [GeneratedRegex(@"^(\d{2})w\d{2}[a-z]$", RegexOptions.IgnoreCase)]
    private static partial Regex SnapshotRegex();
}
