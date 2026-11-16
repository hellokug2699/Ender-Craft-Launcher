// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Storage;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Lifecycle;

/// <summary>
/// F-02 §5.4 来源活动探测：解绑来源前检查该来源下的活动游戏会话/写入任务。
/// 只读探测，不终止进程、不修改会话；探测失败必须返回错误，由调用方保守阻止解绑。
/// </summary>
public sealed class InstanceSourceActivityProbe : IInstanceSourceActivityProbe
{
    private readonly IInstanceService _instances;
    private readonly IInstanceActivityGuard _guard;
    private readonly IWorkspaceService _workspace;
    private readonly IAppPaths _paths;
    private readonly ILogger<InstanceSourceActivityProbe> _logger;

    public InstanceSourceActivityProbe(
        IInstanceService instances,
        IInstanceActivityGuard guard,
        IWorkspaceService workspace,
        IAppPaths paths,
        ILogger<InstanceSourceActivityProbe>? logger = null)
    {
        _instances = instances;
        _guard = guard;
        _workspace = workspace;
        _paths = paths;
        _logger = logger ?? NullLogger<InstanceSourceActivityProbe>.Instance;
    }

    public async Task<Result<IReadOnlyList<ActiveInstanceReference>>> FindActiveAsync(
        string rootId,
        CancellationToken cancellationToken = default)
    {
        var root = _workspace.Roots.FirstOrDefault(item =>
            string.Equals(item.Id, rootId, StringComparison.Ordinal));
        if (root is null)
        {
            return Result<IReadOnlyList<ActiveInstanceReference>>.Fail(
                "source.root_missing",
                "未找到该来源的登记路径。");
        }

        var instances = await _instances.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        var active = new List<ActiveInstanceReference>();
        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BelongsToRoot(instance, root.Path))
            {
                continue;
            }

            var inspection = await _guard.InspectAsync(instance.Id, cancellationToken).ConfigureAwait(false);
            if (!inspection.IsSuccess)
            {
                _logger.LogWarning(
                    "检查实例 {InstanceId} 的活动状态失败：{Error}",
                    instance.Id,
                    inspection.Error);
                return Result<IReadOnlyList<ActiveInstanceReference>>.Fail(
                    "source.activity_unknown",
                    "无法确认来源下实例的活动状态。",
                    inspection.Error?.Message);
            }

            var state = inspection.Value!;
            if (state.HasActiveSession)
            {
                active.Add(new ActiveInstanceReference(instance.Id, state.SessionId, "有活动游戏会话。"));
            }
            else if (state.HasPendingTransaction)
            {
                active.Add(new ActiveInstanceReference(instance.Id, null, $"实例事务状态：{state.TransactionState}。"));
            }
        }

        return Result<IReadOnlyList<ActiveInstanceReference>>.Ok(active);
    }

    private bool BelongsToRoot(GameInstance instance, string root)
    {
        if (PathIdentity.IsUnder(_paths.GetInstanceDirectory(instance.Id), root))
        {
            return true;
        }

        var gameDirectory = string.IsNullOrWhiteSpace(instance.GameDirectory)
            ? _paths.GetInstanceGameDirectory(instance.Id)
            : instance.GameDirectory;
        return PathIdentity.IsUnder(gameDirectory, root);
    }
}
