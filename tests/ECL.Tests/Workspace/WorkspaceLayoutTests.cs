// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Workspace;
using Xunit;

namespace Ecl.Tests.Workspace;

public sealed class WorkspaceLayoutTests
{
    private static WorkspaceId Id(string value) => WorkspaceId.Create(value);

    [Fact]
    public void SingleWorkspaceIsAValidLeaf()
    {
        var layout = WorkspaceLayout.Single(Id("a"));

        Assert.Equal(1, layout.LeafCount);
        Assert.Equal(0, layout.SplitDepth);
        Assert.Equal(new[] { Id("a") }, layout.Workspaces);
        Assert.True(layout.Validate().IsValid);
    }

    [Fact]
    public void SplittingALeafProducesHorizontalSplit()
    {
        var layout = WorkspaceLayout.Single(Id("a")).AddLeaf(Id("a"), Id("b"), WorkspaceSplitOrientation.Horizontal);

        var split = Assert.IsType<WorkspaceSplitNode>(layout.Root);
        Assert.Equal(WorkspaceSplitOrientation.Horizontal, split.Orientation);
        Assert.Equal(WorkspaceSplitNode.DefaultRatio, split.Ratio);
        Assert.Equal(new[] { Id("a"), Id("b") }, layout.Workspaces);
        Assert.Equal(1, layout.SplitDepth);
    }

    [Fact]
    public void AllowsTwoSplitLevelsAndFourVisibleWorkspaces()
    {
        var layout = WorkspaceLayout.Single(Id("a"))
            .AddLeaf(Id("a"), Id("b"), WorkspaceSplitOrientation.Horizontal)
            .AddLeaf(Id("a"), Id("c"), WorkspaceSplitOrientation.Vertical)
            .AddLeaf(Id("b"), Id("d"), WorkspaceSplitOrientation.Vertical);

        Assert.Equal(2, layout.SplitDepth);
        Assert.Equal(4, layout.LeafCount);
        Assert.True(layout.Validate().IsValid);
    }

    [Fact]
    public void RejectsSplitThatWouldExceedMaximumDepth()
    {
        var layout = WorkspaceLayout.Single(Id("a"))
            .AddLeaf(Id("a"), Id("b"), WorkspaceSplitOrientation.Horizontal)
            .AddLeaf(Id("a"), Id("c"), WorkspaceSplitOrientation.Vertical);

        var added = layout.TryAddLeaf(Id("a"), Id("e"), WorkspaceSplitOrientation.Horizontal, out var result, out var error);

        Assert.False(added);
        Assert.Same(layout, result);
        Assert.Equal(WorkspaceLayoutError.TooDeep, error);
    }

    [Fact]
    public void RejectsMoreThanFourVisibleWorkspaces()
    {
        var layout = WorkspaceLayout.Single(Id("a"))
            .AddLeaf(Id("a"), Id("b"), WorkspaceSplitOrientation.Horizontal)
            .AddLeaf(Id("a"), Id("c"), WorkspaceSplitOrientation.Vertical)
            .AddLeaf(Id("b"), Id("d"), WorkspaceSplitOrientation.Vertical);

        var added = layout.TryAddLeaf(Id("c"), Id("e"), WorkspaceSplitOrientation.Vertical, out _, out var error);

        Assert.False(added);
        Assert.Equal(WorkspaceLayoutError.TooManyWorkspaces, error);
    }

    [Fact]
    public void RejectsUnknownAnchorAndDuplicateWorkspace()
    {
        var layout = WorkspaceLayout.Single(Id("a"));

        Assert.False(layout.TryAddLeaf(Id("missing"), Id("b"), WorkspaceSplitOrientation.Horizontal, out _, out var anchorError));
        Assert.Equal(WorkspaceLayoutError.AnchorNotFound, anchorError);

        Assert.False(layout.TryAddLeaf(Id("a"), Id("a"), WorkspaceSplitOrientation.Horizontal, out _, out var duplicateError));
        Assert.Equal(WorkspaceLayoutError.DuplicateWorkspace, duplicateError);
    }

    [Fact]
    public void RatioIsClampedToUsableRange()
    {
        var split = new WorkspaceSplitNode(
            WorkspaceSplitOrientation.Horizontal,
            0.05,
            new WorkspaceLeafNode(Id("a")),
            new WorkspaceLeafNode(Id("b")));

        Assert.Equal(WorkspaceSplitNode.MinRatio, split.Ratio);
        Assert.Equal(WorkspaceSplitNode.MaxRatio, split.WithRatio(9).Ratio);
        Assert.Equal(WorkspaceSplitNode.DefaultRatio, split.WithRatio(double.NaN).Ratio);
    }

    [Fact]
    public void RemovingLeafCollapsesEmptySplitNodes()
    {
        var layout = WorkspaceLayout.Single(Id("a"))
            .AddLeaf(Id("a"), Id("b"), WorkspaceSplitOrientation.Horizontal)
            .AddLeaf(Id("a"), Id("c"), WorkspaceSplitOrientation.Vertical);

        var reduced = layout.Remove(Id("c"));

        var split = Assert.IsType<WorkspaceSplitNode>(reduced.Root);
        Assert.IsType<WorkspaceLeafNode>(split.First);
        Assert.Equal(new[] { Id("a"), Id("b") }, reduced.Workspaces);
    }

    [Fact]
    public void RemovingLastLeafReturnsEmptyLayout()
    {
        var reduced = WorkspaceLayout.Single(Id("a")).Remove(Id("a"));

        Assert.True(reduced.IsEmpty);
        Assert.Empty(reduced.Workspaces);
        Assert.True(reduced.Validate().IsValid);
    }

    [Fact]
    public void RemovingUnknownWorkspaceKeepsLayout()
    {
        var layout = WorkspaceLayout.Single(Id("a")).AddLeaf(Id("a"), Id("b"), WorkspaceSplitOrientation.Horizontal);

        var unchanged = layout.Remove(Id("missing"));

        Assert.Equal(new[] { Id("a"), Id("b") }, unchanged.Workspaces);
    }

    [Fact]
    public void ValidationDetectsDuplicateWorkspaceReferences()
    {
        var duplicated = new WorkspaceSplitNode(
            WorkspaceSplitOrientation.Horizontal,
            WorkspaceSplitNode.DefaultRatio,
            new WorkspaceLeafNode(Id("a")),
            new WorkspaceLeafNode(Id("a")));

        var validation = WorkspaceLayoutValidation.Validate(duplicated);

        Assert.False(validation.IsValid);
        Assert.Contains(WorkspaceLayoutError.DuplicateWorkspace, validation.Errors);
    }

    [Fact]
    public void FromRootRejectsInvalidLayout()
    {
        var tooDeep = new WorkspaceSplitNode(
            WorkspaceSplitOrientation.Horizontal,
            WorkspaceSplitNode.DefaultRatio,
            new WorkspaceSplitNode(
                WorkspaceSplitOrientation.Vertical,
                WorkspaceSplitNode.DefaultRatio,
                new WorkspaceSplitNode(
                    WorkspaceSplitOrientation.Horizontal,
                    WorkspaceSplitNode.DefaultRatio,
                    new WorkspaceLeafNode(Id("a")),
                    new WorkspaceLeafNode(Id("b"))),
                new WorkspaceLeafNode(Id("c"))),
            new WorkspaceLeafNode(Id("d")));

        var exception = Assert.Throws<WorkspaceLayoutException>(() => WorkspaceLayout.FromRoot(tooDeep));
        Assert.Equal(WorkspaceLayoutError.TooDeep, exception.Error);
    }

    [Fact]
    public void RatioCanBeAdjustedForSplitWithoutTouchingWorkspaces()
    {
        var layout = WorkspaceLayout.Single(Id("a")).AddLeaf(Id("a"), Id("b"), WorkspaceSplitOrientation.Horizontal);

        var adjusted = layout.WithRatio(Id("a"), 0.7);

        var split = Assert.IsType<WorkspaceSplitNode>(adjusted.Root);
        Assert.Equal(0.7, split.Ratio, 3);
        Assert.Equal(new[] { Id("a"), Id("b") }, adjusted.Workspaces);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Upper Case")]
    [InlineData("has/slash")]
    public void WorkspaceIdRejectsInvalidValues(string value)
    {
        Assert.Throws<ArgumentException>(() => WorkspaceId.Create(value));
    }

    [Fact]
    public void WorkspaceIdRejectsOverlongValues()
    {
        Assert.Throws<ArgumentException>(() => WorkspaceId.Create(new string('a', WorkspaceId.MaxLength + 1)));
    }
}
