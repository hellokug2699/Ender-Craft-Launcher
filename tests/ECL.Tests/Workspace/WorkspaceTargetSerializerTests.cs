// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Ecl.Core.Workspace;
using Xunit;

namespace Ecl.Tests.Workspace;

/// <summary>
/// 工作区身份的唯一版本化序列化器（B0）：往返、规范化、旧值兼容与畸形输入。
/// serializer 只做身份语法，不承担文件授权或路径边界校验，因此这里不测试文件系统安全。
/// </summary>
public sealed class WorkspaceTargetSerializerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingTargetIsDestination(string? raw)
    {
        var result = WorkspaceTargetSerializer.Parse(raw);

        Assert.True(result.Success);
        Assert.Same(WorkspaceTarget.Destination, result.Target);
        Assert.Null(WorkspaceTargetSerializer.Serialize(result.Target));
    }

    [Fact]
    public void DestinationSerializesToNull()
    {
        Assert.Null(WorkspaceTargetSerializer.Serialize(null));
        Assert.Null(WorkspaceTargetSerializer.Serialize(WorkspaceTarget.Destination));
    }

    [Theory]
    [InlineData("survival-a1b2c3")]
    [InlineData("Instance_42")]
    [InlineData("实例-1")]
    public void LegacyResourceRoundTrips(string raw)
    {
        var parsed = WorkspaceTargetSerializer.ParseRequired(raw);

        Assert.True(parsed.IsLegacyResource);
        Assert.Equal(WorkspaceTarget.UnversionedSchemaVersion, parsed.SchemaVersion);
        Assert.Equal(raw, parsed.Resource);
        Assert.Equal(raw, WorkspaceTargetSerializer.Serialize(parsed));
    }

    [Fact]
    public void LegacyResourceIsTrimmed()
    {
        Assert.Equal("abc", WorkspaceTargetSerializer.ParseRequired("  abc  ").Resource);
    }

    [Fact]
    public void LegacyResourceIsNormalizedToUnicodeFormC()
    {
        // "Cafe" + 组合重音（U+0301）与预组合 "é"（U+00E9）是同一身份。
        var decomposed = "Cafe\u0301";
        var composed = "Caf\u00E9";

        Assert.Equal(composed, WorkspaceTargetSerializer.ParseRequired(decomposed).Resource);
        Assert.True(WorkspaceTargetCompatibility.AreEquivalent(decomposed, composed));
    }

    [Fact]
    public void LegacyResourceKeepsCase()
    {
        // 身份比较使用 Ordinal；大小写不同不得被当作同一工作区。
        Assert.False(WorkspaceTargetCompatibility.AreEquivalent("ABC", "abc"));
    }

    [Fact]
    public void LegacyInstancePrefixIsNormalizedToBareIdentity()
    {
        var parsed = WorkspaceTargetSerializer.ParseRequired("instance:survival-a1b2c3");

        Assert.Equal("survival-a1b2c3", parsed.Resource);
        Assert.Equal("survival-a1b2c3", WorkspaceTargetSerializer.Serialize(parsed));
    }

    [Theory]
    [InlineData("instance:v1:survival-a1b2c3")]
    [InlineData("instance:v2:survival-a1b2c3")]
    [InlineData("file:survival-a1b2c3/mods/a.jar")]
    [InlineData("world:survival-a1b2c3/opaque-locator")]
    public void TargetTypesOutsideB0AreRejectedRatherThanDegraded(string raw)
    {
        var result = WorkspaceTargetSerializer.Parse(raw);

        Assert.False(result.Success);
        Assert.Equal(WorkspaceTargetError.UnsupportedScheme, result.Error);
        Assert.Null(result.Target);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:/minecraft")]
    public void PathSeparatorsAreRejected(string raw)
    {
        var result = WorkspaceTargetSerializer.Parse(raw);

        Assert.False(result.Success);
        Assert.Equal(WorkspaceTargetError.PathSeparator, result.Error);
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    public void ControlCharactersAreRejected(string raw)
    {
        var result = WorkspaceTargetSerializer.Parse(raw);

        Assert.False(result.Success);
        Assert.Equal(WorkspaceTargetError.InvalidCharacter, result.Error);
    }

    [Fact]
    public void OverlongResourceIsRejected()
    {
        var raw = new string('a', WorkspaceTargetSerializer.MaxResourceLength + 1);

        var result = WorkspaceTargetSerializer.Parse(raw);

        Assert.False(result.Success);
        Assert.Equal(WorkspaceTargetError.TooLong, result.Error);
    }

    [Theory]
    [InlineData("instance:")]
    [InlineData("instance:instance:abc")]
    public void MalformedResourceIsRejected(string raw)
    {
        var result = WorkspaceTargetSerializer.Parse(raw);

        Assert.False(result.Success);
        Assert.Equal(WorkspaceTargetError.MalformedResource, result.Error);
    }

    [Fact]
    public void UnknownPrefixIsTreatedAsLegacyIdentity()
    {
        // 只有 instance/file/world 是协议；其它冒号写法保持为旧裸身份，避免误伤既有数据。
        var parsed = WorkspaceTargetSerializer.ParseRequired("foo:bar");

        Assert.True(parsed.IsLegacyResource);
        Assert.Equal("foo:bar", parsed.Resource);
    }

    [Fact]
    public void TryParseReportsFailureWithoutThrowing()
    {
        Assert.False(WorkspaceTargetSerializer.TryParse("a/b", out var target));
        Assert.Null(target);

        Assert.True(WorkspaceTargetSerializer.TryParse("abc", out target));
        Assert.Equal("abc", target!.Resource);
    }

    [Fact]
    public void ParseRequiredThrowsOnFailure()
    {
        Assert.Throws<FormatException>(() => WorkspaceTargetSerializer.ParseRequired("a/b"));
    }

    [Fact]
    public void EquivalentLegacyFormsNormalizeToOneIdentity()
    {
        Assert.True(WorkspaceTargetCompatibility.AreEquivalent("abc", "instance:abc"));
        Assert.Equal("abc", WorkspaceTargetCompatibility.Normalize("instance:abc"));
        Assert.Null(WorkspaceTargetCompatibility.Normalize(null));
    }

    [Fact]
    public void UnprovableEquivalenceIsNotMerged()
    {
        Assert.False(WorkspaceTargetCompatibility.AreEquivalent("abc", "instance:v1:abc"));
        Assert.False(WorkspaceTargetCompatibility.AreEquivalent("abc", "a/b"));
    }

    [Fact]
    public void ResourceWithNonAsciiNormalizationIsStableAcrossRuns()
    {
        var raw = "İstanbul".Normalize(NormalizationForm.FormD);

        var first = WorkspaceTargetSerializer.ParseRequired(raw).Resource;
        var second = WorkspaceTargetSerializer.ParseRequired(first!).Resource;

        Assert.Equal(first, second);
    }
}
