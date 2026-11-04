// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>
/// 工作区的稳定描述。标题与目标资源只是展示/恢复引用，不包含工具业务对象。
/// </summary>
/// <param name="Id">工作区实例标识；同一工具重复打开会得到不同 ID。</param>
/// <param name="ToolId">工具定义标识，与全局导航目的地或 Addition 工具对应。</param>
/// <param name="Title">用户可见标题（通常已本地化）。</param>
/// <param name="TargetResource">可选目标资源引用，例如实例 ID；不存放路径或业务数据。</param>
/// <param name="CreatedAt">创建时间，用于排序与恢复提示。</param>
public sealed record WorkspaceDescriptor(
    WorkspaceId Id,
    string ToolId,
    string Title,
    string? TargetResource,
    DateTimeOffset CreatedAt)
{
    public static WorkspaceDescriptor Create(string toolId, string title, string? targetResource = null, DateTimeOffset? createdAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return new WorkspaceDescriptor(
            WorkspaceId.New(),
            toolId.Trim(),
            title.Trim(),
            string.IsNullOrWhiteSpace(targetResource) ? null : targetResource.Trim(),
            createdAt ?? DateTimeOffset.UtcNow);
    }
}
