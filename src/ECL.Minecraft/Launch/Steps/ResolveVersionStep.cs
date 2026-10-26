// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class ResolveVersionStep(IVersionResolver versionResolver, ILocalizer localizer) : ILaunchStep
{
    public string Id => "version";

    public int Order => 300;

    public async Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        var result = await versionResolver.ResolveAsync(context.Instance.VersionId, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var message = localizer.Get(
                context.DryRun ? "launch.version.missing.preview" : "launch.version.missing",
                context.Instance.VersionId);

            return new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                message);
        }

        context.Version = result.Value;

        if (!string.IsNullOrWhiteSpace(context.Version?.InheritsFrom))
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Warning,
                localizer.Get("launch.version.inherits", context.Instance.VersionId, context.Version.InheritsFrom));
        }

        return new LaunchStepResult(
            Id,
            LaunchStepStatus.Success,
            localizer.Get("launch.version.found", context.Instance.VersionId));
    }
}
