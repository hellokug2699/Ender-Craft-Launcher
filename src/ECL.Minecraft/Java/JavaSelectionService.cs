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
    IReadOnlyList<string> Warnings,
    int MemoryMb);

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
    private static readonly string JavaExecutableName = OperatingSystem.IsWindows() ? "java.exe" : "java";

    public async Task<JavaSelectionResult> ResolveAsync(
        GameInstance instance,
        MinecraftVersion? version,
        CancellationToken cancellationToken = default)
    {
        var requirement = version is null
            ? null
            : JavaRequirements.Resolve(version, instance.GameVersion);

        var configuredPaths = new List<string?>
        {
            settings.Get<string?>("instance.java.executablePath", instance.Id),
        };
        configuredPaths.AddRange(JavaSearchPath.Split(settings.Get<string>("java.defaultPath")));

        var warnings = new List<string>();
        JavaRuntime? runtime = null;
        string? explicitPath = null;

        foreach (var configuredPath in configuredPaths)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                continue;
            }

            var path = configuredPath.Trim().Trim('"');

            // 目录本身不是 Java 主目录时，通常表示“包含多个 JDK 的父目录”。
            // 这种路径交给 JavaLocator.FindAllAsync 展开，避免直接锁定其中版本最高的一个。
            if (Directory.Exists(path) &&
                !File.Exists(Path.Combine(path, "bin", JavaExecutableName)))
            {
                continue;
            }

            explicitPath ??= path;
            runtime = locator.FromPath(path);
            if (runtime is not null)
            {
                break;
            }

            warnings.Add(localizer.Get("launch.java.configuredInvalid", path));
        }

        var runtimes = await locator.FindAllAsync(cancellationToken).ConfigureAwait(false);

        if (runtime is null)
        {
            runtime = JavaSelector.Select(runtimes, requirement)
                      ?? runtimes.OrderByDescending(item => item.Version).FirstOrDefault();
        }

        var checkEnabled = !settings.Get<bool>("instance.java.disableCompatibilityCheck", instance.Id);
        var compatibility = requirement is null
            ? null
            : JavaCompatibility.Evaluate(requirement, runtime);

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
            checkEnabled,
            compatibility,
            runtimes.Count > 0,
            explicitPath,
            warnings,
            memoryMb);
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
