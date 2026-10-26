// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Instances;

public interface IInstanceStore
{
    Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken cancellationToken = default);

    Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default);

    Task SaveAsync(GameInstance instance, CancellationToken cancellationToken = default);

    Task DeleteAsync(string instanceId, CancellationToken cancellationToken = default);
}
