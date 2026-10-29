// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Java;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class CustomJvmPresetTests
{
    [Fact]
    public void ParsesArrayPreset()
    {
        var result = CustomJvmPresetLoader.Parse("""
            ["-XX:+UseG1GC", "-XX:MaxGCPauseMillis=200"]
            """, "my-preset");

        Assert.True(result.IsSuccess);
        Assert.Equal("my-preset", result.Value!.Name);
        Assert.Equal(2, result.Value.Arguments.Count);
    }

    [Fact]
    public void ParsesNamedObjectPreset()
    {
        var result = CustomJvmPresetLoader.Parse("""
            {
              "name": "My preset",
              "arguments": ["-XX:+UseZGC", "-XX:+AlwaysPreTouch"]
            }
            """);

        Assert.True(result.IsSuccess);
        Assert.Equal("My preset", result.Value!.Name);
        Assert.Contains("-XX:+UseZGC", result.Value.Arguments);
    }

    [Theory]
    [InlineData("[\"-Xmx8G\"]")]
    [InlineData("[\"-Xms1G\"]")]
    [InlineData("[\"-cp\", \"a.jar\"]")]
    [InlineData("[]")]
    [InlineData("{\"name\":\"x\"}")]
    public void RejectsUnsafeOrEmptyPresets(string json)
    {
        var result = CustomJvmPresetLoader.Parse(json);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void LoadRejectsMissingFile()
    {
        var result = CustomJvmPresetLoader.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));

        Assert.False(result.IsSuccess);
        Assert.Equal("java.preset_missing", result.Error?.Code);
    }
}
