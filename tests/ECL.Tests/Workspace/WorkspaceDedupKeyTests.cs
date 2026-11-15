// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Workspace;
using Xunit;

namespace Ecl.Tests.Workspace;

/// <summary>
/// 去重键 <c>(ToolId, 规范化 TargetResource)</c> 与 ToolId 语法（迁移规划 §4.3）。
/// 这是工作区复用判定的唯一实现：页面不得自行比较裸字符串。
/// </summary>
public sealed class WorkspaceDedupKeyTests
{
    private static WorkspaceTarget Target(string raw) => WorkspaceTargetSerializer.ParseRequired(raw);

    [Fact]
    public void DestinationKeyHasNoTargetResource()
    {
        var key = WorkspaceDedupKey.Create("home", WorkspaceTarget.Destination);

        Assert.Equal("home", key.ToolId);
        Assert.Null(key.TargetResource);
        Assert.Equal(key, WorkspaceDedupKey.Create("home", null));
    }

    [Fact]
    public void EquivalentLegacyFormsProduceSameKey()
    {
        var bare = WorkspaceDedupKey.Create("instance", Target("survival"));
        var legacy = WorkspaceDedupKey.Create("instance", Target("instance:survival"));

        Assert.Equal(bare, legacy);
        Assert.True(WorkspaceDedupKey.AreEqual("instance", "survival", "instance", "instance:survival"));
    }

    [Fact]
    public void DifferentToolOrTargetIsNotEqual()
    {
        var key = WorkspaceDedupKey.Create("instance", Target("survival"));

        Assert.NotEqual(key, WorkspaceDedupKey.Create("tool.world-workbench", Target("survival")));
        Assert.NotEqual(key, WorkspaceDedupKey.Create("instance", Target("creative")));
        Assert.NotEqual(key, WorkspaceDedupKey.Create("instance", WorkspaceTarget.Destination));
    }

    [Fact]
    public void InvalidToolIdIsRejectedWithoutOpeningAnything()
    {
        Assert.Throws<ArgumentException>(() => WorkspaceDedupKey.Create("Home", WorkspaceTarget.Destination));
        Assert.False(WorkspaceDedupKey.TryCreate("Home", null, out _, out _));
        Assert.False(WorkspaceDedupKey.AreEqual("Home", null, "Home", null));
    }

    [Fact]
    public void InvalidTargetPreventsKeyCreation()
    {
        Assert.False(WorkspaceDedupKey.TryCreate("instance", "a/b", out _, out var error));
        Assert.Equal(WorkspaceTargetError.PathSeparator, error);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("instances")]
    [InlineData("tool.world-workbench")]
    [InlineData("tool.nbt-explorer")]
    [InlineData("a1-b2-c3")]
    public void ValidToolIdsAreAccepted(string toolId)
    {
        Assert.True(WorkspaceToolId.IsValid(toolId));
        Assert.True(WorkspaceToolId.TryNormalize(toolId, out var normalized));
        Assert.Equal(toolId, normalized);
    }

    [Theory]
    [InlineData("Home")]
    [InlineData("TOOL")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-a")]
    [InlineData("a-")]
    [InlineData("a--b")]
    [InlineData("1a")]
    [InlineData("a_b")]
    [InlineData("a b")]
    [InlineData("tool.")]
    [InlineData(".tool")]
    [InlineData("tool..x")]
    [InlineData("tool.a.b")]
    public void InvalidToolIdsAreRejected(string toolId)
    {
        Assert.False(WorkspaceToolId.IsValid(toolId));
    }

    [Fact]
    public void OverlongToolIdIsRejected()
    {
        var toolId = "a" + new string('b', WorkspaceToolId.MaxLength);

        Assert.False(WorkspaceToolId.IsValid(toolId));
    }

    [Fact]
    public void HostFindsWorkspaceAcrossLegacyAndNormalizedIdentity()
    {
        var host = new WorkspaceHost();
        host.Open(WorkspaceDescriptor.Create("instance", "实例", "instance:survival"));

        Assert.NotNull(host.FindByTarget("instance", "survival"));
        Assert.Null(host.FindByTarget("instance", "creative"));
        Assert.Null(host.FindByTarget("instances", "survival"));
    }
}
