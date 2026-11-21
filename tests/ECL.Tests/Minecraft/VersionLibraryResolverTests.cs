// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Versions;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 合并型版本 JSON（PCL/HMCL 写入的 <c>patches</c>）在顶层 <c>libraries</c> 里是「原版段在前」，
/// 直接使用会让原版旧库遮蔽加载器库。解析阶段必须按段 <c>priority</c> 重排：
/// 高优先段在前且同构件胜出，未覆盖条目垫底。
/// 具体崩溃表现见 LaunchLibraryFilterTests（LWJGL 2 / JNA / ICU4J）。
/// </summary>
public sealed class VersionLibraryResolverTests
{
    [Fact]
    public void LoaderSegmentMovesBeforeTheBaseSegment()
    {
        var version = VersionWithPatches(
            mergedLibraries: ["com.ibm.icu:icu4j-core-mojang:51.2", "com.ibm.icu:icu4j:78.3"],
            patches:
            [
                Patch("game", 0, "com.ibm.icu:icu4j-core-mojang:51.2"),
                Patch("cleanroom", 30000, "com.ibm.icu:icu4j:78.3"),
            ]);

        Assert.Equal(
            ["com.ibm.icu:icu4j:78.3", "com.ibm.icu:icu4j-core-mojang:51.2"],
            Names(VersionLibraryResolver.Resolve(version)));
    }

    [Fact]
    public void HigherPrioritySegmentWinsForTheSameArtifactEvenWithAnOlderVersion()
    {
        // 段优先级高于版本号：加载器故意固定旧版本时，不能被原版段的更新版本顶掉。
        var version = VersionWithPatches(
            mergedLibraries: ["com.example:lib:2.0", "com.example:lib:1.0"],
            patches:
            [
                Patch("game", 0, "com.example:lib:2.0"),
                Patch("loader", 500, "com.example:lib:1.0"),
            ]);

        Assert.Equal(["com.example:lib:1.0"], Names(VersionLibraryResolver.Resolve(version)));
    }

    [Fact]
    public void HighestVersionWinsInsideOneSegment()
    {
        var version = VersionWithPatches(
            mergedLibraries: ["com.example:lib:1.0", "com.example:lib:2.0"],
            patches: [Patch("loader", 500, "com.example:lib:1.0", "com.example:lib:2.0")]);

        Assert.Equal(["com.example:lib:2.0"], Names(VersionLibraryResolver.Resolve(version)));
    }

    [Fact]
    public void BasePatchSortsLastWhenPrioritiesAreEqual()
    {
        // 旧 JSON 可能不带 priority：此时基础段仍必须排在其它段之后。
        var version = VersionWithPatches(
            mergedLibraries: ["com.example:base:1.0", "com.example:loader:1.0"],
            patches:
            [
                Patch("game", 0, "com.example:base:1.0"),
                Patch("loader", 0, "com.example:loader:1.0"),
            ]);

        Assert.Equal(
            ["com.example:loader:1.0", "com.example:base:1.0"],
            Names(VersionLibraryResolver.Resolve(version)));
    }

    [Fact]
    public void TopLevelEntriesNotCoveredByAnyPatchAreAppendedLast()
    {
        var version = VersionWithPatches(
            mergedLibraries:
            [
                "com.example:base:1.0",
                "com.example:extra:1.0",
                "com.example:loader:1.0",
            ],
            patches:
            [
                Patch("game", 0, "com.example:base:1.0"),
                Patch("loader", 500, "com.example:loader:1.0"),
            ]);

        Assert.Equal(
            ["com.example:loader:1.0", "com.example:base:1.0", "com.example:extra:1.0"],
            Names(VersionLibraryResolver.Resolve(version)));
    }

    [Fact]
    public void VersionWithoutPatchesIsReturnedUnchanged()
    {
        var libraries = new List<VersionLibrary>
        {
            new() { Name = "com.example:b:1.0" },
            new() { Name = "com.example:a:1.0" },
        };
        var version = new MinecraftVersion { Id = "1.12.2", Libraries = libraries };

        Assert.Same(libraries, VersionLibraryResolver.Resolve(version));
    }

    [Fact]
    public void PatchWithoutLibrariesFallsBackToTheTopLevelList()
    {
        var libraries = new List<VersionLibrary> { new() { Name = "com.example:a:1.0" } };
        var version = new MinecraftVersion
        {
            Id = "merged",
            Libraries = libraries,
            Patches = [new VersionPatch { Id = "game", Priority = 0 }],
        };

        Assert.Same(libraries, VersionLibraryResolver.Resolve(version));
    }

    [Fact]
    public void UnparsableEntriesAreKeptAsIs()
    {
        var version = VersionWithPatches(
            mergedLibraries: ["not-a-coordinate.jar", "not-a-coordinate.jar", "com.example:lib:1.0"],
            patches:
            [
                Patch("game", 0, "not-a-coordinate.jar", "com.example:lib:1.0"),
                Patch("loader", 500, "not-a-coordinate.jar"),
            ]);

        // 无法解析坐标的条目既不参与去重，也不会被段顺序丢弃。
        Assert.Equal(
            ["not-a-coordinate.jar", "not-a-coordinate.jar", "com.example:lib:1.0"],
            Names(VersionLibraryResolver.Resolve(version)));
    }

    private static MinecraftVersion VersionWithPatches(
        string[] mergedLibraries,
        VersionPatch[] patches) => new()
    {
        Id = "merged",
        Libraries = mergedLibraries.Select(name => new VersionLibrary { Name = name }).ToList(),
        Patches = patches.ToList(),
    };

    private static VersionPatch Patch(string id, int priority, params string[] libraries) => new()
    {
        Id = id,
        Priority = priority,
        Libraries = libraries.Select(name => new VersionLibrary { Name = name }).ToList(),
    };

    private static string[] Names(IEnumerable<VersionLibrary> libraries) =>
        libraries.Select(library => library.Name).ToArray();
}
