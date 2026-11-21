// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Versions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class VersionResolverTests
{
    [Fact]
    public async Task MergesInheritanceChain()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();

        WriteVersion(environment, "parent", """
            {
              "id": "parent",
              "type": "release",
              "mainClass": "parent.Main",
              "assetIndex": { "id": "parent-assets" },
              "libraries": [
                { "name": "com.example:shared:1.0" },
                { "name": "com.example:parent:1.0" }
              ]
            }
            """);

        WriteVersion(environment, "child", """
            {
              "id": "child",
              "inheritsFrom": "parent",
              "libraries": [
                { "name": "com.example:shared:1.0" },
                { "name": "com.example:child:1.0" }
              ]
            }
            """);

        var resolved = await provider.GetRequiredService<IVersionResolver>().ResolveAsync("child");

        Assert.True(resolved.IsSuccess);
        var version = resolved.Value!;
        Assert.Equal("child", version.Id);
        Assert.Equal("parent.Main", version.MainClass);
        Assert.Equal("release", version.Type);
        Assert.Equal("parent-assets", version.AssetIndex?.Id);
        Assert.Null(version.InheritsFrom);
        Assert.Equal(3, version.Libraries.Count);
        Assert.Contains(version.Libraries, library => library.Name == "com.example:child:1.0");
        Assert.Contains(version.Libraries, library => library.Name == "com.example:parent:1.0");
    }

    [Fact]
    public async Task DetectsInheritanceCycle()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();

        WriteVersion(environment, "a", """{ "id": "a", "inheritsFrom": "b" }""");
        WriteVersion(environment, "b", """{ "id": "b", "inheritsFrom": "a" }""");

        var resolved = await provider.GetRequiredService<IVersionResolver>().ResolveAsync("a");

        Assert.False(resolved.IsSuccess);
        Assert.Equal("version.inheritance_cycle", resolved.Error?.Code);
    }

    [Fact]
    public async Task ReportsMissingParent()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();

        WriteVersion(environment, "child", """{ "id": "child", "inheritsFrom": "missing" }""");

        var resolved = await provider.GetRequiredService<IVersionResolver>().ResolveAsync("child");

        Assert.False(resolved.IsSuccess);
        Assert.Equal("version.missing", resolved.Error?.Code);
    }

    /// <summary>
    /// 合并型版本 JSON：顶层 libraries 是「原版段在前」，但 patches 的 cleanroom 段优先级更高。
    /// 解析结果必须把加载器段库排到原版段之前，否则原版旧库会遮蔽加载器库。
    /// </summary>
    [Fact]
    public async Task ResolvesMergedPatchesInPriorityOrder()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();

        WriteVersion(environment, "merged", """
            {
              "id": "merged",
              "mainClass": "top.outlands.foundation.boot.Foundation",
              "libraries": [
                { "name": "com.ibm.icu:icu4j-core-mojang:51.2" },
                { "name": "com.ibm.icu:icu4j:78.3" }
              ],
              "patches": [
                {
                  "id": "game",
                  "version": "1.12.2",
                  "priority": 0,
                  "libraries": [ { "name": "com.ibm.icu:icu4j-core-mojang:51.2" } ]
                },
                {
                  "id": "cleanroom",
                  "version": "0.6.13-alpha",
                  "priority": 30000,
                  "libraries": [ { "name": "com.ibm.icu:icu4j:78.3" } ]
                }
              ]
            }
            """);

        var resolved = await provider.GetRequiredService<IVersionResolver>().ResolveAsync("merged");

        Assert.True(resolved.IsSuccess);
        var libraries = resolved.Value!.Libraries;
        var loaderIndex = libraries.FindIndex(library => library.Name == "com.ibm.icu:icu4j:78.3");
        var vanillaIndex = libraries.FindIndex(library => library.Name == "com.ibm.icu:icu4j-core-mojang:51.2");
        Assert.True(loaderIndex >= 0, "缺少加载器段 ICU4J。");
        Assert.True(vanillaIndex >= 0, "缺少原版段裁剪版 ICU4J。");
        Assert.True(loaderIndex < vanillaIndex, "加载器段库必须排在原版段之前。");
    }

    private static void WriteVersion(TestEnvironment environment, string versionId, string json)
    {
        var path = VersionFilePlanner.GetVersionJsonPath(environment.Paths, versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}
