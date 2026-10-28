// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Java;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class JvmPresetTests
{
    [Fact]
    public void DefaultPresetAddsNoArguments()
    {
        Assert.Empty(JvmPresets.GetArguments(JvmPreset.Default, new JavaVersion(21, 0, 0, "21")));
    }

    [Fact]
    public void G1AndAikarPresetsDoNotOverrideMemory()
    {
        var g1 = JvmPresets.GetArguments(JvmPreset.G1, new JavaVersion(21, 0, 0, "21"));
        var aikar = JvmPresets.GetArguments(JvmPreset.Aikar, new JavaVersion(21, 0, 0, "21"));

        Assert.Contains("-XX:+UseG1GC", g1);
        Assert.Contains("-XX:+UseG1GC", aikar);
        Assert.Contains("-XX:G1NewSizePercent=30", aikar);
        Assert.DoesNotContain(g1, argument => argument.StartsWith("-Xmx", StringComparison.Ordinal));
        Assert.DoesNotContain(aikar, argument => argument.StartsWith("-Xmx", StringComparison.Ordinal));
        Assert.DoesNotContain(aikar, argument => argument.StartsWith("-Xms", StringComparison.Ordinal));
    }

    [Fact]
    public void ZgcUsesGenerationalFlagOnlyOnJava21To23()
    {
        var java21 = JvmPresets.GetArguments(JvmPreset.Zgc, new JavaVersion(21, 0, 0, "21"));
        var java23 = JvmPresets.GetArguments(JvmPreset.Zgc, new JavaVersion(23, 0, 0, "23"));
        var java24 = JvmPresets.GetArguments(JvmPreset.Zgc, new JavaVersion(24, 0, 0, "24"));
        var java17 = JvmPresets.GetArguments(JvmPreset.Zgc, new JavaVersion(17, 0, 0, "17"));
        var java8 = JvmPresets.GetArguments(JvmPreset.Zgc, new JavaVersion(8, 0, 0, "1.8"));

        Assert.Contains("-XX:+UseZGC", java21);
        Assert.Contains("-XX:+ZGenerational", java21);
        Assert.Contains("-XX:+UseZGC", java23);
        Assert.Contains("-XX:+ZGenerational", java23);
        Assert.Contains("-XX:+UseZGC", java24);
        Assert.DoesNotContain("-XX:+ZGenerational", java24);
        Assert.Contains("-XX:+UseZGC", java17);
        Assert.DoesNotContain("-XX:+ZGenerational", java17);
        Assert.Empty(java8);
    }

    [Theory]
    [InlineData("g1", JvmPreset.G1)]
    [InlineData("ZGC", JvmPreset.Zgc)]
    [InlineData("aikar", JvmPreset.Aikar)]
    [InlineData("", JvmPreset.Default)]
    [InlineData(null, JvmPreset.Default)]
    [InlineData("unknown", JvmPreset.Default)]
    public void ParsesPresetIds(string? value, JvmPreset expected)
    {
        Assert.Equal(expected, JvmPresets.Parse(value));
    }
}
