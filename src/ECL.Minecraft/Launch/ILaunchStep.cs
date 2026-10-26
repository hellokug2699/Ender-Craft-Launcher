// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Launch;

public interface ILaunchStep
{
    string Id { get; }

    int Order { get; }

    Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default);
}
