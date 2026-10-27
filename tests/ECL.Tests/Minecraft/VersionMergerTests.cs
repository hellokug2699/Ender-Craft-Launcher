// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Json;
using Ecl.Minecraft.Versions;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class VersionMergerTests
{
    [Fact]
    public void MergesArgumentsAndKeepsParentLoggingWhenChildLoggingIsEmpty()
    {
        var parent = Deserialize(
            """
            {
              "id": "1.21.1",
              "type": "release",
              "mainClass": "net.minecraft.client.main.Main",
              "arguments": {
                "game": ["--username", "${auth_player_name}"],
                "jvm": ["-Djna.tmpdir=${natives_directory}"]
              },
              "logging": {
                "client": {
                  "argument": "-Dlog4j.configurationFile=${path}",
                  "file": { "id": "client-1.12.xml" }
                }
              },
              "libraries": []
            }
            """);

        var child = Deserialize(
            """
            {
              "id": "1.21.1-forge-47.4.0",
              "inheritsFrom": "1.21.1",
              "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
              "logging": {},
              "arguments": {
                "game": ["--launchTarget", "forgeclient"],
                "jvm": ["-DlibraryDirectory=${library_directory}"]
              },
              "libraries": []
            }
            """);

        var merged = VersionMerger.Merge(parent, child);

        Assert.NotNull(merged.Arguments);
        Assert.Equal(
            ["--username", "${auth_player_name}", "--launchTarget", "forgeclient"],
            merged.Arguments!.Game.Select(element => element.GetString()));
        Assert.Equal(
            ["-Djna.tmpdir=${natives_directory}", "-DlibraryDirectory=${library_directory}"],
            merged.Arguments.Jvm.Select(element => element.GetString()));
        Assert.NotNull(merged.Logging?.Client?.File);
        Assert.Equal("client-1.12.xml", merged.Logging!.Client!.File!.Id);
    }

    [Fact]
    public void ChildLibraryWithoutUrlInheritsParentArtifact()
    {
        var parent = Deserialize(
            """
            {
              "id": "1.7.10",
              "libraries": [
                {
                  "name": "net.minecraft:launchwrapper:1.12",
                  "downloads": {
                    "artifact": {
                      "path": "net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar",
                      "url": "https://libraries.minecraft.net/net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar",
                      "sha1": "1111111111111111111111111111111111111111",
                      "size": 32999
                    }
                  }
                }
              ]
            }
            """);

        var child = Deserialize(
            """
            {
              "id": "1.7.10-forge",
              "inheritsFrom": "1.7.10",
              "libraries": [
                { "name": "net.minecraft:launchwrapper:1.12" }
              ]
            }
            """);

        var merged = VersionMerger.Merge(parent, child);

        var library = Assert.Single(merged.Libraries);
        var artifact = LibraryRules.GetPrimaryArtifact(library);
        Assert.NotNull(artifact);
        Assert.Equal(
            "https://libraries.minecraft.net/net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar",
            artifact!.Url);
    }

    [Fact]
    public void LoaderLibrariesComeBeforeParentLibraries()
    {
        var parent = Deserialize(
            """
            {
              "id": "1.7.10",
              "libraries": [
                { "name": "com.google.guava:guava:15.0" },
                { "name": "org.lwjgl.lwjgl:lwjgl:2.9.1" }
              ]
            }
            """);

        var child = Deserialize(
            """
            {
              "id": "1.7.10-forge",
              "inheritsFrom": "1.7.10",
              "libraries": [
                { "name": "com.google.guava:guava:17.0" },
                { "name": "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10" }
              ]
            }
            """);

        var merged = VersionMerger.Merge(parent, child);

        Assert.Equal(
            [
                "com.google.guava:guava:17.0",
                "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10",
                "com.google.guava:guava:15.0",
                "org.lwjgl.lwjgl:lwjgl:2.9.1",
            ],
            merged.Libraries.Select(library => library.Name));
    }

    private static MinecraftVersion Deserialize(string json) =>
        JsonSerializer.Deserialize<MinecraftVersion>(json, EclJson.Default)!;
}
