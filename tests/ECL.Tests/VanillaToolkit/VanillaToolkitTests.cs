// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ecl.VanillaToolkit;
using Xunit;

namespace Ecl.Tests.VanillaToolkit;

/// <summary>ADR 0040：版本化数据包、精确版本选择与注册表查询。</summary>
public sealed class VanillaToolkitTests
{
    [Fact]
    public async Task RepositoryTemplateLoadsAndQueriesChineseName()
    {
        var repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is null)
        {
            return;
        }

        var template = Path.Combine(repositoryRoot, "additions", "vanilla-toolkit", "data-template");
        if (!Directory.Exists(template))
        {
            return;
        }

        var pack = CopyTemplate(template);
        try
        {
            var loaded = await VanillaDataPackLoader.LoadAsync(pack);
            Assert.True(loaded.IsSuccess, loaded.Error?.ToString());
            Assert.Equal(["1.20.1"], loaded.Value!.Manifest.MinecraftVersions);

            var query = new RegistryQueryService(loaded.Value, "1.20.1");
            var result = query.Query("钻石", preferredLanguage: "zh-CN");
            Assert.True(result.IsSuccess, result.Error?.ToString());
            var hit = Assert.Single(result.Value!.Results);
            Assert.Equal("minecraft:diamond", hit.Entry.Id);
            Assert.Equal(RegistryMatchKind.ExactName, hit.Match);
            Assert.Equal("钻石", RegistryQueryService.DisplayName(hit.Entry, "zh-CN"));

            var partial = query.Query("stone", kind: "block");
            Assert.True(partial.IsSuccess);
            Assert.Contains(partial.Value!.Results, item => item.Entry.Id == "minecraft:stone");
            Assert.All(partial.Value.Results, item => Assert.Equal("block", item.Entry.Kind));
        }
        finally
        {
            Directory.Delete(pack, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedVersionIsRejectedInsteadOfFallingBack()
    {
        using var pack = new DataPackBuilder();
        pack.Write("registry.json", RegistryJson(EntryJson("minecraft:stone", "block", "石头", "Stone")));
        var directory = pack.Save();

        var opened = await VanillaToolkitFacade.OpenRegistryAsync(directory, "1.21.1");

        Assert.False(opened.IsSuccess);
        Assert.Equal("toolkit.version_unsupported", opened.Error!.Code);
        Assert.Contains("不会自动改用其他版本", opened.Error.Message);
    }

    [Fact]
    public async Task HashMismatchAndTraversalAreRejected()
    {
        using var pack = new DataPackBuilder();
        pack.Write("registry.json", RegistryJson(EntryJson("minecraft:stone", "block", "石头", "Stone")));
        var directory = pack.Save();
        await File.AppendAllTextAsync(Path.Combine(directory, "registry.json"), " ");

        var tampered = await VanillaDataPackLoader.LoadAsync(directory);
        Assert.Equal("toolkit.domain_size_mismatch", tampered.Error?.Code);

        pack.Write("registry.json", RegistryJson(EntryJson("minecraft:stone", "block", "石头", "Stone")));
        directory = pack.Save(fileOverride: "../outside.json");
        var traversal = await VanillaDataPackLoader.LoadAsync(directory);
        Assert.Equal("toolkit.domain_invalid", traversal.Error?.Code);
    }

    [Fact]
    public async Task VersionApplicabilityUsesIntroducedRemovedAndExplicitVersions()
    {
        using var pack = new DataPackBuilder();
        pack.Write(
            "registry.json",
            RegistryJson(
                EntryJson("minecraft:stone", "block", "石头", "Stone"),
                EntryJson("minecraft:copper", "item", "铜", "Copper", introduced: "1.21.0"),
                EntryJson("minecraft:old", "item", "旧", "Old", removed: "1.20.1"),
                EntryJson("minecraft:snapshot_only", "item", "快照", "Snapshot", versions: ["24w14a"])));
        var loaded = await VanillaDataPackLoader.LoadAsync(pack.Save());
        Assert.True(loaded.IsSuccess, loaded.Error?.ToString());

        var query = new RegistryQueryService(loaded.Value!, "1.20.1");
        var all = query.Query("minecraft:", limit: 100);
        Assert.True(all.IsSuccess);
        var ids = all.Value!.Results.Select(item => item.Entry.Id).ToArray();
        Assert.Contains("minecraft:stone", ids);
        Assert.DoesNotContain("minecraft:copper", ids);
        Assert.DoesNotContain("minecraft:old", ids);
        Assert.DoesNotContain("minecraft:snapshot_only", ids);
    }

    [Fact]
    public void LocalResourcesRequireExactVersionAndReadFromClientJar()
    {
        using var directory = new TempDirectory();
        var versionDirectory = Path.Combine(directory.Path, "1.20.1");
        Directory.CreateDirectory(versionDirectory);
        var jar = Path.Combine(versionDirectory, "1.20.1.jar");
        var texture = new byte[] { 1, 2, 3, 4 };
        using (var archive = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("assets/minecraft/textures/block/stone.png");
            using var stream = entry.Open();
            stream.Write(texture);
        }

        var resources = InstalledVersionResources.Create(versionDirectory, "1.20.1");
        Assert.True(resources.IsSuccess, resources.Error?.ToString());
        var read = resources.Value!.ReadTexture("block/stone.png");
        Assert.True(read.IsSuccess);
        Assert.Equal(texture, read.Value);
        Assert.Equal("toolkit.resource_missing", resources.Value.ReadTexture("block/missing.png").Error?.Code);

        var mismatch = InstalledVersionResources.Create(versionDirectory, "1.21.1");
        Assert.Equal("toolkit.resources_version_mismatch", mismatch.Error?.Code);

        var unavailable = InstalledVersionResources.Create(null, "1.20.1");
        Assert.Equal("toolkit.resources_unavailable", unavailable.Error?.Code);
        Assert.Contains("没有可用的版本实例", unavailable.Error!.Message);
    }

    private static string CopyTemplate(string template)
    {
        var destination = Path.Combine(Path.GetTempPath(), "ecl-toolkit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(template, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        File.Move(
            Path.Combine(destination, "manifest.example.json"),
            Path.Combine(destination, "manifest.json"),
            overwrite: true);
        return destination;
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ECL.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string EntryJson(
        string id,
        string kind,
        string zh,
        string en,
        string? introduced = null,
        string? removed = null,
        string[]? versions = null,
        string quality = "verified")
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["kind"] = kind,
            ["names"] = new Dictionary<string, string> { ["zh-CN"] = zh, ["en-US"] = en },
            ["aliases"] = new[] { en.ToLowerInvariant(), zh },
            ["introduced"] = introduced,
            ["removed"] = removed,
            ["versions"] = versions,
            ["quality"] = quality,
            ["sourceId"] = "official-registry",
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string RegistryJson(params string[] entries) =>
        $$"""{"schemaVersion":1,"entries":[{{string.Join(",", entries)}}]}""";

    private sealed class DataPackBuilder : IDisposable
    {
        private readonly Dictionary<string, string> _files = [];
        private string? _root;

        public void Write(string path, string content) => _files[path] = content;

        public string Save(string fileOverride = "registry.json")
        {
            _root = Path.Combine(Path.GetTempPath(), "ecl-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var bytes = Encoding.UTF8.GetBytes(_files["registry.json"]);
            var registryPath = Path.Combine(_root, "registry.json");
            File.WriteAllBytes(registryPath, bytes);
            var manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                packId = "ecl.test.data",
                dataVersion = "1.0.0",
                minecraftVersions = new[] { "1.20.1" },
                domains = new[]
                {
                    new
                    {
                        name = "registry",
                        schemaVersion = 1,
                        file = fileOverride,
                        size = bytes.Length,
                        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                    },
                },
                sources = Array.Empty<object>(),
                maintainer = "tests",
                updatedAtUtc = "2026-11-02T00:00:00Z",
                quality = "verified",
                license = "test",
                description = "test pack",
            });
            File.WriteAllText(Path.Combine(_root, "manifest.json"), manifest);
            return _root;
        }

        public void Dispose()
        {
            try
            {
                if (_root is not null && Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecl-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
