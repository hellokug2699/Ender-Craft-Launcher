// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class BuildCommandStep(ILocalizer localizer) : ILaunchStep
{
    public string Id => "command";

    public int Order => 400;

    public Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        var command = CommandLineBuilder.Build(context);
        context.Command = command;

        return Task.FromResult(new LaunchStepResult(
            Id,
            LaunchStepStatus.Success,
            localizer.Get("launch.command.built", command.Arguments.Count)));
    }
}
