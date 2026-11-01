// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class InstallerTests : IDisposable
{
    private readonly string _root;

    public InstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ecl-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string CreateMedia(params string[] omit)
    {
        var directory = Path.Combine(_root, "media-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        foreach (var file in new[]
                 {
                     "ECL.Bootstrapper.exe",
                     "update-trust.json",
                     "update-manifest.json",
                     "update-manifest.sig",
                     InstallMedia.DefaultPackageName,
                 })
        {
            if (!omit.Contains(file))
            {
                File.WriteAllText(Path.Combine(directory, file), "x");
            }
        }

        return directory;
    }

    [Fact]
    public void CompleteMediaIsAccepted()
    {
        var directory = CreateMedia();

        var result = InstallMedia.Inspect(directory);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(
            Path.Combine(directory, InstallMedia.DefaultPackageName),
            result.Value!.PackagePath);
    }

    [Theory]
    [InlineData("ECL.Bootstrapper.exe")]
    [InlineData("update-trust.json")]
    [InlineData("update-manifest.json")]
    [InlineData("update-manifest.sig")]
    [InlineData(InstallMedia.DefaultPackageName)]
    public void MissingMediaFileIsRejected(string omitted)
    {
        var directory = CreateMedia(omitted);

        var result = InstallMedia.Inspect(directory);

        Assert.False(result.IsSuccess);
        Assert.StartsWith("installer.media.", result.Error!.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitPackagePathIsHonored()
    {
        var directory = CreateMedia(InstallMedia.DefaultPackageName);
        var custom = Path.Combine(directory, "custom.zip");
        File.WriteAllText(custom, "x");

        var result = InstallMedia.Inspect(directory, custom);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(custom, result.Value!.PackagePath);
    }

    [Fact]
    public void MissingMediaDirectoryIsRejected()
    {
        var result = InstallMedia.Inspect(Path.Combine(_root, "does-not-exist"));

        Assert.False(result.IsSuccess);
        Assert.Equal("installer.media.missing", result.Error!.Code);
    }

    [Fact]
    public async Task RelativeInstallRootIsRejectedBeforeDeployment()
    {
        var media = InstallMedia.Inspect(CreateMedia()).Value!;
        var installer = new InstallerService();

        var result = await installer.InstallAsync(new InstallOptions("relative/path", media));

        Assert.False(result.IsSuccess);
        Assert.Equal("installer.target.relative", result.Error!.Code);
    }

    [Fact]
    public async Task DriveRootInstallRootIsRejected()
    {
        var media = InstallMedia.Inspect(CreateMedia()).Value!;
        var installer = new InstallerService();

        var result = await installer.InstallAsync(new InstallOptions(Path.GetPathRoot(_root)!, media));

        Assert.False(result.IsSuccess);
        Assert.Equal("installer.target.root", result.Error!.Code);
    }

    [Fact]
    public void CleanupStagingRemovesOnlyInterruptedStaging()
    {
        var layout = new UpdateLayout(Path.Combine(_root, "install"));
        layout.EnsureCreated();
        var staging = Path.Combine(layout.StagingDirectory, "1.0.1-abc");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "payload.bin"), "partial");
        var version = layout.GetVersionDirectory(ReleaseVersion.Parse("1.0.0"));
        Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(version, "ECL.exe"), "current");
        File.WriteAllText(layout.CurrentFile, """{ "version": "1.0.0", "executable": "ECL.exe" }""");

        var removed = new UpdateInstaller(layout).CleanupStaging();

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(staging));
        Assert.True(Directory.Exists(version));
        Assert.True(File.Exists(layout.CurrentFile));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }
}
