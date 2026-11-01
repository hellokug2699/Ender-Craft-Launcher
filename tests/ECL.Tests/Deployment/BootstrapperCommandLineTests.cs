// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class BootstrapperCommandLineTests
{
    [Fact]
    public void LaunchWithoutFlagsKeepsApplicationArguments()
    {
        var result = BootstrapperCommandLine.Parse(["curve", "1.20.1"]);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(BootstrapperAction.Launch, result.Value!.Action);
        Assert.Null(result.Value.InstallRoot);
        Assert.Equal(["curve", "1.20.1"], result.Value.ApplicationArguments);
    }

    [Fact]
    public void InstallRootIsParsedAndRemovedFromApplicationArguments()
    {
        var result = BootstrapperCommandLine.Parse(["--install-root", @"C:\Games\ECL"]);

        Assert.Equal(@"C:\Games\ECL", result.Value!.InstallRoot);
        Assert.Empty(result.Value.ApplicationArguments);
    }

    [Fact]
    public void ApplyRequiresManifestSignatureAndPackage()
    {
        var result = BootstrapperCommandLine.Parse(["--apply", "--manifest", "m.json"]);

        Assert.False(result.IsSuccess);
        Assert.Equal(BootstrapperCommandLine.ErrorCode, result.Error!.Code);
        Assert.Contains("--manifest", result.Error.Message);
    }

    [Fact]
    public void ApplyParsesDeploymentInputs()
    {
        var result = BootstrapperCommandLine.Parse(
        [
            "--apply",
            "--manifest", "m.json",
            "--signature", "m.sig",
            "--package", "pkg.zip",
            "--trust", "trust.json",
            "--deploy-only",
        ]);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var command = result.Value!;
        Assert.Equal(BootstrapperAction.Apply, command.Action);
        Assert.NotNull(command.Deploy);
        Assert.Equal("m.json", command.Deploy!.ManifestPath);
        Assert.Equal("m.sig", command.Deploy.SignaturePath);
        Assert.Equal("pkg.zip", command.Deploy.PackagePath);
        Assert.Equal("trust.json", command.Deploy.TrustFile);
        Assert.True(command.DeployOnly);
    }

    [Fact]
    public void ApplyCannotBeCombinedWithRollback()
    {
        var result = BootstrapperCommandLine.Parse(
            ["--apply", "--manifest", "m", "--signature", "s", "--package", "p", "--rollback"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("--rollback", result.Error!.Message);
    }

    [Fact]
    public void RollbackAndCleanupAreMutuallyExclusive()
    {
        var result = BootstrapperCommandLine.Parse(["--rollback", "--cleanup"]);

        Assert.False(result.IsSuccess);
        Assert.Equal(BootstrapperCommandLine.ErrorCode, result.Error!.Code);
    }

    [Fact]
    public void CleanupSelectsCleanupAction()
    {
        var result = BootstrapperCommandLine.Parse(["--cleanup"]);

        Assert.Equal(BootstrapperAction.Cleanup, result.Value!.Action);
    }

    [Fact]
    public void MissingValueIsRejected()
    {
        var result = BootstrapperCommandLine.Parse(["--manifest"]);

        Assert.False(result.IsSuccess);
        Assert.Equal(BootstrapperCommandLine.ErrorCode, result.Error!.Code);
    }

    [Fact]
    public void EmptyValueIsRejected()
    {
        var result = BootstrapperCommandLine.Parse(["--package", "  "]);

        Assert.False(result.IsSuccess);
        Assert.Equal(BootstrapperCommandLine.ErrorCode, result.Error!.Code);
    }

    [Fact]
    public void HelpIsRecognized()
    {
        var result = BootstrapperCommandLine.Parse(["--help"]);

        Assert.True(result.Value!.Help);
        Assert.Equal(BootstrapperAction.Launch, result.Value.Action);
    }
}
