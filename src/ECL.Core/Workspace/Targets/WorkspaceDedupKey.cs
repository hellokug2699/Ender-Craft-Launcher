// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>
/// ToolId 语法（迁移规划 §4.3）：全小写 ASCII，
/// <c>[a-z][a-z0-9]*(-[a-z0-9]+)*</c>，工具工作区可再加一段 <c>.&lt;name&gt;</c>。
/// 不得包含用户可见名称或本地化文案；一经发布不得改名。
/// </summary>
public static class WorkspaceToolId
{
    /// <summary>ToolId 允许的最大长度。</summary>
    public const int MaxLength = 64;

    /// <summary>ToolId 是否合法（已规范，无需再 trim）。</summary>
    public static bool IsValid(string? toolId) => TryNormalize(toolId, out _);

    /// <summary>校验并规范化。只接受已经符合语法的小写值，不做大小写转换。</summary>
    public static bool TryNormalize(string? toolId, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return false;
        }

        var trimmed = toolId.Trim();
        if (trimmed.Length > MaxLength)
        {
            return false;
        }

        // 可选的工具工作区后缀：tool.<name>，只允许一段。
        var dot = trimmed.IndexOf('.');
        string[] segments;
        if (dot >= 0)
        {
            if (dot == 0 || dot == trimmed.Length - 1 || trimmed.IndexOf('.', dot + 1) >= 0)
            {
                return false;
            }

            segments = [trimmed[..dot], trimmed[(dot + 1)..]];
        }
        else
        {
            segments = [trimmed];
        }

        foreach (var segment in segments)
        {
            if (!IsValidSegment(segment))
            {
                return false;
            }
        }

        normalized = trimmed;
        return true;
    }

    /// <summary><c>[a-z][a-z0-9]*(-[a-z0-9]+)*</c>。</summary>
    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0 || segment[0] is < 'a' or > 'z')
        {
            return false;
        }

        var previousWasDash = false;
        foreach (var character in segment)
        {
            if (character == '-')
            {
                if (previousWasDash)
                {
                    return false;
                }

                previousWasDash = true;
                continue;
            }

            if (!(character is >= '0' and <= '9' || character is >= 'a' and <= 'z'))
            {
                return false;
            }

            previousWasDash = false;
        }

        return !previousWasDash;
    }
}

/// <summary>
/// 工作区去重键：<c>(ToolId, 规范化 TargetResource)</c>（迁移规划 §4.3）。
///
/// 这是工作区复用判定的**唯一**实现：页面不得自行拼接或比较裸字符串。
/// 比较使用 Ordinal，规范化由 <see cref="WorkspaceTargetSerializer"/> 保证。
/// </summary>
public readonly record struct WorkspaceDedupKey
{
    private WorkspaceDedupKey(string toolId, string? targetResource)
    {
        ToolId = toolId;
        TargetResource = targetResource;
    }

    public string ToolId { get; }

    /// <summary>规范化后的目标资源；目的地为 <c>null</c>。</summary>
    public string? TargetResource { get; }

    /// <summary>由已校验的 ToolId 与强类型目标构造。</summary>
    public static WorkspaceDedupKey Create(string toolId, WorkspaceTarget? target)
    {
        if (!WorkspaceToolId.TryNormalize(toolId, out var normalizedToolId))
        {
            throw new ArgumentException($"ToolId 不合法：{toolId}。", nameof(toolId));
        }

        return new WorkspaceDedupKey(normalizedToolId, WorkspaceTargetSerializer.Serialize(target));
    }

    /// <summary>由序列化后的目标字符串构造；语法错误不会抛异常，供只读比较使用。</summary>
    public static bool TryCreate(
        string toolId,
        string? serializedTarget,
        out WorkspaceDedupKey key,
        out WorkspaceTargetError error)
    {
        key = default;

        if (!WorkspaceToolId.TryNormalize(toolId, out var normalizedToolId))
        {
            error = WorkspaceTargetError.MalformedResource;
            return false;
        }

        var result = WorkspaceTargetSerializer.Parse(serializedTarget);
        if (!result.Success)
        {
            error = result.Error;
            return false;
        }

        key = new WorkspaceDedupKey(normalizedToolId, WorkspaceTargetSerializer.Serialize(result.Target));
        error = WorkspaceTargetError.None;
        return true;
    }

    /// <summary>比较两个工作区身份是否相同；任一侧非法时返回 <c>false</c>。</summary>
    public static bool AreEqual(
        string leftToolId,
        string? leftTarget,
        string rightToolId,
        string? rightTarget)
    {
        if (!TryCreate(leftToolId, leftTarget, out var left, out _)
            || !TryCreate(rightToolId, rightTarget, out var right, out _))
        {
            return false;
        }

        return left == right;
    }
}
