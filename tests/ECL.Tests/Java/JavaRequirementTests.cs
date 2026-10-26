// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Java;
using Ecl.Minecraft.Versions;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class JavaRequirementTests
{
    [Theory]
    [InlineData("1.16.5", 8, 8)]
    [InlineData("1.17.1", 16, null)]
    [InlineData("1.20.1", 17, null)]
    [InlineData("1.20.5", 21, null)]
    [InlineData("1.21.1", 21, null)]
    public void InfersJavaRequirementFromGameVersion(string gameVersion, int minimum, int? maximum)
    {
        var requirement = JavaRequirements.Resolve(new MinecraftVersion { Id = gameVersion }, gameVersion);

        Assert.Equal(minimum, requirement.MinimumMajor);
        Assert.Equal(maximum, requirement.MaximumMajor);
    }

    [Fact]
    public void PrefersDeclaredJavaVersion()
    {
        var version = new MinecraftVersion
        {
            Id = "test",
            JavaVersion = new VersionJavaRequirement
            {
                Component = "java-runtime-gamma",
                MajorVersion = 17,
            },
        };

        var requirement = JavaRequirements.Resolve(version, "1.0");

        Assert.Equal(17, requirement.MinimumMajor);
        Assert.Null(requirement.MaximumMajor);
        Assert.Equal("java-runtime-gamma", requirement.Component);
    }

    [Fact]
    public void DetectsTooOldAndTooNew()
    {
        var requirement = new JavaRequirement(17, "java-runtime-gamma", null);
        var java8 = Runtime("8.0.412");
        var java17 = Runtime("17.0.15");
        var java21 = Runtime("21.0.7");

        Assert.Equal(JavaCompatibilityStatus.TooOld, JavaCompatibility.Evaluate(requirement, java8).Status);
        Assert.Equal(JavaCompatibilityStatus.Compatible, JavaCompatibility.Evaluate(requirement, java17).Status);
        Assert.Equal(JavaCompatibilityStatus.Compatible, JavaCompatibility.Evaluate(requirement, java21).Status);

        var legacy = new JavaRequirement(8, null, 8);
        Assert.Equal(JavaCompatibilityStatus.TooNew, JavaCompatibility.Evaluate(legacy, java17).Status);
    }

    [Fact]
    public void SelectsClosestCompatibleRuntime()
    {
        var runtimes = new[] { Runtime("8.0.412"), Runtime("17.0.15"), Runtime("21.0.7") };

        Assert.Equal(17, JavaSelector.Select(runtimes, new JavaRequirement(17))?.Version?.Major);
        Assert.Equal(8, JavaSelector.Select(runtimes, new JavaRequirement(8, null, 8))?.Version?.Major);
        Assert.Equal(21, JavaSelector.Select(runtimes, new JavaRequirement(21))?.Version?.Major);
        Assert.Null(JavaSelector.Select(runtimes, new JavaRequirement(11, null, 11)));
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
