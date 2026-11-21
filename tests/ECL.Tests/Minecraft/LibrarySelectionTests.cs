// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Versions;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class LibrarySelectionTests
{
    [Theory]
    [InlineData("x86_64", "natives-windows")]
    [InlineData("x86", "natives-windows-x86")]
    [InlineData("arm64", "natives-windows-arm64")]
    public void SelectsNativeVariantForArchitecture(string arch, string expectedClassifier)
    {
        var libraries = new[]
        {
            NormalLibrary(),
            NativeLibrary("natives-windows"),
            NativeLibrary("natives-windows-x86"),
            NativeLibrary("natives-windows-arm64"),
        };

        var plan = PlatformLibrarySelector.Plan(libraries, "windows", arch);

        var nativeNames = plan
            .Where(item => item.Library.Name.Contains("natives-", StringComparison.Ordinal))
            .Select(item => item.Library.Name)
            .ToArray();

        Assert.Single(nativeNames);
        Assert.EndsWith($":{expectedClassifier}", nativeNames[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// PCL 等启动器会用「OS-架构」组合 token（<c>osx-arm64</c>、<c>windows-arm64</c>）写规则；
    /// 以前这类 token 永远不匹配，规则被静默忽略。
    /// </summary>
    [Theory]
    [InlineData("osx-arm64", "osx", "arm64", true)]
    [InlineData("osx-arm64", "osx", "x86_64", false)]
    [InlineData("macos-arm64", "osx", "arm64", true)]
    [InlineData("windows-arm64", "windows", "arm64", true)]
    [InlineData("windows-arm64", "windows", "x86_64", false)]
    [InlineData("windows", "windows", "x86_64", true)]
    [InlineData("osx", "osx", "x86_64", true)]
    public void OsNameWithArchSuffixIsMatched(string name, string os, string arch, bool expected) =>
        Assert.Equal(expected, VersionRuleEvaluator.OsNameMatches(name, os, arch));

    [Fact]
    public void FiltersLibrariesByRules()
    {
        var windowsOnly = new VersionLibrary
        {
            Name = "com.example:demo:1.0",
            Rules = [new VersionRule { Action = "allow", Os = new VersionRuleOs { Name = "windows" } }],
        };

        Assert.True(LibraryRules.IsAllowed(windowsOnly, "windows", "x86_64"));
        Assert.False(LibraryRules.IsAllowed(windowsOnly, "linux", "x86_64"));
    }

    /// <summary>
    /// netty 的原生传输 jar 在整合包 JSON 里被写成「classifier 拼进版本段」
    /// （<c>…:4.2.15.Final-linux-x86_64</c>，没有 rules）；Windows 上不应进入类路径。
    /// </summary>
    [Fact]
    public void DropsNativeArtifactsWhosePlatformSuffixIsForeign()
    {
        var libraries = new[]
        {
            PlainLibrary("io.netty:netty-transport-native-epoll:4.2.15.Final-linux-x86_64"),
            PlainLibrary("io.netty:netty-transport-native-epoll:4.2.15.Final-windows-x86_64"),
            PlainLibrary("io.netty:netty-transport-native-unix-common:4.2.15.Final-linux-x86_64"),
            PlainLibrary("com.example:plain:1.0"),
        };

        var windows = Names(PlatformLibrarySelector.Plan(libraries, "windows", "x86_64"));

        Assert.Equal(
            [
                "io.netty:netty-transport-native-epoll:4.2.15.Final-windows-x86_64",
                "com.example:plain:1.0",
            ],
            windows);

        var linux = Names(PlatformLibrarySelector.Plan(libraries, "linux", "x86_64"));

        Assert.Contains("io.netty:netty-transport-native-epoll:4.2.15.Final-linux-x86_64", linux);
        Assert.Contains("io.netty:netty-transport-native-unix-common:4.2.15.Final-linux-x86_64", linux);
        Assert.DoesNotContain("io.netty:netty-transport-native-epoll:4.2.15.Final-windows-x86_64", linux);
    }

    [Fact]
    public void DropsNativeClassifierArtifactsWhosePlatformIsForeign()
    {
        var libraries = new[]
        {
            PlainLibrary("com.example:native:1.0:linux-x86_64"),
            PlainLibrary("com.example:native:1.0:windows-x86_64"),
            PlainLibrary("com.example:native:1.0:sources"),
        };

        var windows = Names(PlatformLibrarySelector.Plan(libraries, "windows", "x86_64"));

        // 普通 classifier（sources 等）不是平台标识，必须保留。
        Assert.Equal(
            ["com.example:native:1.0:windows-x86_64", "com.example:native:1.0:sources"],
            windows);
    }

    [Theory]
    [InlineData("windows", "x86_64", "windows-x86_64", true)]
    [InlineData("windows", "x86_64", "windows-x86", false)]
    [InlineData("windows", "x86", "windows-x86", true)]
    [InlineData("windows", "x86_64", "osx-x86_64", false)]
    public void ForeignArchitectureNativeArtifactsAreDropped(string os, string arch, string classifier, bool expected)
    {
        var library = PlainLibrary($"com.example:native:1.0:{classifier}");

        Assert.Equal(expected, !LibraryRules.IsForeignNativeArtifact(library, os, arch));
    }

    [Fact]
    public void UnrecognizedCoordinatesAreNeverDroppedAsNative()
    {
        // 版本段或 classifier 无法识别为平台时保持原行为，避免误删普通库。
        foreach (var name in new[]
                 {
                     "com.example:demo:1.0",
                     "org.lwjgl:lwjgl:3.4.1-unsafe",
                     "com.example:demo:1.0:sources",
                     "com.example:demo:1.0-natives",
                 })
        {
            Assert.False(LibraryRules.IsForeignNativeArtifact(PlainLibrary(name), "windows", "x86_64"));
        }
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("x86_64")]
    [InlineData("amd64")]
    public void AcceptsX64ArchAlias(string arch)
    {
        var library = new VersionLibrary
        {
            Name = "com.example:demo:1.0",
            Rules =
            [
                new VersionRule
                {
                    Action = "allow",
                    Os = new VersionRuleOs { Name = "windows", Arch = arch },
                },
            ],
        };

        Assert.True(LibraryRules.IsAllowed(library, "windows", "x86_64"));
        Assert.False(LibraryRules.IsAllowed(library, "windows", "x86"));
        Assert.False(LibraryRules.IsAllowed(library, "linux", "x86_64"));
    }

    [Fact]
    public void SelectsCleanroomX64NativeLibraries()
    {
        // Cleanroom 0.7.12 的真实格式：LWJGL natives 的 rules 使用 "arch": "x64"。
        var library = new VersionLibrary
        {
            Name = "org.lwjgl:lwjgl:3.3.3:natives-windows",
            Downloads = new VersionLibraryDownloads
            {
                Artifact = new VersionLibraryArtifact
                {
                    Path = "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar",
                    Url = "https://repo.maven.apache.org/maven2/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar",
                    Sha1 = "0000000000000000000000000000000000000000",
                    Size = 1,
                },
            },
            Rules =
            [
                new VersionRule
                {
                    Action = "allow",
                    Os = new VersionRuleOs { Name = "windows", Arch = "x64" },
                },
            ],
        };

        var plan = PlatformLibrarySelector.Plan([library], "windows", "x86_64");

        var item = Assert.Single(plan);
        Assert.NotNull(item.NativeArtifact);
        Assert.NotNull(item.PrimaryArtifact);
        Assert.Contains("natives-windows", item.NativeArtifact!.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifierOnlyLibraryHasNoPrimaryArtifact()
    {
        var library = new VersionLibrary
        {
            Name = "org.lwjgl.lwjgl:lwjgl-platform:2.9.0",
            Natives = new Dictionary<string, string>
            {
                ["linux"] = "natives-linux",
                ["windows"] = "natives-windows",
            },
            Downloads = new VersionLibraryDownloads
            {
                Classifiers = new Dictionary<string, VersionLibraryArtifact>
                {
                    ["natives-linux"] = new()
                    {
                        Path = "org/lwjgl/lwjgl/lwjgl-platform/2.9.0/lwjgl-platform-2.9.0-natives-linux.jar",
                    },
                },
            },
        };

        Assert.Null(LibraryRules.GetPrimaryArtifact(library));
        Assert.NotNull(LibraryRules.GetNativeArtifact(library, "linux", "x86_64"));
    }

    [Fact]
    public void ExtractsNativesAndSkipsMetaInf()
    {
        using var environment = new TestEnvironment();
        var artifact = new VersionLibraryArtifact
        {
            Path = "org/example/native/1.0/native-1.0-natives-windows.jar",
            Url = "https://libraries.minecraft.net/org/example/native/1.0/native-1.0-natives-windows.jar",
        };
        var library = new VersionLibrary
        {
            Name = "org.example:native:1.0:natives-windows",
            Downloads = new VersionLibraryDownloads { Artifact = artifact },
            Rules = [new VersionRule { Action = "allow", Os = new VersionRuleOs { Name = "windows" } }],
        };

        var jarPath = VersionFilePlanner.GetLibraryPath(environment.Paths, artifact);
        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
        using (var archive = ZipFile.Open(jarPath, ZipArchiveMode.Create))
        {
            var nativeEntry = archive.CreateEntry("native.dll");
            using (var writer = new StreamWriter(nativeEntry.Open()))
            {
                writer.Write("native");
            }

            archive.CreateEntry("META-INF/MANIFEST.MF");
        }

        var plan = PlatformLibrarySelector.Plan([library], "windows", "x86_64");
        var nativesDirectory = Path.Combine(environment.Root, "natives");
        var count = NativesExtractor.Extract(plan, environment.Paths, nativesDirectory);

        Assert.Equal(1, count);
        Assert.True(File.Exists(Path.Combine(nativesDirectory, "native.dll")));
        Assert.False(File.Exists(Path.Combine(nativesDirectory, "META-INF", "MANIFEST.MF")));
    }

    private static string[] Names(IReadOnlyList<LibraryPlanItem> plan) =>
        plan.Select(item => item.Library.Name).ToArray();

    private static VersionLibrary PlainLibrary(string name) => new()
    {
        Name = name,
        Downloads = new VersionLibraryDownloads
        {
            Artifact = new VersionLibraryArtifact { Path = MavenCoordinates.GetPath(name) },
        },
    };

    private static VersionLibrary NormalLibrary() => new()
    {
        Name = "com.example:demo:1.0",
        Downloads = new VersionLibraryDownloads
        {
            Artifact = new VersionLibraryArtifact
            {
                Path = "com/example/demo/1.0/demo-1.0.jar",
                Url = "https://libraries.minecraft.net/com/example/demo/1.0/demo-1.0.jar",
            },
        },
    };

    private static VersionLibrary NativeLibrary(string classifier) => new()
    {
        Name = $"org.lwjgl:lwjgl:3.3.3:{classifier}",
        Downloads = new VersionLibraryDownloads
        {
            Artifact = new VersionLibraryArtifact
            {
                Path = $"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-{classifier}.jar",
                Url = $"https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-{classifier}.jar",
            },
        },
        Rules = [new VersionRule { Action = "allow", Os = new VersionRuleOs { Name = "windows" } }],
    };
}
