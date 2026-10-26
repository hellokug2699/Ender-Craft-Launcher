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
