// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Instances;

public interface IInstanceStore
{
    Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken cancellationToken = default);

    Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default);

    Task SaveAsync(GameInstance instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按删除计划移除记录与（可选的）ECL 托管数据。实现不得删除
    /// <see cref="InstanceDirectoryOwnership.External"/> / <see cref="InstanceDirectoryOwnership.SharedExternal"/> 路径。
    /// </summary>
    Task<Result> DeleteAsync(
        InstanceDeletionPlan plan,
        InstanceDeletionMode mode,
        CancellationToken cancellationToken = default);
}
