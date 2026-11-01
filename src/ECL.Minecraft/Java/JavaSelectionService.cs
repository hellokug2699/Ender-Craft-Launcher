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
    bool IsCompatibilityWarningAcknowledged = false,
    string? CompatibilityAcknowledgementToken = null,
    JavaSelectionDecision? Decision = null,
    JavaLaunchPlan? Plan = null,
    bool IsBlocked = false,
    string? BlockReason = null);

public interface IJavaSelectionService
{
    Task<JavaSelectionResult> ResolveAsync(
        GameInstance instance,
        MinecraftVersion? version,
        CancellationToken cancellationToken = default);

    Task<EffectiveJavaRequirements> ResolveRequirementsAsync(
        GameInstance instance,
        MinecraftVersion? version,
        CancellationToken cancellationToken = default);
}

public sealed class JavaSelectionService(
    ISettingsService settings,
    IJavaLocator locator,
    ILocalizer localizer,
    IJavaRequirementProvider? requirementProvider = null) : IJavaSelectionService
{
    public async Task<JavaSelectionResult> ResolveAsync(
        GameInstance instance,
        MinecraftVersion? version,
        CancellationToken cancellationToken = default)
    {
        var requirements = await ResolveRequirementsAsync(instance, version, cancellationToken)
            .ConfigureAwait(false);

        var instancePath = settings.Get<string?>("instance.java.executablePath", instance.Id);
        var globalPaths = JavaSearchPath.Split(settings.Get<string>("java.defaultPath"));

        var warnings = new List<string>();
        JavaRuntime? explicitRuntime = null;
        string? explicitPath = null;

        // 实例级路径是显式 pin：用户明确选择后，即使版本不是推荐版本也保留选择，启动前给出风险确认。
        if (!string.IsNullOrWhiteSpace(instancePath))
        {
            var path = instancePath.Trim().Trim('"');
            explicitPath = path;
            explicitRuntime = locator.FromPath(path);
            if (explicitRuntime is null)
            {
                warnings.Add(localizer.Get("launch.java.configuredInvalid", path));
            }
        }

        var discovered = await locator.FindAllAsync(cancellationToken).ConfigureAwait(false);

        // 配置的全局路径是显式用户选择，不只是发现缓存提示。直接解析它，
        // 使在早期扫描之后新增的运行时也仍然可选。
        var configuredRuntimes = globalPaths
            .Select(path => ResolveConfiguredRuntime(locator, path))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .Concat(discovered)
            .GroupBy(candidate => candidate.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var priority = JavaPriorityOrdering.Order(configuredRuntimes, globalPaths);
        var decision = JavaSelectionPlanner.Select(
            priority.Runtimes,
            requirements,
            explicitRuntime,
            priority.ConfiguredCount);

        var compatibility = requirements.Evaluate(decision.Runtime);
        var acknowledgementToken = decision.RequiresRiskAcknowledgement
            ? decision.RiskIdentity
            : null;
        var savedAcknowledgement = settings.Get<string?>(JavaRiskIdentity.SettingsKey);
        var isCompatibilityWarningAcknowledged = acknowledgementToken is not null &&
                                                  string.Equals(
                                                      savedAcknowledgement,
                                                      acknowledgementToken,
                                                      StringComparison.Ordinal);

        var instanceMemory = settings.Get<int?>("instance.java.memoryMb", instance.Id);
        var globalMemory = settings.Get<int>("java.defaultMemoryMb");
        var memoryMb = Math.Clamp(instanceMemory ?? globalMemory, 1024, 65536);

        foreach (var issue in JavaPlatformAdvisor.Assess(decision.Runtime, memoryMb))
        {
            warnings.Add(DescribePlatformIssue(issue));
        }

        if (decision.Runtime is { ResolvedBitness: JavaBitness.Unknown } unknownArchitecture)
        {
            warnings.Add(localizer.Get(
                "launch.java.unknownArch",
                unknownArchitecture.DisplayName));
        }

        var presetValue = settings.Get<string?>("instance.java.jvmPreset", instance.Id)
                          ?? settings.Get<string>("java.jvmPreset");
        if (JvmPresets.TryParse(presetValue, out var preset) &&
            preset == JvmPreset.Zgc &&
            decision.Runtime?.Version is { Major: < 17 })
        {
            warnings.Add(localizer.Get("launch.java.issue.zgcOld", decision.Runtime.Version.Raw));
        }

        var plan = JavaLaunchPlan.From(decision, compatibility, isCompatibilityWarningAcknowledged);

        return new JavaSelectionResult(
            decision.Runtime,
            requirements.ToRequirement(),
            true,
            compatibility,
            discovered.Count > 0 || configuredRuntimes.Length > 0 || decision.Runtime is not null,
            explicitPath,
            decision.IsExplicitOverride,
            warnings,
            memoryMb,
            isCompatibilityWarningAcknowledged,
            acknowledgementToken,
            decision,
            plan,
            decision.IsBlocked,
            decision.IsBlocked ? decision.Explanation : null);
    }

    public async Task<EffectiveJavaRequirements> ResolveRequirementsAsync(
        GameInstance instance,
        MinecraftVersion? version,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JavaRequirementSource> extraSources = [];
        if (requirementProvider is not null)
        {
            try
            {
                extraSources = await requirementProvider
                    .GetRequirementsAsync(instance, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 单个要求 Provider 失败不能让整个实例分析失败；Minecraft 基线仍然可用。
                extraSources = [];
            }
        }

        return JavaRequirementAggregation.Resolve(instance, version, extraSources);
    }

    private static JavaRuntime? ResolveConfiguredRuntime(IJavaLocator locator, string configuredPath)
    {
        var path = configuredPath.Trim().Trim('"');
        if (File.Exists(path))
        {
            return locator.FromPath(path);
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        // A configured parent directory may contain several Java installations.
        // FromPath intentionally returns one runtime for such a directory, which
        // would collapse the candidates and can hide an older compatible runtime.
        // Only resolve directories that are themselves a Java home or its bin folder.
        var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var directExecutable = Path.Combine(path, "bin", executableName);
        if (File.Exists(directExecutable))
        {
            return locator.FromPath(path);
        }

        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.Equals(directoryName, "bin", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(path, executableName)))
        {
            return locator.FromPath(path);
        }

        return null;
    }

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
