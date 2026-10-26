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

    private static void WriteVersion(TestEnvironment environment, string versionId, string json)
    {
        var path = VersionFilePlanner.GetVersionJsonPath(environment.Paths, versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}
