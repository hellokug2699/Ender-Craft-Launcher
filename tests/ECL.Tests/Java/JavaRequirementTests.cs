// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Java;
using Ecl.Minecraft.Versions;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class JavaRequirementTests
{
    [Theory]
    [InlineData("1.16.5", 8)]
    [InlineData("1.17.1", 16)]
    [InlineData("1.20.1", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.21.1", 21)]
    [InlineData("26.1", 25)]
    [InlineData("26.2.1", 25)]
    [InlineData("26w01a", 25)]
    public void InfersMinimumJavaFromGameVersion(string gameVersion, int minimum)
    {
        var requirement = JavaRequirements.Resolve(new MinecraftVersion { Id = gameVersion }, gameVersion);

        Assert.Equal(minimum, requirement.MinimumMajor);
    }

    [Fact]
    public void PrefersDeclaredJavaVersionForFutureVersions()
    {
        var version = new MinecraftVersion
        {
            Id = "future",
            JavaVersion = new VersionJavaRequirement
            {
                Component = "java-runtime-epsilon",
                MajorVersion = 26,
            },
        };

        var requirement = JavaRequirements.Resolve(version, "26.1");

        Assert.Equal(26, requirement.MinimumMajor);
        Assert.Equal("java-runtime-epsilon", requirement.Component);
    }

    [Fact]
    public void OnlyChecksMinimumVersion()
    {
        var requirement = new JavaRequirement(17, "java-runtime-gamma");
        var java8 = Runtime("8.0.412");
        var java17 = Runtime("17.0.15");
        var java21 = Runtime("21.0.7");
        var java26 = Runtime("26.0.1");

        Assert.Equal(JavaCompatibilityStatus.TooOld, JavaCompatibility.Evaluate(requirement, java8).Status);
        Assert.Equal(JavaCompatibilityStatus.Compatible, JavaCompatibility.Evaluate(requirement, java17).Status);
        Assert.Equal(JavaCompatibilityStatus.Compatible, JavaCompatibility.Evaluate(requirement, java21).Status);
        Assert.Equal(JavaCompatibilityStatus.Compatible, JavaCompatibility.Evaluate(requirement, java26).Status);

        var legacy = new JavaRequirement(8);
        Assert.Equal(JavaCompatibilityStatus.Compatible, JavaCompatibility.Evaluate(legacy, java17).Status);
        Assert.Equal(JavaCompatibilityStatus.Compatible, JavaCompatibility.Evaluate(legacy, java26).Status);
    }

    [Fact]
    public void SelectsClosestCompatibleRuntime()
    {
        var runtimes = new[] { Runtime("8.0.412"), Runtime("17.0.15"), Runtime("21.0.7") };

        Assert.Equal(17, JavaSelector.Select(runtimes, new JavaRequirement(17))?.Version?.Major);
        Assert.Equal(8, JavaSelector.Select(runtimes, new JavaRequirement(8))?.Version?.Major);
        Assert.Equal(21, JavaSelector.Select(runtimes, new JavaRequirement(21))?.Version?.Major);
        Assert.Equal(17, JavaSelector.Select(runtimes, new JavaRequirement(11))?.Version?.Major);
        Assert.Null(JavaSelector.Select(runtimes, new JavaRequirement(25)));
    }

    private static JavaRuntime Runtime(string version)
    {
        var parsed = JavaVersion.TryParse(version)!;
        return new JavaRuntime(
            Path.Combine(Path.GetTempPath(), $"java-{version}"),
            Path.Combine(Path.GetTempPath(), $"java-{version}", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"),
            "Test",
            "x86_64",
            parsed);
    }
}
