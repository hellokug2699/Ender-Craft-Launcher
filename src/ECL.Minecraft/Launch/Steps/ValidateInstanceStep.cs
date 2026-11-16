// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Engine.Transactions;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class ValidateInstanceStep(
    IAppPaths paths,
    ILocalizer localizer,
    InstanceTransactionCoordinator transactions) : ILaunchStep
{
    public string Id => "validate";

    public int Order => 100;

    public async Task<LaunchStepResult> ExecuteAsync(
        LaunchContext context,
        CancellationToken cancellationToken = default)
    {
        var instanceDirectory = paths.GetInstanceDirectory(context.Instance.Id);
        if (!Directory.Exists(instanceDirectory))
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.validate.directoryMissing", instanceDirectory));
        }

        // F-04 §5.3：存在未完成/需恢复事务时阻止启动，避免在中间态上启动游戏。
        var activity = await transactions.GetActivityStateAsync(context.Instance.Id, cancellationToken)
            .ConfigureAwait(false);
        if (activity.IsSuccess)
        {
            switch (activity.Value)
            {
                case InstanceActivityState.NeedsRecovery:
                    return new LaunchStepResult(
                        Id,
                        LaunchStepStatus.Failed,
                        localizer.Get("launch.validate.needsRecovery"));
                case InstanceActivityState.Recovering:
                    return new LaunchStepResult(
                        Id,
                        LaunchStepStatus.Failed,
                        localizer.Get("launch.validate.recovering"));
                case InstanceActivityState.Writing:
                case InstanceActivityState.Changing:
                    return new LaunchStepResult(
                        Id,
                        LaunchStepStatus.Failed,
                        localizer.Get("launch.validate.changing"));
            }
        }

        if (string.IsNullOrWhiteSpace(context.Account.Name))
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Failed,
                localizer.Get("launch.validate.accountMissing"));
        }

        return new LaunchStepResult(
            Id,
            LaunchStepStatus.Success,
            localizer.Get("launch.validate.ok", context.Instance.Name, context.Account.Name));
    }
}
