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
    public void LegacyLwjglIsOnlyDroppedForCleanroom()
    {
        var libraries = new[] { Library("org.lwjgl.lwjgl:lwjgl:2.9.4-nightly-20150209") };

        Assert.Same(libraries, LaunchLibraryFilter.Apply("forge", libraries));
        Assert.Same(libraries, LaunchLibraryFilter.Apply("vanilla", libraries));
        Assert.Same(libraries, LaunchLibraryFilter.Apply(null, libraries));
    }

    /// <summary>
    /// JNA 4 把 <c>net.java.dev.jna:platform</c> 改名为 <c>jna-platform</c>，两者提供同一批
    /// <c>com.sun.jna.platform.*</c> 类。旧构件先进入 classpath 会让 Cleanroom 的 Taskbar API
    /// 报 <c>NoSuchMethodError: Guid$GUID.&lt;init&gt;(String)</c>，因此必须剔除旧构件名。
    /// </summary>
    [Fact]
    public void LegacyJnaPlatformIsDroppedWhenJnaPlatformExists()
    {
        var libraries = new[]
        {
            Library("net.java.dev.jna:jna:4.4.0"),
            Library("net.java.dev.jna:platform:3.4.0"),
            Library("net.java.dev.jna:jna:5.19.1"),
            Library("net.java.dev.jna:jna-platform:5.19.1"),
        };

        var filtered = LaunchLibraryFilter.Apply("cleanroom", libraries);

        Assert.DoesNotContain(filtered, library => library.Name == "net.java.dev.jna:platform:3.4.0");
        Assert.Contains(filtered, library => library.Name == "net.java.dev.jna:jna-platform:5.19.1");
        Assert.Contains(filtered, library => library.Name == "net.java.dev.jna:jna:4.4.0");
    }

    [Fact]
    public void LegacyJnaPlatformIsDroppedForEveryLoader()
    {
        var libraries = new[]
        {
            Library("net.java.dev.jna:platform:3.4.0"),
            Library("net.java.dev.jna:jna-platform:5.19.1"),
        };

        foreach (var loader in new[] { "forge", "vanilla", "fabric", null })
        {
            var filtered = LaunchLibraryFilter.Apply(loader, libraries);
            Assert.DoesNotContain(filtered, library => library.Name == "net.java.dev.jna:platform:3.4.0");
        }
    }

    [Fact]
    public void LegacyJnaPlatformIsKeptWhenNoSuccessorExists()
    {
        var libraries = new[] { Library("net.java.dev.jna:platform:3.4.0") };

        // 没有 jna-platform 时不能把唯一可用的库删掉。
        Assert.Same(libraries, LaunchLibraryFilter.Apply("cleanroom", libraries));
    }

    [Theory]
    [InlineData("net.java.dev.jna:platform:3.4.0", true)]
    [InlineData("net.java.dev.jna:jna-platform:5.19.1", false)]
    [InlineData("net.java.dev.jna:jna:4.4.0", false)]
    [InlineData("", false)]
    public void DetectsLegacyJnaPlatformCoordinates(string name, bool expected) =>
        Assert.Equal(expected, LaunchLibraryFilter.IsLegacyJnaPlatform(name));

    /// <summary>
    /// 原版 1.12.2 带裁剪版 <c>com.ibm.icu:icu4j-core-mojang:51.2</c>（无 brkitr 断行数据），
    /// Cleanroom 带完整 <c>com.ibm.icu:icu4j:78.3</c>。两者构件名不同，按坐标去重抓不到；
    /// 裁剪版先被命中时 Cleanroom 的 <c>ICU4JInstances</c> 会抛
    /// <c>MissingResourceException: .../icudt51b/brkitr/en_US</c>，启动在模型烘焙期就崩。
    /// </summary>
    [Fact]
    public void MojangIcu4jIsDroppedWhenFullIcu4jExists()
    {
        var libraries = new[]
        {
            Library("com.ibm.icu:icu4j-core-mojang:51.2"),
            Library("com.ibm.icu:icu4j:78.3"),
        };

        var filtered = LaunchLibraryFilter.Apply("cleanroom", libraries);

        var library = Assert.Single(filtered);
        Assert.Equal("com.ibm.icu:icu4j:78.3", library.Name);
    }

    [Fact]
    public void MojangIcu4jIsDroppedForEveryLoader()
    {
        var libraries = new[]
        {
            Library("com.ibm.icu:icu4j-core-mojang:51.2"),
            Library("com.ibm.icu:icu4j:78.3"),
        };

        foreach (var loader in new[] { "forge", "vanilla", "fabric", "cleanroom", null })
        {
            var filtered = LaunchLibraryFilter.Apply(loader, libraries);
            Assert.DoesNotContain(filtered, library => library.Name.Contains("icu4j-core-mojang", StringComparison.Ordinal));
            Assert.Contains(filtered, library => library.Name == "com.ibm.icu:icu4j:78.3");
        }
    }

    [Fact]
    public void MojangIcu4jIsKeptWhenNoFullIcu4jExists()
    {
        var libraries = new[] { Library("com.ibm.icu:icu4j-core-mojang:51.2") };

        // 原版 1.12.2 单独使用时该库仍然需要，不能把唯一可用的库删掉。
        Assert.Same(libraries, LaunchLibraryFilter.Apply("vanilla", libraries));
    }

    [Theory]
    [InlineData("com.ibm.icu:icu4j-core-mojang:51.2", true)]
    [InlineData("com.ibm.icu:icu4j:78.3", false)]
    [InlineData("com.ibm.icu:icu4j:73.2", false)]
    [InlineData("", false)]
    public void DetectsMojangIcu4jCoordinates(string name, bool expected) =>
        Assert.Equal(expected, LaunchLibraryFilter.IsMojangIcu4j(name));

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
