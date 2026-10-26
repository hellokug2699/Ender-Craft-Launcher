// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class ValidateGameFilesStep(IAppPaths paths, ILocalizer localizer) : ILaunchStep
{
    public string Id => "files";

    public int Order => 350;

    public Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        if (context.Version is null)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                localizer.Get("launch.files.noVersion")));
        }

        var requiredFiles = VersionFilePlanner.GetRequiredFiles(context.Version, paths, context.Instance.VersionId);
        var missing = requiredFiles.Where(path => !File.Exists(path)).ToArray();

        if (missing.Length == 0)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Success,
                localizer.Get("launch.files.ok", requiredFiles.Count)));
        }

        if (context.DryRun)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Warning,
                localizer.Get("launch.files.missing.preview", missing.Length, missing[0])));
        }

        return Task.FromResult(new LaunchStepResult(
            Id,
            LaunchStepStatus.Failed,
            localizer.Get("launch.files.missing", missing.Length, missing[0])));
    }
}
