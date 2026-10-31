// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class UpdateCompatibilityTests
{
    [Fact]
    public void RejectsOlderUpdaterAndWrongArchitecture()
    {
        var manifest = DeploymentTestData.CreateManifest(
            "1.0.1",
            "ECL.zip",
            1,
            new string('a', 64),
            minimumUpdaterVersion: "2.0.0",
            architecture: UpdateArchitecture.Arm64);

        var oldUpdater = UpdateCompatibility.EnsureCompatible(
            manifest,
            ReleaseVersion.Parse("1.0.0"),
            UpdateArchitecture.Arm64);
        Assert.Equal("update.updater_too_old", oldUpdater.Error?.Code);

        var wrongArch = UpdateCompatibility.EnsureCompatible(
            manifest,
            ReleaseVersion.Parse("3.0.0"),
            UpdateArchitecture.X64);
        Assert.Equal("update.architecture_mismatch", wrongArch.Error?.Code);

        Assert.True(UpdateCompatibility.EnsureCompatible(
            manifest,
            ReleaseVersion.Parse("3.0.0"),
            UpdateArchitecture.Arm64).IsSuccess);
    }
}
