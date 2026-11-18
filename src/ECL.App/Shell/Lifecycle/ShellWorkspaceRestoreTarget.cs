// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Services.Lifecycle;
using Ecl.Core.Workspace.Session;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Shell.Lifecycle;

/// <summary>
/// 恢复目标：把领域层选出的会话条目变成真实导航。
///
/// 只使用 Shell 已有的公开入口（<see cref="ShellViewModel.OpenInstanceWorkspace"/>、
/// <see cref="ShellViewModel.ActivateDestination"/>）；无法解析的条目抛异常，
/// 由 <see cref="WorkspaceRestoreService"/> 逐项记录为 <c>apply_failed</c>，
/// 而不是假装恢复成功。
/// </summary>
public sealed class ShellWorkspaceRestoreTarget(
    ShellViewModel shell,
    ILogger<ShellWorkspaceRestoreTarget> logger) : IWorkspaceRestoreTarget
{
    public void Restore(WorkspaceSessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!string.IsNullOrWhiteSpace(entry.InstanceId))
        {
            if (!shell.OpenInstanceWorkspace(entry.InstanceId))
            {
                throw new InvalidOperationException($"无法打开实例工作区：{entry.InstanceId}");
            }

            return;
        }

        // 固定目的地：优先使用快照记录的 Destination，其次才是工具 ID。
        var key = string.IsNullOrWhiteSpace(entry.Destination) ? entry.ToolId : entry.Destination;
        if (string.IsNullOrWhiteSpace(key) || !shell.ActivateDestination(key))
        {
            logger.LogWarning("会话条目没有可解析的目的地：{ToolId} / {Destination}", entry.ToolId, entry.Destination);
            throw new InvalidOperationException($"无法恢复工作区目的地：{key}");
        }
    }
}
