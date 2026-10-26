// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Java;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class JavaVersionTests
{
    [Theory]
    [InlineData("openjdk version \"21.0.4\" 2024-07-16 LTS", 21, 0, 4)]
    [InlineData("java version \"1.8.0_411\"", 8, 0, 0)]
    [InlineData("17.0.12", 17, 0, 12)]
    [InlineData("jdk-21.0.4+7", 21, 0, 4)]
    [InlineData("jdk8u412-b08", 8, 0, 0)]
    public void ParsesCommonFormats(string raw, int major, int minor, int patch)
    {
        var version = JavaVersion.TryParse(raw);

        Assert.NotNull(version);
        Assert.Equal(major, version!.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData(null)]
    public void RejectsInvalidValues(string? raw)
    {
        Assert.Null(JavaVersion.TryParse(raw));
    }

    [Fact]
    public void ComparesByVersion()
    {
        var java21 = JavaVersion.TryParse("21.0.1")!;
        var java17 = JavaVersion.TryParse("17.0.12")!;

        Assert.True(java21.CompareTo(java17) > 0);
        Assert.True(java17.IsAtLeast(17));
        Assert.False(java17.IsAtLeast(21));
    }
}

public sealed class JavaLocatorTests
{
    [Fact]
    public async Task FindsRuntimeInLauncherJavaDirectory()
    {
        using var environment = new TestEnvironment();
        var home = Path.Combine(environment.Paths.JavaDirectory, "jdk-21.0.4");
        var bin = Path.Combine(home, "bin");
        Directory.CreateDirectory(bin);

        var executable = Path.Combine(bin, OperatingSystem.IsWindows() ? "java.exe" : "java");
        await File.WriteAllTextAsync(executable, string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(home, "release"),
            "JAVA_VERSION=\"21.0.4\"\nOS_ARCH=\"x86_64\"\nIMPLEMENTOR=\"Eclipse Adoptium\"\n");

        await using var provider = environment.CreateServices();
        var locator = provider.GetRequiredService<IJavaLocator>();

        var runtime = (await locator.FindAllAsync())
            .FirstOrDefault(item => string.Equals(item.HomePath, home, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(runtime);
        Assert.Equal(21, runtime!.Version?.Major);
        Assert.Equal("Eclipse Adoptium", runtime.Vendor);
        Assert.True(runtime.Is64Bit);
        Assert.Equal(executable, runtime.ExecutablePath);

        var direct = locator.FromPath(home);
        Assert.NotNull(direct);
        Assert.Equal(executable, direct!.ExecutablePath);
    }
}
