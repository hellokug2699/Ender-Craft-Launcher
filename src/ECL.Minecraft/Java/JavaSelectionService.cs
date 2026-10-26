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
    IReadOnlyList<string> Warnings);

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
    public async Task<JavaSelectionResult> ResolveAsync(
        GameInstance instance,
        MinecraftVersion? version,
        CancellationToken cancellationToken = default)
    {
        var requirement = version is null
            ? null
            : JavaRequirements.Resolve(version, instance.GameVersion);

        var configuredPaths = new[]
        {
            settings.Get<string?>("instance.java.executablePath", instance.Id),
            settings.Get<string>("java.defaultPath"),
        };

        var warnings = new List<string>();
        JavaRuntime? runtime = null;
        string? explicitPath = null;

        foreach (var configuredPath in configuredPaths)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                continue;
            }

            explicitPath ??= configuredPath;
            runtime = locator.FromPath(configuredPath);
            if (runtime is not null)
            {
                break;
            }

            warnings.Add(localizer.Get("launch.java.configuredInvalid", configuredPath));
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

        return new JavaSelectionResult(
            runtime,
            requirement,
            checkEnabled,
            compatibility,
            runtimes.Count > 0,
            explicitPath,
            warnings);
    }
}
