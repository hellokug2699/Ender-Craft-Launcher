// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Instances;

public interface IInstanceService
{
    Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default);

    Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default);

    Task<Result<GameInstance>> CreateAsync(
        string name,
        string gameVersion,
        string loader = "vanilla",
        string? loaderVersion = null,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(string instanceId, CancellationToken cancellationToken = default);

    Task TouchLastPlayedAsync(string instanceId, CancellationToken cancellationToken = default);
}
