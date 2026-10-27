// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;
using Xunit;

namespace Ecl.Tests.Install;

public sealed class ForgeInstallerTests
{
    [Fact]
    public void ReadsInstallerProfileAndVersion()
    {
        var installerPath = CreateInstaller("""
            {
              "spec": 1,
              "minecraft": "1.21.1",
              "data": {
                "PATCHED": { "client": "[net.neoforged:neoforge:21.1.264:client]", "server": "[net.neoforged:neoforge:21.1.264:server]" }
              },
              "processors": [
                {
                  "sides": ["client"],
                  "jar": "net.neoforged.installertools:jarsplitter:2.1.2",
                  "classpath": ["net.neoforged.installertools:jarsplitter:2.1.2"],
                  "args": ["--input", "{MINECRAFT_JAR}", "--slim", "{MC_SLIM}", "--extra", "{MC_EXTRA}"]
                },
                { "sides": ["server"], "jar": "net.neoforged.installertools:installertools:2.1.2" }
              ],
              "libraries": [
                { "name": "net.neoforged.installertools:jarsplitter:2.1.2" }
              ]
            }
            """, """
            {
              "id": "neoforge-21.1.264",
              "inheritsFrom": "1.21.1",
              "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
              "arguments": {
                "game": ["--launchTarget", "forgeclient", "--fml.neoForgeVersion", "21.1.264"],
                "jvm": ["-DlibraryDirectory=${library_directory}", "-p", "${library_directory}/cpw/mods/bootstraplauncher/2.0.2/bootstraplauncher-2.0.2.jar"]
              },
              "libraries": []
            }
            """);

        try
        {
            var content = ForgeInstallerReader.Read(installerPath, ForgeInstallers.NeoForgeMavenRoot);

            Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", content.Version.MainClass);
            Assert.Equal("1.21.1", content.Version.InheritsFrom);
            Assert.Equal(1, content.Profile.Spec);
            Assert.Equal(2, content.Profile.Processors.Count);
            Assert.Single(content.Profile.Libraries);
            Assert.Single(ForgeProcessorPlanner.SelectProcessors(content.Profile, ForgeProcessorPlanner.ClientSide));
        }
        finally
        {
            File.Delete(installerPath);
        }
    }

    [Fact]
    public void ResolvesLegacyUniversalJarUrl()
    {
        var installerPath = CreateInstaller("""
            {
              "spec": 0,
              "profile": "forge",
              "version": "1.12.2-forge-14.23.5.2860",
              "path": "net.minecraftforge:forge:1.12.2-14.23.5.2860",
              "minecraft": "1.12.2",
              "json": "/version.json"
            }
            """, """
            {
              "id": "1.12.2-forge-14.23.5.2860",
              "inheritsFrom": "1.12.2",
              "mainClass": "net.minecraft.launchwrapper.Launch",
              "minecraftArguments": "--tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker",
              "libraries": [
                {
                  "name": "net.minecraftforge:forge:1.12.2-14.23.5.2860",
                  "downloads": {
                    "artifact": {
                      "path": "net/minecraftforge/forge/1.12.2-14.23.5.2860/forge-1.12.2-14.23.5.2860.jar",
                      "url": ""
                    }
                  }
                }
              ]
            }
            """);

        try
        {
            var content = ForgeInstallerReader.Read(installerPath, ForgeInstallers.ForgeMavenRoot);

            var library = Assert.Single(content.Version.Libraries);
            Assert.Equal(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/1.12.2-14.23.5.2860/forge-1.12.2-14.23.5.2860-universal.jar",
                library.Downloads?.Artifact?.Url);
        }
        finally
        {
            File.Delete(installerPath);
        }
    }

    [Fact]
    public void ResolvesProcessorArgumentsAndMavenReferences()
    {
        var profile = new ForgeInstallProfile
        {
            Spec = 1,
            Minecraft = "1.21.1",
            Data = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["PATCHED"] = System.Text.Json.JsonDocument.Parse(
                    """{ "client": "[com.example:demo:1.0:client]" }""").RootElement,
                ["BINPATCH"] = System.Text.Json.JsonDocument.Parse(
                    """{ "client": "/data/client.lzma" }""").RootElement,
                ["SIDE"] = System.Text.Json.JsonDocument.Parse("""{ "client": "'client'" }""").RootElement,
            },
        };

        var tokens = ForgeProcessorPlanner.BuildTokens(
            profile,
            ForgeProcessorPlanner.ClientSide,
            "/cache/installer.jar",
            "/root",
            "/root/versions/1.21.1/1.21.1.jar",
            "1.21.1",
            "/root/libraries",
            entry => "/cache/extracted/" + entry);

        Assert.Equal("/cache/installer.jar", tokens["INSTALLER"]);
        Assert.Equal("1.21.1", tokens["MINECRAFT_VERSION"]);
        Assert.Equal("client", tokens["SIDE"]);
        Assert.Equal("/root/libraries", tokens["LIBRARY_DIR"]);
        Assert.Equal(
            Path.Combine("/root/libraries", "com/example/demo/1.0/demo-1.0-client.jar".Replace('/', Path.DirectorySeparatorChar)),
            tokens["PATCHED"]);
        Assert.Equal("/cache/extracted/data/client.lzma", tokens["BINPATCH"]);

        var processor = new ForgeProcessor
        {
            Jar = "com.example:demo:1.0",
            Args = ["--input", "[com.example:demo:1.0@zip]", "--output", "{PATCHED}", "--version", "{MINECRAFT_VERSION}"],
        };

        var arguments = ForgeProcessorPlanner.ResolveArguments(processor, tokens, "/root/libraries");

        Assert.Equal(
            Path.Combine("/root/libraries", "com/example/demo/1.0/demo-1.0.zip".Replace('/', Path.DirectorySeparatorChar)),
            arguments[1]);
        Assert.Equal(tokens["PATCHED"], arguments[3]);
        Assert.Equal("1.21.1", arguments[5]);
    }

    [Fact]
    public void ResolvesLegacy1710UrlsFromBaseUrlAndOfficialLibraries()
    {
        var installerPath = CreateInstaller("""
            {
              "install": { "profileName": "Forge", "minecraft": "1.7.10" },
              "versionInfo": {
                "id": "1.7.10-Forge10.13.4.1614-1.7.10",
                "inheritsFrom": "1.7.10",
                "mainClass": "net.minecraft.launchwrapper.Launch",
                "minecraftArguments": "--userProperties ${user_properties}",
                "libraries": [
                  {
                    "name": "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10",
                    "url": "https://maven.minecraftforge.net/"
                  },
                  { "name": "net.minecraft:launchwrapper:1.12" },
                  {
                    "name": "org.scala-lang:scala-library:2.11.1",
                    "url": "https://maven.minecraftforge.net/"
                  },
                  { "name": "lzma:lzma:0.0.1" }
                ]
              }
            }
            """, "{}");

        try
        {
            var content = ForgeInstallerReader.Read(installerPath, ForgeInstallers.ForgeMavenRoot);

            var forge = content.Version.Libraries[0];
            Assert.Equal(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/1.7.10-10.13.4.1614-1.7.10/forge-1.7.10-10.13.4.1614-1.7.10-universal.jar",
                LibraryRules.GetPrimaryArtifact(forge)?.Url);

            var launchWrapper = content.Version.Libraries[1];
            Assert.Equal(
                "https://libraries.minecraft.net/net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar",
                LibraryRules.GetPrimaryArtifact(launchWrapper)?.Url);

            var scala = content.Version.Libraries[2];
            Assert.Equal(
                "https://maven.minecraftforge.net/org/scala-lang/scala-library/2.11.1/scala-library-2.11.1.jar",
                LibraryRules.GetPrimaryArtifact(scala)?.Url);

            var lzma = content.Version.Libraries[3];
            Assert.Equal(
                "https://libraries.minecraft.net/lzma/lzma/0.0.1/lzma-0.0.1.jar",
                LibraryRules.GetPrimaryArtifact(lzma)?.Url);
        }
        finally
        {
            File.Delete(installerPath);
        }
    }

    [Fact]
    public void DoesNotDoubleCombineFullFileUrls()
    {
        var library = new Ecl.Minecraft.Versions.VersionLibrary
        {
            Name = "net.minecraft:launchwrapper:1.12",
            Url = "https://libraries.minecraft.net/net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar",
        };

        var artifact = Ecl.Minecraft.Versions.LibraryRules.GetPrimaryArtifact(library);

        Assert.NotNull(artifact);
        Assert.Equal(library.Url, artifact!.Url);
    }

    private static string CreateInstaller(string installProfile, string versionJson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"forge-installer-{Guid.NewGuid():N}.jar");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "install_profile.json", installProfile);
        WriteEntry(archive, "version.json", versionJson);
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }
}
