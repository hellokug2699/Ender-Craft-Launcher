// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

/// <summary>
/// 一个 Minecraft 版本对 Java 的最低要求。ECL 只检查最低主版本，不限制最高版本：
/// 较新的 Java 通常可以运行较新的游戏，也允许用户自行承担更高版本的风险。
/// </summary>
public sealed record JavaRequirement(
    int MinimumMajor,
    string? Component = null)
{
    public bool IsCompatible(JavaVersion? version) =>
        version is not null && version.Major >= MinimumMajor;

    public string Describe() => $"Java {MinimumMajor}+";
}

public enum JavaCompatibilityStatus
{
    Compatible,
    TooOld,
    Unknown,
}

public sealed record JavaCompatibilityResult(
    JavaCompatibilityStatus Status,
    JavaRequirement Requirement,
    JavaVersion? JavaVersion)
{
    public bool IsCompatible => Status == JavaCompatibilityStatus.Compatible;
}

public static class JavaCompatibility
{
    public static JavaCompatibilityResult Evaluate(JavaRequirement? requirement, JavaRuntime? runtime)
    {
        if (requirement is null)
        {
            return new JavaCompatibilityResult(
                JavaCompatibilityStatus.Compatible,
                new JavaRequirement(0),
                runtime?.Version);
        }

        if (runtime?.Version is null)
        {
            return new JavaCompatibilityResult(JavaCompatibilityStatus.Unknown, requirement, null);
        }

        return runtime.Version.Major < requirement.MinimumMajor
            ? new JavaCompatibilityResult(JavaCompatibilityStatus.TooOld, requirement, runtime.Version)
            : new JavaCompatibilityResult(JavaCompatibilityStatus.Compatible, requirement, runtime.Version);
    }
}

public static class JavaSelector
{
    public static JavaRuntime? Select(IReadOnlyList<JavaRuntime> runtimes, JavaRequirement? requirement)
    {
        if (runtimes.Count == 0)
        {
            return null;
        }

        if (requirement is null)
        {
            return runtimes
                .OrderByDescending(runtime => runtime.Version)
                .FirstOrDefault();
        }

        var compatible = runtimes
            .Where(runtime => requirement.IsCompatible(runtime.Version))
            .ToArray();

        if (compatible.Length == 0)
        {
            return null;
        }

        // 优先选择与最低要求相同的主版本，避免不必要的“越新越好”。
        return compatible
            .OrderBy(runtime => runtime.Version!.Major == requirement.MinimumMajor ? 0 : 1)
            .ThenBy(runtime => runtime.Version!.Major)
            .ThenByDescending(runtime => runtime.Version!.Minor)
            .ThenByDescending(runtime => runtime.Version!.Patch)
            .First();
    }
}
