// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Localization;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class ValidateInstanceStep(IAppPaths paths, ILocalizer localizer) : ILaunchStep
{
    public string Id => "validate";

    public int Order => 100;

    public Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        var instanceDirectory = paths.GetInstanceDirectory(context.Instance.Id);
        if (!Directory.Exists(instanceDirectory))
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.validate.directoryMissing", instanceDirectory)));
        }

        if (string.IsNullOrWhiteSpace(context.Account.Name))
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.validate.accountMissing")));
        }

        return Task.FromResult(new LaunchStepResult(
            Id,
            LaunchStepStatus.Success,
            localizer.Get("launch.validate.ok", context.Instance.Name, context.Account.Name)));
    }
}
