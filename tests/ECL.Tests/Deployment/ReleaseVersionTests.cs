// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.8.0")]
    [InlineData("10.20.30")]
    [InlineData("1.0.1-beta.2")]
    [InlineData("1.0.1-rc.1+sha.abc")]
    public void ParsesSupportedVersions(string value)
    {
        Assert.True(ReleaseVersion.TryParse(value, out var version));
        Assert.NotNull(version);
        Assert.DoesNotContain('/', version.ToString());
        Assert.DoesNotContain('\\', version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("../1.0.0")]
    [InlineData("1.0.0\\evil")]
    public void RejectsInvalidOrUnsafeVersions(string value)
    {
        Assert.False(ReleaseVersion.TryParse(value, out _));
    }

    [Fact]
    public void OrdersStableAfterPreReleaseAndComparesNumbers()
    {
        Assert.True(ReleaseVersion.Parse("1.0.0").CompareTo(ReleaseVersion.Parse("1.0.0-rc.1")) > 0);
        Assert.True(ReleaseVersion.Parse("1.0.0-rc.2").CompareTo(ReleaseVersion.Parse("1.0.0-rc.1")) > 0);
        Assert.True(ReleaseVersion.Parse("1.0.0-beta").CompareTo(ReleaseVersion.Parse("1.0.0-alpha")) > 0);
        Assert.True(ReleaseVersion.Parse("1.0.10").CompareTo(ReleaseVersion.Parse("1.0.9")) > 0);
        Assert.True(ReleaseVersion.Parse("2.0.0").CompareTo(ReleaseVersion.Parse("1.99.99")) > 0);
    }
}
