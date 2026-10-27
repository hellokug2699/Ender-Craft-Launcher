// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using Ecl.Minecraft.Loaders;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class LoaderCatalogTests
{
    [Fact]
    public void ParsesFabricLoadersWithReleaseDates()
    {
        const string json = """
            [
              { "loader": { "version": "0.20.1", "stable": true } },
              { "loader": { "version": "0.19.5", "stable": false } }
            ]
            """;
        const string atom = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>tag:github.com,2008:Repository/65730412/0.20.1</id>
                <updated>2026-10-19T19:02:45Z</updated>
                <title>Fabric Loader 0.20.1</title>
              </entry>
            </feed>
            """;

        var dates = LoaderCatalogParsers.ParseAtomDates(atom);
        var versions = LoaderCatalogParsers.ParseFabricLoaders(json, dates);

        Assert.Equal(2, versions.Count);
        Assert.Equal("0.20.1", versions[0].Version);
        Assert.True(versions[0].Stable);
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T19:02:45Z"), versions[0].ReleasedAt);
        Assert.Null(versions[1].ReleasedAt);
    }

    [Fact]
    public void ParsesFabricApiVersions()
    {
        const string json = """
            [
              {
                "version_number": "0.116.17+1.21.1",
                "date_published": "2026-09-01T16:40:04.373428Z",
                "files": [
                  {
                    "url": "https://cdn.modrinth.com/data/P7dR8mSH/versions/Mys3P7lK/fabric-api.jar",
                    "filename": "fabric-api-0.116.17+1.21.1.jar",
                    "primary": true,
                    "size": 2452735,
                    "hashes": { "sha1": "810b2b0195371a012906241d8b85a32a1d6de53c" }
                  }
                ]
              }
            ]
            """;

        var versions = LoaderCatalogParsers.ParseFabricApiVersions(json);

        var version = Assert.Single(versions);
        Assert.Equal("0.116.17+1.21.1", version.Version);
        Assert.Equal("fabric-api-0.116.17+1.21.1.jar", version.FileName);
        Assert.Equal("810b2b0195371a012906241d8b85a32a1d6de53c", version.Sha1);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T16:40:04.373428Z"), version.ReleasedAt);
    }

    [Fact]
    public void ParsesOptiFineVersionsUsingObjectIdTimestamp()
    {
        const string json = """
            [
              { "_id": "5ccc81c8871d9b9623c12903", "mcversion": "1.12.2", "type": "HD_U", "patch": "C5",
                "filename": "OptiFine_1.12.2_HD_U_C5.jar" },
              { "_id": "600db6bad052d3ce55d3ed36", "mcversion": "1.12.2", "type": "HD_U", "patch": "G5",
                "filename": "OptiFine_1.12.2_HD_U_G5.jar" }
            ]
            """;

        var versions = LoaderCatalogParsers.ParseOptiFineVersions(json);

        Assert.Equal(2, versions.Count);
        Assert.Equal("HD_U:G5", versions[0].Key);
        Assert.Equal("OptiFine_1.12.2_HD_U_G5.jar", versions[0].FileName);
        Assert.Equal(DateTimeOffset.Parse("2019-05-03T18:00:40Z"), versions[1].ReleasedAt);
    }

    [Fact]
    public void ParsesCleanroomVersionsNewestFirst()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <metadata>
              <groupId>com.cleanroommc</groupId>
              <artifactId>cleanroom</artifactId>
              <versioning>
                <versions>
                  <version>0.6.13-alpha</version>
                  <version>0.7.12-alpha</version>
                </versions>
              </versioning>
            </metadata>
            """;

        var versions = LoaderCatalogParsers.ParseCleanroomVersions(xml);

        Assert.Equal("0.7.12-alpha", versions[0].Version);
        Assert.False(versions[0].Stable);
        Assert.Equal("0.6.13-alpha", versions[1].Version);
    }

    [Fact]
    public void ParsesForgeVersionsForRequestedGameVersion()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <metadata>
              <versioning>
                <versions>
                  <version>1.20.1-47.3.0</version>
                  <version>1.21.1-52.0.1</version>
                  <version>1.20.1-47.4.24</version>
                </versions>
              </versioning>
            </metadata>
            """;

        var versions = LoaderCatalogParsers.ParseForgeVersions(xml, "1.20.1");

        Assert.Equal(2, versions.Count);
        Assert.Equal("47.4.24", versions[0].Version);
        Assert.Equal("47.3.0", versions[1].Version);
        Assert.All(versions, version => Assert.True(version.Stable));
        Assert.All(versions, version => Assert.Null(version.ReleasedAt));
    }

    [Theory]
    [InlineData("1.21.1", "21.1.")]
    [InlineData("1.21.10", "21.10.")]
    [InlineData("26.3", "26.3.0.")]
    [InlineData("26.1.2", "26.1.2.")]
    [InlineData("1.20.1", "47.1.")]
    public void ComputesNeoForgeVersionPrefix(string gameVersion, string expected) =>
        Assert.Equal(expected, LoaderCatalogParsers.GetNeoForgeVersionPrefix(gameVersion));

    [Fact]
    public void RejectsNeoForgeForVersionsBefore1202()
    {
        Assert.Null(LoaderCatalogParsers.GetNeoForgeVersionPrefix("1.19.2"));
        Assert.Empty(LoaderCatalogParsers.ParseNeoForgeVersions("<metadata />", "1.19.2"));
    }

    [Fact]
    public void ParsesNeoForgeVersionsForModernGameVersions()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <metadata>
              <versioning>
                <versions>
                  <version>21.1.263</version>
                  <version>21.1.264</version>
                  <version>21.10.5</version>
                  <version>26.3.0.0-beta</version>
                  <version>26.3.0.99</version>
                </versions>
              </versioning>
            </metadata>
            """;

        var versions = LoaderCatalogParsers.ParseNeoForgeVersions(xml, "1.21.1");

        Assert.Equal(2, versions.Count);
        Assert.Equal("21.1.264", versions[0].Version);
        Assert.Equal("21.1.263", versions[1].Version);

        var newer = LoaderCatalogParsers.ParseNeoForgeVersions(xml, "26.3");
        Assert.Equal(2, newer.Count);
        Assert.Equal("26.3.0.99", newer[0].Version);
        Assert.True(newer[0].Stable);
        Assert.False(newer[1].Stable);
    }

    [Fact]
    public void ParsesLegacyNeoForgeVersionsFromForgeArtifact()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <metadata>
              <versioning>
                <versions>
                  <version>1.20.1-47.1.105</version>
                  <version>1.20.1-47.1.106</version>
                </versions>
              </versioning>
            </metadata>
            """;

        var versions = LoaderCatalogParsers.ParseNeoForgeVersions(xml, "1.20.1");

        Assert.Equal(2, versions.Count);
        Assert.Equal("47.1.106", versions[0].Version);
        Assert.Equal("47.1.105", versions[1].Version);
    }

    [Fact]
    public void ReadsCleanroomInstallerAndResolvesRepositoryUrls()
    {
        var installerPath = Path.Combine(Path.GetTempPath(), $"cleanroom-{Guid.NewGuid():N}.jar");
        try
        {
            using (var archive = ZipFile.Open(installerPath, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "version.json", """
                    {
                      "id": "Cleanroom-0.7.12-alpha",
                      "mainClass": "top.outlands.foundation.boot.Foundation",
                      "javaVersion": { "component": "java-runtime-epsilon", "majorVersion": 25 },
                      "libraries": [
                        {
                          "name": "com.cleanroommc:cleanroom:0.7.12-alpha:universal",
                          "downloads": {
                            "artifact": {
                              "path": "com/cleanroommc/cleanroom/0.7.12-alpha/cleanroom-0.7.12-alpha-universal.jar",
                              "url": ""
                            }
                          }
                        }
                      ]
                    }
                    """);
                WriteEntry(archive, "install_profile.json", """
                    {
                      "repositories": {
                        "com.cleanroommc": "https://maven.cleanroommc.com/"
                      }
                    }
                    """);
            }

            var content = CleanroomInstallerReader.Read(installerPath);

            Assert.Equal("top.outlands.foundation.boot.Foundation", content.Version.MainClass);
            Assert.Equal(25, content.Version.JavaVersion?.MajorVersion);
            var library = Assert.Single(content.Version.Libraries);
            Assert.Equal(
                "https://maven.cleanroommc.com/com/cleanroommc/cleanroom/0.7.12-alpha/cleanroom-0.7.12-alpha-universal.jar",
                library.Downloads?.Artifact?.Url);
        }
        finally
        {
            File.Delete(installerPath);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }
}
