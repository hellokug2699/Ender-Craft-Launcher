// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class BootstrapperExitCodesTests
{
    [Theory]
    [InlineData("update.trust_missing", 3)]
    [InlineData("update.unknown_key", 3)]
    [InlineData("update.signature_invalid", 3)]
    [InlineData("update.trust_invalid", 3)]
    [InlineData("update.executable_missing", 5)]
    [InlineData("update.launch_failed", 5)]
    [InlineData("bootstrapper.arguments", 2)]
    [InlineData("update.package_hash_mismatch", 4)]
    [InlineData("update.file_hash_mismatch", 4)]
    [InlineData("update.path_traversal", 4)]
    [InlineData("anything.else", 4)]
    public void MapsErrorCodesToStableExitCodes(string code, int expected)
    {
        var exitCode = BootstrapperExitCodes.FromError(new EclError(code, "test"));

        Assert.Equal(expected, exitCode);
    }

    [Fact]
    public void NoErrorMeansSuccess()
    {
        Assert.Equal(0, BootstrapperExitCodes.FromError(null));
    }

    [Fact]
    public void ContractValuesAreStable()
    {
        Assert.Equal(0, BootstrapperExitCodes.Success);
        Assert.Equal(2, BootstrapperExitCodes.ArgumentError);
        Assert.Equal(3, BootstrapperExitCodes.TrustError);
        Assert.Equal(4, BootstrapperExitCodes.DeployError);
        Assert.Equal(5, BootstrapperExitCodes.LaunchError);
    }
}
