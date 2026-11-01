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
    /// <summary>
    /// 按调用方提供的优先级选择第一个兼容运行时。
    /// 全局 Java 设置因此表达“优先级”，而不是“强制指定某个版本”。
    /// </summary>
    public static JavaRuntime? Select(IReadOnlyList<JavaRuntime> runtimes, JavaRequirement? requirement)
    {
        if (runtimes.Count == 0)
        {
            return null;
        }

        if (requirement is null)
        {
            return runtimes[0];
        }

        foreach (var runtime in runtimes)
        {
            if (requirement.IsCompatible(runtime.Version))
            {
                return runtime;
            }
        }

        return null;
    }
}
