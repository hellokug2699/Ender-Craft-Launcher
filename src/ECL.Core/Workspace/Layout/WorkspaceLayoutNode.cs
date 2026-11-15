// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>
/// Split Tree 节点。只包含布局结构与工作区引用，不保存工具组件树、业务对象或文档内容。
/// </summary>
public abstract class WorkspaceLayoutNode
{
    /// <summary>子树中 SplitNode 的最大嵌套层数；叶子为 0。</summary>
    public abstract int SplitDepth { get; }

    public abstract int LeafCount { get; }

    public abstract IEnumerable<WorkspaceId> EnumerateWorkspaces();

    /// <summary>叶子在树中的祖先分割数；根叶子为 0。</summary>
    internal abstract int DepthOf(WorkspaceId workspaceId, int depth);
}

/// <summary>工作区叶节点，表示一个可见工作区位置。</summary>
public sealed class WorkspaceLeafNode(WorkspaceId workspaceId) : WorkspaceLayoutNode
{
    public WorkspaceId WorkspaceId { get; } = workspaceId ?? throw new ArgumentNullException(nameof(workspaceId));

    public override int SplitDepth => 0;

    public override int LeafCount => 1;

    public override IEnumerable<WorkspaceId> EnumerateWorkspaces()
    {
        yield return WorkspaceId;
    }

    internal override int DepthOf(WorkspaceId workspaceId, int depth) =>
        WorkspaceId == workspaceId ? depth : -1;
}

/// <summary>
/// 分割节点。<see cref="Ratio"/> 是第一个子节点占用的比例，始终被限制在
/// <see cref="MinRatio"/>–<see cref="MaxRatio"/> 之间，避免出现零尺寸面板。
/// </summary>
public sealed class WorkspaceSplitNode : WorkspaceLayoutNode
{
    public const double MinRatio = 0.2;
    public const double MaxRatio = 0.8;
    public const double DefaultRatio = 0.5;

    public WorkspaceSplitNode(
        WorkspaceSplitOrientation orientation,
        double ratio,
        WorkspaceLayoutNode first,
        WorkspaceLayoutNode second)
    {
        Orientation = orientation;
        Ratio = ClampRatio(ratio);
        First = first ?? throw new ArgumentNullException(nameof(first));
        Second = second ?? throw new ArgumentNullException(nameof(second));
    }

    public WorkspaceSplitOrientation Orientation { get; }

    public double Ratio { get; }

    public WorkspaceLayoutNode First { get; }

    public WorkspaceLayoutNode Second { get; }

    public override int SplitDepth => 1 + Math.Max(First.SplitDepth, Second.SplitDepth);

    public override int LeafCount => First.LeafCount + Second.LeafCount;

    public override IEnumerable<WorkspaceId> EnumerateWorkspaces() =>
        First.EnumerateWorkspaces().Concat(Second.EnumerateWorkspaces());

    public WorkspaceSplitNode WithRatio(double ratio) => new(Orientation, ratio, First, Second);

    public WorkspaceSplitNode WithChildren(WorkspaceLayoutNode first, WorkspaceLayoutNode second) =>
        new(Orientation, Ratio, first, second);

    internal override int DepthOf(WorkspaceId workspaceId, int depth)
    {
        var inFirst = First.DepthOf(workspaceId, depth + 1);
        return inFirst >= 0 ? inFirst : Second.DepthOf(workspaceId, depth + 1);
    }

    public static double ClampRatio(double ratio) =>
        double.IsFinite(ratio) ? Math.Clamp(ratio, MinRatio, MaxRatio) : DefaultRatio;
}
