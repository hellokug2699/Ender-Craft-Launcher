// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class StartGameStep(
    IProcessLauncher processLauncher,
    ILocalizer localizer,
    ILogger<StartGameStep> logger) : ILaunchStep
{
    public string Id => "start";

    public int Order => 500;

    public Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        if (context.DryRun)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Skipped,
                localizer.Get("launch.start.dryRun")));
        }

        if (context.Command is null)
        {
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.start.noCommand")));
        }

        try
        {
            var showConsole = context.Settings.Get<bool>("game.showConsole");
            var processId = processLauncher.Start(context.Command, context.GameDirectory, showConsole, context.Instance.Id);
            context.ProcessId = processId;

            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Success,
                localizer.Get("launch.start.started", processId)));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "启动实例 {InstanceId} 的进程失败。", context.Instance.Id);
            return Task.FromResult(new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.start.failed", exception.Message)));
        }
    }
}
