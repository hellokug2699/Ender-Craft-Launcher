// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Diagnostics;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class CrashReportTests
{
    [Fact]
    public void ParsesDescriptionAndRanksModsByEvidence()
    {
        const string report = """
            ---- Minecraft Crash Report ----
            // Don't be sad, have a hug!
            Time: 2026-10-29 10:00:00
            Description: Rendering overlay

            java.lang.NoSuchMethodError: examplemod.Foo.bar()V
                at examplemod.Foo.bar(Foo.java:10)
                at net.minecraft.client.renderer.GameRenderer.render(GameRenderer.java:1)
            Caused by: java.lang.NullPointerException
                at othermod.Helper.tick(Helper.java:20)

            Minecraft Version: 1.21.1
            Java Version: 21.0.7

            Fabric Mods:
                examplemod: Example Mod 1.0.0
                othermod: Other Mod 2.0.0
            """;

        var analysis = CrashReportService.Parse(
            "crash.txt",
            report,
            new Dictionary<string, string> { ["examplemod"] = "Example Mod" });

        Assert.Equal("Rendering overlay", analysis.Description);
        Assert.Equal("1.21.1", analysis.MinecraftVersion);
        Assert.NotEmpty(analysis.Suspects);
        Assert.Equal("examplemod", analysis.Suspects[0].ModId);
        Assert.Contains(analysis.Suspects, suspect => suspect.ModId == "othermod");
        Assert.Contains(analysis.KeyLines, line => line.Contains("Caused by", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformModsAreNotReportedAsSuspects()
    {
        var analysis = CrashReportService.Parse("crash.txt", """
            Description: Test
            Fabric Mods:
                minecraft: Minecraft 1.21.1
                fabricloader: Fabric Loader 0.16.0
                examplemod: Example 1.0
            at examplemod.Example.run(Example.java:1)
            """);

        Assert.DoesNotContain(analysis.Suspects, suspect => suspect.ModId == "minecraft");
        Assert.Contains(analysis.Suspects, suspect => suspect.ModId == "examplemod");
    }
}
