// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Versions;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class ExtractNativesStep(
    IAppPaths paths,
    ILocalizer localizer,
    ILogger<ExtractNativesStep> logger) : ILaunchStep
{
    public string Id => "natives";

    public int Order => 360;

    public Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        if (context.DryRun)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Skipped,
                localizer.Get("launch.natives.dryRun")));
        }

        if (context.Version is null)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.natives.noVersion")));
        }

        try
        {
            var plan = PlatformLibrarySelector.Plan(
                context.Version.Libraries,
                LibraryRules.CurrentOsName,
                LibraryRules.CurrentArch);

            var nativeCount = plan.Count(item => item.NativeArtifact is not null);
            if (nativeCount == 0)
            {
                return Task.FromResult(new LaunchStepResult(
                    Id,
                    LaunchStepStatus.Success,
                    localizer.Get("launch.natives.none")));
            }

            var extracted = NativesExtractor.Extract(plan, paths, context.NativesDirectory);
            logger.LogInformation("已解压 {Count} 个 natives 文件到 {Directory}。", extracted, context.NativesDirectory);

            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Success,
                localizer.Get("launch.natives.extracted", extracted)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(exception, "解压 natives 失败。");
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.natives.failed", exception.Message)));
        }
    }
}
