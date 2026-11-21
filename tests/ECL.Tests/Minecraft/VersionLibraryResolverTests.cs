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

    /// <summary>
    /// 同一构件里「通用低版本 + 仅某平台生效的高版本」是真实存在的写法
    /// （<c>com.mojang:text2speech</c>：<c>1.10.3</c> 通用、<c>1.11.3</c> 仅 <c>osx-arm64</c>）。
    /// 去重必须在当前平台允许的候选里择优，否则被 rules 排除的高版本会顶掉可用版本，
    /// 随后平台过滤丢光整个构件，类路径缺 <c>Narrator</c>。
    /// </summary>
    [Fact]
    public void RulesGatedHigherVersionDoesNotDisplaceAllowedLowerVersion()
    {
        var version = new MinecraftVersion
        {
            Id = "merged",
            Libraries =
            [
                Text2Speech("1.10.3", rules: null),
                Text2Speech("1.10.3", DisallowOsxArm64),
                Text2Speech("1.11.3", AllowOsxArm64),
            ],
            Patches =
            [
                new VersionPatch
                {
                    Id = "game",
                    Priority = 0,
                    Libraries = [Text2Speech("1.10.3", rules: null)],
                },
                new VersionPatch
                {
                    Id = "cleanroom",
                    Priority = 30000,
                    Libraries =
                    [
                        Text2Speech("1.10.3", DisallowOsxArm64),
                        Text2Speech("1.11.3", AllowOsxArm64),
                    ],
                },
            ],
        };

        Assert.Equal(
            ["com.mojang:text2speech:1.10.3"],
            Names(VersionLibraryResolver.Resolve(version, "windows", "x86_64")));

        // Apple Silicon 上才允许 <c>1.11.3</c>：组合 token 由 VersionRuleEvaluator.OsNameMatches 识别。
        Assert.Equal(
            ["com.mojang:text2speech:1.11.3"],
            Names(VersionLibraryResolver.Resolve(version, "osx", "arm64")));
    }

    [Fact]
    public void CandidatesDisallowedOnEveryPlatformStillLeaveOneEntryForTheFilter()
    {
        // 全部候选都被 rules 排除时保留一条，交给后续平台过滤统一丢弃，避免去重结果依赖过滤顺序。
        var version = new MinecraftVersion
        {
            Id = "merged",
            Libraries = [Text2Speech("1.10.3", DisallowOsxArm64)],
            Patches =
            [
                new VersionPatch
                {
                    Id = "game",
                    Priority = 0,
                    Libraries = [Text2Speech("1.10.3", DisallowOsxArm64)],
                },
            ],
        };

        Assert.Equal(
            ["com.mojang:text2speech:1.10.3"],
            Names(VersionLibraryResolver.Resolve(version, "osx", "arm64")));
    }

    private static VersionRule[]? DisallowOsxArm64 =>
    [
        new VersionRule { Action = "allow" },
        new VersionRule
        {
            Action = "disallow",
            Os = new VersionRuleOs { Name = "osx-arm64" },
        },
    ];

    private static VersionRule[]? AllowOsxArm64 =>
    [
        new VersionRule
        {
            Action = "allow",
            Os = new VersionRuleOs { Name = "osx-arm64" },
        },
    ];

    private static VersionLibrary Text2Speech(string version, VersionRule[]? rules) => new()
    {
        Name = $"com.mojang:text2speech:{version}",
        Downloads = new VersionLibraryDownloads
        {
            Artifact = new VersionLibraryArtifact
            {
                Path = $"com/mojang/text2speech/{version}/text2speech-{version}.jar",
            },
        },
        Rules = rules?.ToList(),
    };

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
