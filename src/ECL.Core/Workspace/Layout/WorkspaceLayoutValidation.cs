// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>布局操作与校验的稳定原因码；用户可见文案由 UI 层本地化。</summary>
public enum WorkspaceLayoutError
{
    None,
    EmptyLayout,
    AnchorNotFound,
    DuplicateWorkspace,
    TooDeep,
    TooManyWorkspaces,
    InvalidRatio,
}

public sealed class WorkspaceLayoutException(WorkspaceLayoutError error)
    : InvalidOperationException($"工作区布局操作被拒绝：{error}")
{
    public WorkspaceLayoutError Error { get; } = error;
}

/// <summary>
/// 布局校验结果。恢复持久化布局时必须先校验：节点数量、深度、工作区引用和比例。
/// 校验失败的布局回退到安全默认布局，不阻塞启动。
/// </summary>
public sealed record WorkspaceLayoutValidation(IReadOnlyList<WorkspaceLayoutError> Errors)
{
    public static WorkspaceLayoutValidation Valid { get; } = new([]);

    public bool IsValid => Errors.Count == 0;

    public static WorkspaceLayoutValidation Validate(WorkspaceLayoutNode? root)
    {
        if (root is null)
        {
            return Valid;
        }

        var errors = new List<WorkspaceLayoutError>();
        if (root.SplitDepth > WorkspaceLayout.MaxSplitDepth)
        {
            errors.Add(WorkspaceLayoutError.TooDeep);
        }

        if (root.LeafCount > WorkspaceLayout.MaxVisibleWorkspaces)
        {
            errors.Add(WorkspaceLayoutError.TooManyWorkspaces);
        }

        var seen = new HashSet<WorkspaceId>();
        foreach (var workspaceId in root.EnumerateWorkspaces())
        {
            if (!seen.Add(workspaceId))
            {
                errors.Add(WorkspaceLayoutError.DuplicateWorkspace);
                break;
            }
        }

        if (HasOutOfRangeRatio(root))
        {
            errors.Add(WorkspaceLayoutError.InvalidRatio);
        }

        return errors.Count == 0 ? Valid : new WorkspaceLayoutValidation(errors);
    }

    private static bool HasOutOfRangeRatio(WorkspaceLayoutNode node) => node switch
    {
        WorkspaceSplitNode split =>
            split.Ratio is < WorkspaceSplitNode.MinRatio or > WorkspaceSplitNode.MaxRatio
            || HasOutOfRangeRatio(split.First)
            || HasOutOfRangeRatio(split.Second),
        _ => false,
    };
}
