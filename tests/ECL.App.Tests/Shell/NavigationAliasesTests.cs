// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Shell;
using Xunit;

namespace Ecl.App.Tests.Shell;

/// <summary>
/// 旧导航 Key 与 <c>ECL_START_PAGE</c> 兼容（迁移规划 §5.8）：
/// 别名只在同名 Key 不存在时生效，解析失败必须回退并报告，不得白屏。
/// </summary>
public sealed class NavigationAliasesTests
{
    private static readonly string[] TargetNavigation = ["home", "instances", "download", "settings", "tools", "storage", "logs", "about"];

    [Fact]
    public void LegacyJavaKeyRedirectsToSettingsAfterJavaDestinationIsRemoved()
    {
        Assert.True(NavigationAliases.TryResolve("java", TargetNavigation, out var key));
        Assert.Equal("settings", key);
    }

    [Fact]
    public void ExistingKeyWinsOverAliasDuringMigration()
    {
        string[] migrationNavigation = ["home", "settings", "java"];

        Assert.True(NavigationAliases.TryResolve("java", migrationNavigation, out var key));
        Assert.Equal("java", key);
    }

    [Theory]
    [InlineData("HOME")]
    [InlineData("  home  ")]
    public void ResolutionIsCaseInsensitiveAndTrimmed(string raw)
    {
        Assert.True(NavigationAliases.TryResolve(raw, TargetNavigation, out var key));
        Assert.Equal("home", key);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownKeyIsNotResolved(string? raw)
    {
        Assert.False(NavigationAliases.TryResolve(raw, TargetNavigation, out var key));
        Assert.Equal(string.Empty, key);
    }

    [Fact]
    public void UnsetStartPageUsesFirstDestinationWithoutFallback()
    {
        var page = NavigationAliases.ResolveStartPage(null, TargetNavigation, out var usedFallback);

        Assert.Equal("home", page);
        Assert.False(usedFallback);
    }

    [Fact]
    public void UnknownStartPageFallsBackToFirstDestinationAndReportsIt()
    {
        var page = NavigationAliases.ResolveStartPage("does-not-exist", TargetNavigation, out var usedFallback);

        Assert.Equal("home", page);
        Assert.True(usedFallback);
    }

    [Fact]
    public void LegacyStartPageResolvesThroughAlias()
    {
        var page = NavigationAliases.ResolveStartPage("java", TargetNavigation, out var usedFallback);

        Assert.Equal("settings", page);
        Assert.False(usedFallback);
    }

    [Fact]
    public void EmptyNavigationListIsRejected()
    {
        Assert.Throws<ArgumentException>(() => NavigationAliases.ResolveStartPage("home", [], out _));
    }
}
