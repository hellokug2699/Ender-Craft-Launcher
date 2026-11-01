// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Java;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class ResolveJavaStep(IJavaSelectionService selection, ILocalizer localizer) : ILaunchStep
{
    public string Id => "java";

    public int Order => 200;

    public async Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        var result = await selection
            .ResolveAsync(context.Instance, context.Version, cancellationToken)
            .ConfigureAwait(false);

        context.MemoryMb = Math.Clamp(result.MemoryMb, 1024, 65536);

        if (result.Runtime is null)
        {
            context.JavaExecutable = "java";
            var missingMessage = localizer.Get(
                context.DryRun ? "launch.java.missing.preview" : "launch.java.missing");
            missingMessage = AppendWarnings(missingMessage, result.Warnings);

            return new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                missingMessage);
        }

        context.Java = result.Runtime;
        context.JavaExecutable = result.Runtime.ExecutablePath;

        if (result.Compatibility is { Status: JavaCompatibilityStatus.Unknown })
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Warning,
                AppendWarnings(
                    localizer.Get("launch.java.unknownVersion", result.Runtime.DisplayName),
                    result.Warnings));
        }

        if (result.Compatibility is { IsCompatible: false })
        {
            var required = result.Requirement?.Describe() ?? "Java";
            var message = localizer.Get(
                "launch.java.incompatible",
                result.Runtime.Version?.Raw ?? result.Runtime.DisplayName,
                required);

            // Java 兼容性是启动前置条件，不再允许通过“忽略警告”继续启动。
            return new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                AppendWarnings(message, result.Warnings));
        }

        var foundMessage = localizer.Get("launch.java.found", result.Runtime.DisplayName, context.MemoryMb);
        return new LaunchStepResult(
            Id,
            result.Warnings.Count > 0 ? LaunchStepStatus.Warning : LaunchStepStatus.Success,
            AppendWarnings(foundMessage, result.Warnings));
    }

    private static string AppendWarnings(string message, IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? message : $"{message} {string.Join(' ', warnings)}";
}
