// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

/// <summary>
/// 一个 Minecraft 版本对 Java 的最低要求。MaximumMajor 用于旧版本：例如 1.16 及以下通常只适合 Java 8。
/// </summary>
public sealed record JavaRequirement(
    int MinimumMajor,
    string? Component = null,
    int? MaximumMajor = null)
{
    public bool IsCompatible(JavaVersion? version) =>
        version is not null &&
        version.Major >= MinimumMajor &&
        (MaximumMajor is null || version.Major <= MaximumMajor);

    public string Describe() => MaximumMajor switch
    {
        int maximum when maximum == MinimumMajor => $"Java {MinimumMajor}",
        int maximum => $"Java {MinimumMajor}-{maximum}",
        _ => $"Java {MinimumMajor}+",
    };
}

public enum JavaCompatibilityStatus
{
    Compatible,
    TooOld,
    TooNew,
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

        if (runtime.Version.Major < requirement.MinimumMajor)
        {
            return new JavaCompatibilityResult(JavaCompatibilityStatus.TooOld, requirement, runtime.Version);
        }

        if (requirement.MaximumMajor is int maximum && runtime.Version.Major > maximum)
        {
            return new JavaCompatibilityResult(JavaCompatibilityStatus.TooNew, requirement, runtime.Version);
        }

        return new JavaCompatibilityResult(JavaCompatibilityStatus.Compatible, requirement, runtime.Version);
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

        return compatible
            .OrderBy(runtime => runtime.Version!.Major == requirement.MinimumMajor ? 0 : 1)
            .ThenBy(runtime => runtime.Version!.Major)
            .ThenByDescending(runtime => runtime.Version!.Minor)
            .ThenByDescending(runtime => runtime.Version!.Patch)
            .First();
    }
}
