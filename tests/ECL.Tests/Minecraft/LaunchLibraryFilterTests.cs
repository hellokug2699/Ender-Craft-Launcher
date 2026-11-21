// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Versions;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// Cleanroom 启动库过滤：导入的整合包 JSON 可能同时带有 LWJGL 2 与 LWJGL 3，
/// 两套 org.lwjgl 同入 classpath 会让 org.lwjgl.Sys 加载不匹配的原生库并报
/// NoSuchMethodError: getPointer。Cleanroom 必须只保留 LWJGL 3 + lwjglxx。
/// </summary>
public sealed class LaunchLibraryFilterTests
{
    [Fact]
    public void CleanroomDropsLegacyLwjglAndKeepsModernLibraries()
    {
        var libraries = new[]
        {
            Library("org.lwjgl.lwjgl:lwjgl:2.9.4-nightly-20150209"),
            Library("org.lwjgl.lwjgl:lwjgl_util:2.9.4-nightly-20150209"),
            Library("org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209"),
            Library("org.lwjgl:lwjgl:3.4.1-unsafe"),
            Library("org.lwjgl:lwjgl:3.4.1:natives-windows"),
            Library("com.cleanroommc:lwjglxx:1.1.22"),
            Library("com.paulscode:librarylwjglopenal:20100824"),
        };

        var filtered = LaunchLibraryFilter.Apply("cleanroom", libraries);

        Assert.DoesNotContain(
            filtered,
            library => library.Name.StartsWith("org.lwjgl.lwjgl:", StringComparison.Ordinal));
        Assert.Contains(filtered, library => library.Name == "org.lwjgl:lwjgl:3.4.1-unsafe");
        Assert.Contains(filtered, library => library.Name == "org.lwjgl:lwjgl:3.4.1:natives-windows");
        Assert.Contains(filtered, library => library.Name == "com.cleanroommc:lwjglxx:1.1.22");
        Assert.Contains(filtered, library => library.Name == "com.paulscode:librarylwjglopenal:20100824");
    }

    [Fact]
    public void NonCleanroomLoadersKeepLegacyLwjgl()
    {
        var libraries = new[] { Library("org.lwjgl.lwjgl:lwjgl:2.9.4-nightly-20150209") };

        Assert.Same(libraries, LaunchLibraryFilter.Apply("forge", libraries));
        Assert.Same(libraries, LaunchLibraryFilter.Apply("vanilla", libraries));
        Assert.Same(libraries, LaunchLibraryFilter.Apply(null, libraries));
    }

    [Theory]
    [InlineData("org.lwjgl.lwjgl:lwjgl:2.9.4-nightly-20150209", true)]
    [InlineData("org.lwjgl.lwjgl:lwjgl_util:2.9.4-nightly-20150209", true)]
    [InlineData("org.lwjgl:lwjgl:2.9.4", true)]
    [InlineData("org.lwjgl:lwjgl:3.4.1", false)]
    [InlineData("org.lwjgl:lwjgl:3.4.1:natives-windows", false)]
    [InlineData("com.cleanroommc:lwjglxx:1.1.22", false)]
    [InlineData("", false)]
    public void DetectsLegacyLwjglCoordinates(string name, bool expected) =>
        Assert.Equal(expected, LaunchLibraryFilter.IsLegacyLwjgl(name));

    private static VersionLibrary Library(string name) => new() { Name = name };
}
