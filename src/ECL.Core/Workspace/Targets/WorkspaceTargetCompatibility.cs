// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>
/// 旧 <c>TargetResource</c> 的兼容解析与一次性规范化（迁移规划 §4.3）。
///
/// 迁移期间可能同时出现两种写法：裸实例 ID 与旧 <c>instance:&lt;id&gt;</c> 前缀。
/// 两者是等价身份，必须规范化成同一种表示（<c>instance:v1:&lt;id&gt;</c>），
/// 否则同一实例会打开两个工作区标签；版本高于当前构建的目标必须**明确拒绝**而不是
/// 退化为任意字符串。
///
/// 本类型只处理身份，不读取文件、不校验目标是否存在。
/// </summary>
public static class WorkspaceTargetCompatibility
{
    /// <summary>解析旧值或新值；失败时 <paramref name="error"/> 说明原因。</summary>
    public static bool TryResolve(string? rawTarget, out WorkspaceTarget target, out WorkspaceTargetError error)
    {
        var result = WorkspaceTargetSerializer.Parse(rawTarget);
        target = result.Target ?? WorkspaceTarget.Destination;
        error = result.Error;
        return result.Success;
    }

    /// <summary>解析旧值或新值；失败时抛出 <see cref="FormatException"/>。</summary>
    public static WorkspaceTarget Resolve(string? rawTarget) => WorkspaceTargetSerializer.ParseRequired(rawTarget);

    /// <summary>把旧值规范化为当前序列化形式（目的地返回 <c>null</c>）。</summary>
    public static string? Normalize(string? rawTarget) => WorkspaceTargetSerializer.Serialize(Resolve(rawTarget));

    /// <summary>
    /// 两个旧/新写法是否表示同一身份。任一侧无法解析时返回 <c>false</c>——
    /// 无法证明等价的身份不得被合并。
    /// </summary>
    public static bool AreEquivalent(string? left, string? right)
    {
        var leftResult = WorkspaceTargetSerializer.Parse(left);
        if (!leftResult.Success)
        {
            return false;
        }

        var rightResult = WorkspaceTargetSerializer.Parse(right);
        if (!rightResult.Success)
        {
            return false;
        }

        return string.Equals(
            WorkspaceTargetSerializer.Serialize(leftResult.Target),
            WorkspaceTargetSerializer.Serialize(rightResult.Target),
            StringComparison.Ordinal);
    }
}
