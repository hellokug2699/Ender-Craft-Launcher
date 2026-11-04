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
    public void UnrecognizedGameVersionHasNoInventedRequirement()
    {
        Assert.Null(JavaRequirements.TryInferMinimumMajor("custom-build"));
        Assert.Equal(21, JavaRequirements.Resolve(new MinecraftVersion { Id = "custom-build" }, "custom-build").MinimumMajor);
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
    public void SelectsFirstCompatibleRuntimeInConfiguredPriorityOrder()
    {
        // The order is intentional: Java 21 has priority over Java 17 even though both work.
        var runtimes = new[] { Runtime("8.0.412"), Runtime("21.0.7"), Runtime("17.0.15") };

        Assert.Equal(21, JavaSelector.Select(runtimes, new JavaRequirement(17))?.Version?.Major);
        Assert.Equal(8, JavaSelector.Select(runtimes, new JavaRequirement(8))?.Version?.Major);
        Assert.Equal(21, JavaSelector.Select(runtimes, new JavaRequirement(21))?.Version?.Major);
        Assert.Null(JavaSelector.Select(runtimes, new JavaRequirement(25)));
    }

    [Fact]
    public void SkipsKnown32BitRuntimeDuringSelection()
    {
        var java17x86 = Runtime("17.0.15", "x86");
        var java17x64 = Runtime("17.0.15", "x86_64");

        Assert.Equal(
            JavaCompatibilityStatus.UnsupportedArchitecture,
            JavaCompatibility.Evaluate(new JavaRequirement(17), java17x86).Status);
        Assert.Equal(java17x64, JavaSelector.Select([java17x86, java17x64], new JavaRequirement(17)));
    }

    private static JavaRuntime Runtime(string version, string architecture = "x86_64")
    {
        var parsed = JavaVersion.TryParse(version)!;
        return new JavaRuntime(
            Path.Combine(Path.GetTempPath(), $"java-{version}-{architecture}"),
            Path.Combine(Path.GetTempPath(), $"java-{version}-{architecture}", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"),
            "Test",
            architecture,
            parsed);
    }
}
