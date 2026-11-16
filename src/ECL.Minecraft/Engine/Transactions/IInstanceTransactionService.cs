// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Engine.Transactions;

/// <summary>
/// 实例内容写入的唯一公开事务入口（F-04 §5.2）。
/// 调用方不得依赖 <see cref="InstanceTransactionCoordinator"/> 的内部锁或内存状态。
/// </summary>
public interface IInstanceTransactionService
{
    Task<Result<InstanceTransactionRecord>> ExecuteAsync(
        string instanceId,
        InstanceChangePlan plan,
        CancellationToken cancellationToken = default);

    Task<Result<InstanceTransactionRecord>> GetAsync(
        string instanceId,
        string transactionId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<InstanceTransactionRecord>>> ListAsync(
        string instanceId,
        InstanceTransactionListFilter? filter = null,
        CancellationToken cancellationToken = default);

    Task<Result<RecoverySweepReport>> RecoverPendingAsync(
        string? instanceId = null,
        CancellationToken cancellationToken = default);

    Task<Result<InstanceTransactionRecord>> RecoverAsync(
        string instanceId,
        string transactionId,
        RecoveryResolution resolution = RecoveryResolution.Auto,
        CancellationToken cancellationToken = default);

    Task<Result<InstanceActivityState>> GetActivityStateAsync(
        string instanceId,
        CancellationToken cancellationToken = default);
}
