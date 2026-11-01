// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Java;

public sealed record JavaSelectionResult(
    JavaRuntime? Runtime,
    JavaRequirement? Requirement,
    bool CompatibilityCheckEnabled,
    JavaCompatibilityResult? Compatibility,
    bool HasAnyDetectedJava,
    string? ExplicitPath,
    bool IsExplicitOverride,
    IReadOnlyList<string> Warnings,
    int MemoryMb,
    bool IsCompatibilityWarningAcknowledged = false);

public static class JavaCompatibilityAcknowledgement
{
    public static string Create(JavaRuntime runtime, JavaRequirement requirement)
    {
        var actual = runtime.Version?.Raw ?? runtime.DisplayName;
        var component = requirement.Component ?? string.Empty;
        return $"{runtime.ExecutablePath}|{actual}|{requirement.MinimumMajor}|{component}";
    }
}

public interface IJavaSelectionService
{
    Task<JavaSelectionResult> ResolveAsync(
        GameInstance instance,
        MinecraftVersion? version,
        CancellationToken cancellationToken = default);
}

public sealed class JavaSelectionService(
    ISettingsService settings,
    IJavaLocator locator,
    ILocalizer localizer) : IJavaSelectionService
{
    private const string JavaCompatibilityAcknowledgementKey = "launcher.java.incompatiblePromptAcknowledged";

    public async Task<JavaSelectionResult> ResolveAsync(
        GameInstance instance,
        MinecraftVersion? version,
        CancellationToken cancellationToken = default)
    {
        var requirement = version is null
            ? null
            : JavaRequirements.Resolve(version, instance.GameVersion);

        var instancePath = settings.Get<string?>("instance.java.executablePath", instance.Id);
        var globalPaths = JavaSearchPath.Split(settings.Get<string>("java.defaultPath"));

        var warnings = new List<string>();
        JavaRuntime? runtime = null;
        string? explicitPath = null;
        var isExplicitOverride = false;

        // 实例级路径是显式 pin：用户明确选择后，即使版本不是推荐版本也保留选择，启动前给出一次风险确认。
        if (!string.IsNullOrWhiteSpace(instancePath))
        {
            var path = instancePath.Trim().Trim('"');
            explicitPath = path;
            isExplicitOverride = true;
            runtime = locator.FromPath(path);
            if (runtime is null)
            {
                warnings.Add(localizer.Get("launch.java.configuredInvalid", path));
                isExplicitOverride = false;
            }
        }

        var discovered = await locator.FindAllAsync(cancellationToken).ConfigureAwait(false);
        var runtimes = OrderByGlobalPriority(discovered, globalPaths);

        if (runtime is null)
        {
            runtime = JavaSelector.Select(runtimes, requirement)
                      ?? (requirement is null ? runtimes.FirstOrDefault() : null);
        }

        // 兼容性检查仍然始终执行；实例显式 pin 只是允许用户承担风险，不会关闭检查。
        var compatibility = requirement is null
            ? null
            : JavaCompatibility.Evaluate(requirement, runtime);

        var warningMessage = compatibility is { IsCompatible: false } &&
                              requirement is not null &&
                              runtime is not null &&
                              isExplicitOverride
            ? localizer.Get(
                "instances.java.incompatiblePrompt",
                runtime.Version?.Raw ?? runtime.DisplayName,
                requirement.Describe())
            : null;
        var acknowledgement = settings.Get<string?>(JavaCompatibilityAcknowledgementKey);
        var isCompatibilityWarningAcknowledged = warningMessage is not null &&
                                                  string.Equals(
                                                      acknowledgement,
                                                      warningMessage,
                                                      StringComparison.Ordinal);

        var instanceMemory = settings.Get<int?>("instance.java.memoryMb", instance.Id);
        var globalMemory = settings.Get<int>("java.defaultMemoryMb");
        var memoryMb = Math.Clamp(instanceMemory ?? globalMemory, 1024, 65536);

        foreach (var issue in JavaPlatformAdvisor.Assess(runtime, memoryMb))
        {
            warnings.Add(DescribePlatformIssue(issue));
        }

        var presetValue = settings.Get<string?>("instance.java.jvmPreset", instance.Id)
                          ?? settings.Get<string>("java.jvmPreset");
        if (JvmPresets.TryParse(presetValue, out var preset) &&
            preset == JvmPreset.Zgc &&
            runtime?.Version is { Major: < 17 })
        {
            warnings.Add(localizer.Get("launch.java.issue.zgcOld", runtime.Version.Raw));
        }

        return new JavaSelectionResult(
            runtime,
            requirement,
            true,
            compatibility,
            discovered.Count > 0,
            explicitPath,
            isExplicitOverride,
            warnings,
            memoryMb,
            isCompatibilityWarningAcknowledged);
    }

    private static IReadOnlyList<JavaRuntime> OrderByGlobalPriority(
        IReadOnlyList<JavaRuntime> runtimes,
        IReadOnlyList<string> configuredPaths)
    {
        if (runtimes.Count <= 1 || configuredPaths.Count == 0)
        {
            return runtimes;
        }

        var ordered = new List<JavaRuntime>(runtimes.Count);
        var remaining = runtimes.ToList();

        foreach (var configuredPath in configuredPaths)
        {
            var path = configuredPath.Trim().Trim('"');
            if (path.Length == 0)
            {
                continue;
            }

            for (var index = remaining.Count - 1; index >= 0; index--)
            {
                var runtime = remaining[index];
                if (!MatchesConfiguredPath(runtime, path))
                {
                    continue;
                }

                ordered.Add(runtime);
                remaining.RemoveAt(index);
            }
        }

        ordered.AddRange(remaining);
        return ordered;
    }

    private static bool MatchesConfiguredPath(JavaRuntime runtime, string configuredPath)
    {
        var fullConfigured = NormalizePath(configuredPath);
        var executable = NormalizePath(runtime.ExecutablePath);
        var home = NormalizePath(runtime.HomePath);

        if (string.Equals(fullConfigured, executable, PathComparison) ||
            string.Equals(fullConfigured, home, PathComparison))
        {
            return true;
        }

        if (!Directory.Exists(configuredPath))
        {
            return false;
        }

        var root = fullConfigured.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return executable.StartsWith(root, PathComparison) || home.StartsWith(root, PathComparison);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception) when (true)
        {
            return path;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private string DescribePlatformIssue(JavaPlatformIssue issue) => issue.Kind switch
    {
        JavaPlatformIssueKind.Java32BitOn64BitOs => localizer.Get("launch.java.issue.java32On64"),
        JavaPlatformIssueKind.Java64BitOn32BitOs => localizer.Get("launch.java.issue.java64On32"),
        JavaPlatformIssueKind.X64JavaOnArm64Os => localizer.Get("launch.java.issue.x64OnArm64"),
        JavaPlatformIssueKind.MemoryTooHighFor32BitJava => localizer.Get(
            "launch.java.issue.memory32Bit",
            issue.MemoryMb ?? 0,
            issue.LimitMb ?? 1024),
        JavaPlatformIssueKind.MemoryExceedsPhysical => localizer.Get(
            "launch.java.issue.memoryPhysical",
            issue.MemoryMb ?? 0,
            issue.LimitMb ?? 0),
        _ => string.Empty,
    };
}
