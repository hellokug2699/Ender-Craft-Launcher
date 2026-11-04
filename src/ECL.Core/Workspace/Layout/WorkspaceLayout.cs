// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>
/// 不可变 Split Tree。约束来自 ADR 0055 §2：
/// 分割嵌套最多 <see cref="MaxSplitDepth"/> 层，同时可见工作区最多 <see cref="MaxVisibleWorkspaces"/> 个；
/// 关闭工作区后自动折叠空节点。布局与 Session 持久化分离，本类型不保存任何 UI 或业务状态。
/// </summary>
public sealed class WorkspaceLayout
{
    public const int MaxSplitDepth = 2;
    public const int MaxVisibleWorkspaces = 4;
    public const int SchemaVersion = 1;

    public static WorkspaceLayout Empty { get; } = new(null);

    private WorkspaceLayout(WorkspaceLayoutNode? root)
    {
        Root = root;
        Workspaces = root is null ? [] : root.EnumerateWorkspaces().ToArray();
    }

    public WorkspaceLayoutNode? Root { get; }

    public bool IsEmpty => Root is null;

    public int SplitDepth => Root?.SplitDepth ?? 0;

    public int LeafCount => Root?.LeafCount ?? 0;

    /// <summary>按布局顺序返回可见工作区。</summary>
    public IReadOnlyList<WorkspaceId> Workspaces { get; }

    public static WorkspaceLayout Single(WorkspaceId workspaceId) =>
        new(new WorkspaceLeafNode(workspaceId));

    public static WorkspaceLayout FromRoot(WorkspaceLayoutNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var validation = WorkspaceLayoutValidation.Validate(root);
        if (!validation.IsValid)
        {
            throw new WorkspaceLayoutException(validation.Errors[0]);
        }

        return new WorkspaceLayout(root);
    }

    public bool Contains(WorkspaceId workspaceId) => Workspaces.Contains(workspaceId);

    /// <summary>把一个新工作区分割到 <paramref name="anchorId"/> 旁边。</summary>
    public WorkspaceLayout AddLeaf(WorkspaceId anchorId, WorkspaceId newWorkspaceId, WorkspaceSplitOrientation orientation)
    {
        if (TryAddLeaf(anchorId, newWorkspaceId, orientation, out var layout, out var error))
        {
            return layout;
        }

        throw new WorkspaceLayoutException(error);
    }

    public bool TryAddLeaf(
        WorkspaceId anchorId,
        WorkspaceId newWorkspaceId,
        WorkspaceSplitOrientation orientation,
        out WorkspaceLayout layout,
        out WorkspaceLayoutError error)
    {
        layout = this;
        if (Root is null)
        {
            error = WorkspaceLayoutError.EmptyLayout;
            return false;
        }

        if (Contains(newWorkspaceId))
        {
            error = WorkspaceLayoutError.DuplicateWorkspace;
            return false;
        }

        var anchorDepth = Root.DepthOf(anchorId, 0);
        if (anchorDepth < 0)
        {
            error = WorkspaceLayoutError.AnchorNotFound;
            return false;
        }

        if (LeafCount + 1 > MaxVisibleWorkspaces)
        {
            error = WorkspaceLayoutError.TooManyWorkspaces;
            return false;
        }

        if (anchorDepth + 1 > MaxSplitDepth)
        {
            error = WorkspaceLayoutError.TooDeep;
            return false;
        }

        var replacement = new WorkspaceSplitNode(
            orientation,
            WorkspaceSplitNode.DefaultRatio,
            new WorkspaceLeafNode(anchorId),
            new WorkspaceLeafNode(newWorkspaceId));
        layout = new WorkspaceLayout(ReplaceLeaf(Root, anchorId, replacement));
        error = WorkspaceLayoutError.None;
        return true;
    }

    /// <summary>移除工作区叶节点并折叠因此变空的分割节点；移除不存在的节点返回自身。</summary>
    public WorkspaceLayout Remove(WorkspaceId workspaceId)
    {
        if (Root is null)
        {
            return this;
        }

        var root = RemoveLeaf(Root, workspaceId);
        return root is null ? Empty : new WorkspaceLayout(root);
    }

    public WorkspaceLayout WithRatio(WorkspaceId leftMostWorkspaceOfSplit, double ratio)
    {
        if (Root is null)
        {
            return this;
        }

        var updated = ReplaceSplitRatio(Root, leftMostWorkspaceOfSplit, ratio);
        return ReferenceEquals(updated, Root) ? this : new WorkspaceLayout(updated);
    }

    public WorkspaceLayoutValidation Validate() => WorkspaceLayoutValidation.Validate(Root);

    private static WorkspaceLayoutNode ReplaceLeaf(WorkspaceLayoutNode node, WorkspaceId anchorId, WorkspaceLayoutNode replacement) => node switch
    {
        WorkspaceLeafNode leaf => leaf.WorkspaceId == anchorId ? replacement : leaf,
        WorkspaceSplitNode split => split.WithChildren(
            ReplaceLeaf(split.First, anchorId, replacement),
            ReplaceLeaf(split.Second, anchorId, replacement)),
        _ => node,
    };

    private static WorkspaceLayoutNode? RemoveLeaf(WorkspaceLayoutNode node, WorkspaceId workspaceId)
    {
        if (node is WorkspaceLeafNode leaf)
        {
            return leaf.WorkspaceId == workspaceId ? null : leaf;
        }

        var split = (WorkspaceSplitNode)node;
        var first = RemoveLeaf(split.First, workspaceId);
        var second = RemoveLeaf(split.Second, workspaceId);
        return (first, second) switch
        {
            (null, null) => null,
            (null, _) => second,
            (_, null) => first,
            _ => split.WithChildren(first!, second!),
        };
    }

    private static WorkspaceLayoutNode ReplaceSplitRatio(WorkspaceLayoutNode node, WorkspaceId leftMostWorkspace, double ratio)
    {
        if (node is not WorkspaceSplitNode split)
        {
            return node;
        }

        var leftMost = split.First.EnumerateWorkspaces().First();
        if (leftMost == leftMostWorkspace)
        {
            return split.WithRatio(ratio);
        }

        var first = ReplaceSplitRatio(split.First, leftMostWorkspace, ratio);
        var second = ReplaceSplitRatio(split.Second, leftMostWorkspace, ratio);
        return ReferenceEquals(first, split.First) && ReferenceEquals(second, split.Second)
            ? node
            : split.WithChildren(first, second);
    }
}
