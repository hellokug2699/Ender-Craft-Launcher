// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

/// <summary>
/// 一个已经合并的有效 Java 版本要求。ECL 默认只检查最低主版本，不限制最高版本；
/// MaximumMajor 为将来的明确上限约束保留，冲突由 <see cref="EffectiveJavaRequirements"/> 负责。
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

    public string Describe()
    {
        if (MaximumMajor is not { } maximum)
        {
            return $"Java {MinimumMajor}+";
        }

        return MinimumMajor == maximum
            ? $"Java {MinimumMajor}"
            : $"Java {MinimumMajor}–{maximum}";
    }
}

public enum JavaCompatibilityStatus
{
    Compatible,
    TooOld,
    TooNew,
    UnsupportedArchitecture,
    Unknown,
    UnknownRequirement,
    RequirementsConflict,
}

public sealed record JavaCompatibilityResult(
    JavaCompatibilityStatus Status,
    JavaRequirement Requirement,
    JavaVersion? JavaVersion,
    EffectiveJavaRequirements? EffectiveRequirements = null)
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
                runtime is { IsKnown32Bit: true }
                    ? JavaCompatibilityStatus.UnsupportedArchitecture
                    : JavaCompatibilityStatus.Compatible,
                new JavaRequirement(0),
                runtime?.Version);
        }

        if (runtime is { IsKnown32Bit: true })
        {
            return new JavaCompatibilityResult(
                JavaCompatibilityStatus.UnsupportedArchitecture,
                requirement,
                runtime.Version);
        }

        if (runtime?.Version is null)
        {
            return new JavaCompatibilityResult(JavaCompatibilityStatus.Unknown, requirement, null);
        }

        if (runtime.Version.Major < requirement.MinimumMajor)
        {
            return new JavaCompatibilityResult(JavaCompatibilityStatus.TooOld, requirement, runtime.Version);
        }

        if (requirement.MaximumMajor is { } maximum && runtime.Version.Major > maximum)
        {
            return new JavaCompatibilityResult(JavaCompatibilityStatus.TooNew, requirement, runtime.Version);
        }

        return new JavaCompatibilityResult(JavaCompatibilityStatus.Compatible, requirement, runtime.Version);
    }
}

public static class JavaSelector
{
    /// <summary>
    /// 按调用方提供的优先级选择第一个兼容运行时。
    /// 全局 Java 设置因此表达“优先级”，而不是“强制指定某个版本”。
    /// </summary>
    public static JavaRuntime? Select(IReadOnlyList<JavaRuntime> runtimes, JavaRequirement? requirement) =>
        JavaSelectionPlanner
            .Select(
                runtimes,
                requirement is null ? null : EffectiveJavaRequirements.FromRequirement(requirement),
                configuredPriorityCount: 0)
            .Runtime;
}
