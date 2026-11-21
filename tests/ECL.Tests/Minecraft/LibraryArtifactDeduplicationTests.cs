// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Versions;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 同一构件的多个版本：只保留最高版本（2026-11-21 Cleanroom 启动失败的根因）。
/// 导入的整合包 JSON 会把原版库与加载器库合并，旧版本先进入类路径会遮蔽新版本，
/// 表现为 <c>NoSuchMethodError</c>（例如 log4j-slf4j2-impl 找不到
/// <c>LoggerContextFactory.isClassLoaderDependent()</c>）。
/// </summary>
public sealed class LibraryArtifactDeduplicationTests
{
    [Fact]
    public void KeepsOnlyTheHighestVersionOfTheSameArtifact()
    {
        var libraries = new[]
        {
            Library("org.apache.logging.log4j:log4j-api:2.8.1"),
            Library("org.apache.logging.log4j:log4j-core:2.8.1"),
            Library("org.apache.logging.log4j:log4j-api:2.26.0"),
            Library("org.apache.logging.log4j:log4j-core:2.26.0"),
            Library("org.apache.logging.log4j:log4j-slf4j2-impl:2.26.0"),
            Library("org.slf4j:slf4j-api:2.0.18"),
        };

        var classpath = Classpath(libraries);

        Assert.Equal(
            [
                "org.apache.logging.log4j:log4j-api:2.26.0",
                "org.apache.logging.log4j:log4j-core:2.26.0",
                "org.apache.logging.log4j:log4j-slf4j2-impl:2.26.0",
                "org.slf4j:slf4j-api:2.0.18",
            ],
            classpath);
        Assert.DoesNotContain(classpath, name => name.Contains(":2.8.1", StringComparison.Ordinal));
    }

    [Fact]
    public void HighestVersionWinsRegardlessOfDeclarationOrder()
    {
        var libraries = new[]
        {
            Library("org.apache.logging.log4j:log4j-api:2.26.0"),
            Library("org.apache.httpcomponents:httpclient:4.5.13"),
            Library("org.apache.logging.log4j:log4j-api:2.8.1"),
            Library("org.apache.httpcomponents:httpclient:4.3.3"),
        };

        var classpath = Classpath(libraries);

        Assert.Contains("org.apache.logging.log4j:log4j-api:2.26.0", classpath);
        Assert.Contains("org.apache.httpcomponents:httpclient:4.5.13", classpath);
        Assert.Equal(2, classpath.Count);
    }

    [Fact]
    public void MultiDigitVersionSegmentsCompareNumerically()
    {
        // 字符串比较会把 2.8.1 判为更高，这正是旧行为无法自愈的原因。
        Assert.True(MavenVersionComparer.Compare("2.26.0", "2.8.1") > 0);
        Assert.True(MavenVersionComparer.Compare("2.8.1", "2.26.0") < 0);
        Assert.True(MavenVersionComparer.Compare("3.4.1", "3.2.2") > 0);
        Assert.Equal(0, MavenVersionComparer.Compare("2.26.0", "2.26.0"));
        Assert.True(MavenVersionComparer.Compare("1.0.0", "1.0.0-SNAPSHOT") > 0);
        Assert.True(MavenVersionComparer.Compare("1.0", "1.0.1") < 0);
    }

    [Fact]
    public void NativeClassifiersAreNotTreatedAsDuplicateOfThePlainArtifact()
    {
        var libraries = new[]
        {
            Library("org.lwjgl:lwjgl:3.4.1"),
            NativeLibrary("org.lwjgl:lwjgl:3.4.1:natives-windows", "windows"),
            NativeLibrary("org.lwjgl:lwjgl:3.4.1:natives-windows-x86", "x86"),
        };

        var plan = PlatformLibrarySelector.Plan(libraries, "windows", "x86_64");
        var names = plan.Select(item => item.Library.Name).ToArray();

        Assert.Contains("org.lwjgl:lwjgl:3.4.1", names);
        Assert.Contains("org.lwjgl:lwjgl:3.4.1:natives-windows", names);
        Assert.Single(plan, item => item.IsNativeOnly);
    }

    [Fact]
    public void UnrelatedArtifactsKeepTheirOrderAndQuantity()
    {
        var libraries = new[]
        {
            Library("com.mojang:patchy:1.3.9"),
            Library("net.java.dev.jna:jna:4.4.0"),
            Library("com.mojang:text2speech:1.10.3"),
        };

        var classpath = Classpath(libraries);

        Assert.Equal(
            ["com.mojang:patchy:1.3.9", "net.java.dev.jna:jna:4.4.0", "com.mojang:text2speech:1.10.3"],
            classpath);
    }

    private static List<string> Classpath(IEnumerable<VersionLibrary> libraries) =>
        PlatformLibrarySelector
            .Plan(libraries, "windows", "x86_64")
            .Where(item => !item.IsNativeOnly)
            .Select(item => item.Library.Name)
            .ToList();

    private static VersionLibrary Library(string name) => new()
    {
        Name = name,
        Downloads = new VersionLibraryDownloads
        {
            Artifact = new VersionLibraryArtifact { Path = ToPath(name) },
        },
    };

    private static VersionLibrary NativeLibrary(string name, string os) => new()
    {
        Name = name,
        Downloads = new VersionLibraryDownloads
        {
            Artifact = new VersionLibraryArtifact { Path = ToPath(name) },
        },
        Rules = [new VersionRule { Action = "allow", Os = new VersionRuleOs { Name = os } }],
    };

    private static string ToPath(string name)
    {
        var parts = name.Split(':');
        var group = parts[0].Replace('.', '/');
        var classifier = parts.Length >= 4 ? $"-{parts[3]}" : string.Empty;
        return $"{group}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}{classifier}.jar";
    }
}
