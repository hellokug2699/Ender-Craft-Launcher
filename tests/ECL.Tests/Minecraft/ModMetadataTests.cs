// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using Ecl.Minecraft.Mods;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class ModMetadataTests
{
    [Fact]
    public void ReadsFabricMetadataAndDependencies()
    {
        var path = CreateJar("""
            {
              "id": "examplemod",
              "name": "Example Mod",
              "version": "1.2.3",
              "description": "Example",
              "depends": {
                "fabricloader": ">=0.16.0",
                "minecraft": "1.21.1",
                "missinglib": "*"
              },
              "breaks": ["badmod", "worsemod"]
            }
            """, "fabric.mod.json");

        try
        {
            var result = ModMetadataReader.Read(path);
            Assert.True(result.IsSuccess, result.Error);
            var metadata = result.Metadata!;
            Assert.Equal("examplemod", metadata.ModId);
            Assert.Equal("Example Mod", metadata.Name);
            Assert.Equal("1.2.3", metadata.Version);
            Assert.Equal(ModPlatform.Fabric, metadata.Platform);
            Assert.Contains(metadata.Dependencies, item => item.ModId == "missinglib" && item.Mandatory);
            Assert.Equal(["badmod", "worsemod"], metadata.Breaks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsForgeMetadataAndReplacesJarVersionPlaceholder()
    {
        var path = CreateJar("""
            modLoader="javafml"
            loaderVersion="[47,)"

            [[mods]]
            modId="examplemod"
            version="${file.jarVersion}"
            displayName="Example Forge Mod"
            description='''A multi-line description.'''

            [[dependencies.examplemod]]
            modId="forge"
            mandatory=true
            versionRange="[47,)"

            [[dependencies.examplemod]]
            modId="requiredlib"
            mandatory=true
            versionRange="[1.0,2.0)"
            """, "META-INF/mods.toml", ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Version: 4.5.6\n"));

        try
        {
            var result = ModMetadataReader.Read(path);
            Assert.True(result.IsSuccess, result.Error);
            var metadata = result.Metadata!;
            Assert.Equal("examplemod", metadata.ModId);
            Assert.Equal("4.5.6", metadata.Version);
            Assert.Equal(ModPlatform.Forge, metadata.Platform);
            Assert.Contains(metadata.Dependencies, item => item.ModId == "requiredlib" && item.Mandatory);
            Assert.Contains(metadata.Dependencies, item => item.ModId == "forge" && item.Mandatory);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsLegacyMcmodInfo()
    {
        var path = CreateJar("""
            [
              {
                "modid": "legacymod",
                "name": "Legacy Mod",
                "version": "1.0",
                "requiredMods": ["libone", "libtwo@1.0"]
              }
            ]
            """, "mcmod.info");

        try
        {
            var result = ModMetadataReader.Read(path);
            Assert.True(result.IsSuccess, result.Error);
            var metadata = result.Metadata!;
            Assert.Equal("legacymod", metadata.ModId);
            Assert.Equal(ModPlatform.LegacyForge, metadata.Platform);
            Assert.Equal(["libone", "libtwo"], metadata.Dependencies.Select(item => item.ModId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReportsMissingMetadata()
    {
        var path = CreateJar("not a mod", "readme.txt");
        try
        {
            var result = ModMetadataReader.Read(path);
            Assert.False(result.IsSuccess);
            Assert.NotNull(result.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateJar(string content, string metadataEntry, params (string Name, string Content)[] extras)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ecl-mod-{Guid.NewGuid():N}.jar");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, metadataEntry, content);
        foreach (var (name, extraContent) in extras)
        {
            WriteEntry(archive, name, extraContent);
        }

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
