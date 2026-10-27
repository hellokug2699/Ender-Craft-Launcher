// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Json;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Versions;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class VersionArgumentResolverTests
{
    [Fact]
    public void FiltersJvmArgumentsByOsAndArchitecture()
    {
        var version = Deserialize(
            """
            {
              "arguments": {
                "jvm": [
                  { "rules": [{ "action": "allow", "os": { "name": "osx" } }], "value": ["-XstartOnFirstThread"] },
                  { "rules": [{ "action": "allow", "os": { "name": "windows" } }], "value": "-XX:HeapDumpPath=heapdump" },
                  { "rules": [{ "action": "allow", "os": { "arch": "x86" } }], "value": "-Xss1M" },
                  "-Djna.tmpdir=${natives_directory}",
                  "-cp",
                  "${classpath}"
                ]
              }
            }
            """);

        var arguments = VersionArgumentResolver.ResolveJvmArguments(
            version.Arguments,
            value => value
                .Replace("${natives_directory}", "NATIVES", StringComparison.Ordinal)
                .Replace("${classpath}", "CLASSPATH", StringComparison.Ordinal),
            osName: "windows",
            osArch: "x86_64");

        Assert.Contains("-XX:HeapDumpPath=heapdump", arguments);
        Assert.Contains("-Djna.tmpdir=NATIVES", arguments);
        Assert.DoesNotContain("-XstartOnFirstThread", arguments);
        Assert.DoesNotContain("-Xss1M", arguments);
        Assert.DoesNotContain("-cp", arguments);
        Assert.DoesNotContain("CLASSPATH", arguments);
    }

    [Fact]
    public void FiltersGameArgumentsByRulesAndFeatures()
    {
        var version = Deserialize(
            """
            {
              "arguments": {
                "game": [
                  { "rules": [{ "action": "allow", "os": { "name": "windows", "arch": "x64" } }],
                    "value": ["--launchTarget", "forgeclient"] },
                  { "rules": [{ "action": "allow", "features": { "is_demo_user": true } }], "value": "--demo" },
                  "--username", "${auth_player_name}",
                  "--width", "1280"
                ]
              }
            }
            """);

        var arguments = VersionArgumentResolver.ResolveGameArguments(
            version.Arguments,
            value => value.Replace("${auth_player_name}", "Player", StringComparison.Ordinal),
            osName: "windows",
            osArch: "x86_64");

        Assert.Contains("--launchTarget", arguments);
        Assert.Contains("forgeclient", arguments);
        Assert.DoesNotContain("--demo", arguments);
        Assert.DoesNotContain("--username", arguments);
        Assert.DoesNotContain("--width", arguments);
    }

    [Fact]
    public void LegacyArgumentsSkipHandledOptionsAndKeepTweakClass()
    {
        const string minecraftArguments =
            "--username ${auth_player_name} --version ${version_name} " +
            "--userProperties ${user_properties} --userType ${user_type} " +
            "--tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker";

        var arguments = VersionArgumentResolver.ResolveExtraMinecraftArguments(
            minecraftArguments,
            value => value.Replace("${user_properties}", "{}", StringComparison.Ordinal));

        Assert.Contains("--tweakClass", arguments);
        Assert.Contains("net.minecraftforge.fml.common.launcher.FMLTweaker", arguments);
        Assert.DoesNotContain("--username", arguments);
        Assert.DoesNotContain("--userProperties", arguments);
        Assert.DoesNotContain("${user_properties}", arguments);
    }

    [Fact]
    public void DropsArgumentsWithUnknownPlaceholders()
    {
        var version = Deserialize(
            """
            {
              "arguments": {
                "game": ["--launchTarget", "forgeclient", "--quickPlayPath", "${quickPlayPath}"]
              }
            }
            """);

        var arguments = VersionArgumentResolver.ResolveGameArguments(
            version.Arguments,
            value => value,
            osName: "windows",
            osArch: "x86_64");

        Assert.Contains("--launchTarget", arguments);
        Assert.DoesNotContain("--quickPlayPath", arguments);
        Assert.DoesNotContain("${quickPlayPath}", arguments);
    }

    private static MinecraftVersion Deserialize(string json) =>
        JsonSerializer.Deserialize<MinecraftVersion>(json, EclJson.Default)!;
}
