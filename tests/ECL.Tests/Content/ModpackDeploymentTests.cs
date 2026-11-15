// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Modrinth;
using Ecl.Minecraft.Mods;
using Ecl.Operations;
using Ecl.Tests.Modrinth;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Content;

/// <summary>
/// 整合包模式的领域准备与部署（`download-instance-versions.md` §5）：
/// 按选定版本下载归档、只按清单统计模组、统一创建新实例并导入，失败时删除本次新建实例。
/// </summary>
public sealed class ModpackDeploymentTests
{
    private const string ModsA = "mods/alpha.jar";
    private const string ModsB = "mods/beta.jar";
    private const string ModsServerOnly = "mods/server-only.jar";
    private const string ConfigFile = "config/example.json";

    [Fact]
    public void ModrinthManifestCountsClientModsAndLeavesOtherFilesOut()
    {
        using var environment = new TestEnvironment();
        var archivePath = WriteArchive(environment, "pack.mrpack", ModrinthIndex());

        var result = ModpackArchiveInspector.Read(archivePath);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var metadata = result.Value!;
        Assert.Equal("Client Pack", metadata.Name);
        Assert.Equal("1.21.1", metadata.GameVersion);
        Assert.Equal("fabric", metadata.Loader);
        Assert.Equal("0.16.0", metadata.LoaderVersion);
        Assert.Equal(2, metadata.ModCount);
        Assert.Equal(
            new[] { ModsA, ModsB },
            metadata.ModFiles!.Select(file => file.RelativePath));
        Assert.All(metadata.ModFiles!, file => Assert.Null(file.ProjectId));
    }

    [Fact]
    public void CurseForgeManifestCountsRequiredFilesAndKeepsIdsOnly()
    {
        using var environment = new TestEnvironment();
        var archivePath = WriteArchive(environment, "pack.zip", CurseForgeManifest());

        var result = ModpackArchiveInspector.Read(archivePath);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var metadata = result.Value!;
        Assert.Equal("Forge Pack", metadata.Name);
        Assert.Equal("1.20.1", metadata.GameVersion);
        Assert.Equal("forge", metadata.Loader);
        Assert.Equal("47.4.0", metadata.LoaderVersion);
        Assert.Equal(2, metadata.ModCount);
        var files = metadata.ModFiles!;
        Assert.Equal((101, 1001), (files[0].ProjectId, files[0].FileId));
        Assert.Equal((102, 1002), (files[1].ProjectId, files[1].FileId));
        Assert.All(files, file => Assert.Null(file.RelativePath));
    }

    [Fact]
    public void ManifestWithoutAFileListReportsAnUnknownCountInsteadOfZero()
    {
        using var environment = new TestEnvironment();
        var index = new
        {
            formatVersion = 1,
            game = "minecraft",
            versionId = "1.0.0",
            name = "No Files",
            dependencies = new Dictionary<string, string> { ["minecraft"] = "1.21.1" },
        };
        var archivePath = WriteArchive(environment, "empty.mrpack", JsonSerializer.Serialize(index));

        var result = ModpackArchiveInspector.Read(archivePath);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Null(result.Value!.ModFiles);
        Assert.Null(result.Value.ModCount);
    }

    [Fact]
    public async Task PrepareModpackDownloadsTheSelectedVersionAndReadsTheManifest()
    {
        var payload = CreateMrpackPayload();
        using var environment = new TestEnvironment();
        var downloads = new PayloadDownloadService(payload);
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(downloads);
            services.AddSingleton<IModrinthClient>(CreateModrinthClient(payload.Length));
        });

        var result = await provider.GetRequiredService<IContentInstallService>()
            .PrepareModpackAsync(CreateModpackHit(), CreateModpackVersion(payload.Length));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var prepared = result.Value!;
        Assert.True(File.Exists(prepared.ArchivePath));
        Assert.Equal("1.21.1", prepared.Metadata.GameVersion);
        Assert.Equal("fabric", prepared.Metadata.Loader);
        Assert.Equal(2, prepared.Metadata.ModCount);
        Assert.Equal(payload.Length, prepared.ArchiveSize);
        Assert.Single(downloads.Requests);

        var snapshot = Assert.Single(provider.GetRequiredService<IOperationStore>().Snapshots);
        Assert.Equal(OperationKind.Download, snapshot.Kind);
        Assert.Equal(OperationState.Completed, snapshot.State);
    }

    [Fact]
    public async Task DeployModpackCreatesANewInstanceAndImportsTheArchive()
    {
        var payload = CreateMrpackPayload();
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(new PayloadDownloadService(payload));
            services.AddSingleton<IModrinthClient>(CreateModrinthClient(payload.Length));
        });

        var archivePath = WriteDownloadedArchive(environment, payload);
        var result = await provider.GetRequiredService<IContentInstallService>()
            .DeployModpackAsync(new ModpackDeploymentRequest(
                "Prepared Pack",
                archivePath,
                "1.21.1",
                "fabric",
                "0.16.0"));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var instances = await provider.GetRequiredService<IInstanceService>().GetInstancesAsync();
        var created = Assert.Single(instances);
        Assert.Equal("Prepared Pack", created.Name);
        Assert.Equal("1.21.1", created.GameVersion);
        Assert.Equal("fabric", created.Loader);
        Assert.Equal("0.16.0", created.LoaderVersion);
        Assert.Equal(created.Id, result.Value!.InstanceId);
        Assert.True(File.Exists(archivePath));
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.GetInstanceGameDirectory(created.Id),
            "config",
            "test.txt")));

        var snapshot = Assert.Single(provider.GetRequiredService<IOperationStore>().Snapshots);
        Assert.Equal(OperationKind.Import, snapshot.Kind);
        Assert.Equal(OperationState.Completed, snapshot.State);
        Assert.Equal("Prepared Pack", snapshot.Title);
    }

    [Fact]
    public async Task DeployModpackRollsBackTheCreatedInstanceWhenImportFails()
    {
        var payload = CreateMrpackPayload();
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(new PayloadDownloadService(payload));
            services.AddSingleton<IModrinthClient>(CreateModrinthClient(payload.Length));
            services.AddSingleton<IModpackService>(new FailingModpackService());
        });

        var archivePath = WriteDownloadedArchive(environment, payload);
        var result = await provider.GetRequiredService<IContentInstallService>()
            .DeployModpackAsync(new ModpackDeploymentRequest(
                "Broken Pack",
                archivePath,
                "1.21.1",
                "fabric",
                "0.16.0"));

        Assert.False(result.IsSuccess);
        Assert.Empty(await provider.GetRequiredService<IInstanceService>().GetInstancesAsync());

        var snapshot = Assert.Single(provider.GetRequiredService<IOperationStore>().Snapshots);
        Assert.Equal(OperationKind.Import, snapshot.Kind);
        Assert.Equal(OperationState.Failed, snapshot.State);
    }

    [Fact]
    public async Task DeployModpackRefusesAMissingArchive()
    {
        var payload = CreateMrpackPayload();
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(new PayloadDownloadService(payload));
            services.AddSingleton<IModrinthClient>(CreateModrinthClient(payload.Length));
        });

        var result = await provider.GetRequiredService<IContentInstallService>()
            .DeployModpackAsync(new ModpackDeploymentRequest(
                "Missing Pack",
                Path.Combine(environment.Root, "missing.mrpack"),
                "1.21.1",
                "fabric",
                "0.16.0"));

        Assert.False(result.IsSuccess);
        Assert.Equal("modpack.not_found", result.Error!.Code);
        Assert.Empty(await provider.GetRequiredService<IInstanceService>().GetInstancesAsync());
    }

    private sealed class FailingModpackService : IModpackService
    {
        public Task<Result<ModpackImportReport>> ImportAsync(
            string instanceId,
            string archivePath,
            IProgress<ModpackProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ModpackImportReport>.Fail("test.import_failed", "模拟导入失败。"));

        public Task<Result<ModpackExportReport>> ExportAsync(
            string instanceId,
            string outputPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static string WriteDownloadedArchive(TestEnvironment environment, byte[] payload)
    {
        var directory = Path.Combine(environment.Paths.DownloadsDirectory, "modpacks");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.mrpack");
        File.WriteAllBytes(path, payload);
        return path;
    }

    private static string WriteArchive(TestEnvironment environment, string fileName, string json)
    {
        var path = Path.Combine(environment.Root, fileName);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, fileName.EndsWith(".mrpack", StringComparison.Ordinal)
                ? "modrinth.index.json"
                : "manifest.json", json);
        }

        return path;
    }

    private static string ModrinthIndex() => JsonSerializer.Serialize(new
    {
        formatVersion = 1,
        game = "minecraft",
        versionId = "1.0.0",
        name = "Client Pack",
        dependencies = new Dictionary<string, string>
        {
            ["minecraft"] = "1.21.1",
            ["fabric-loader"] = "0.16.0",
        },
        files = new object[]
        {
            new { path = ModsA, hashes = new { sha1 = "a" }, downloads = new[] { "https://cdn.modrinth.com/a.jar" } },
            new
            {
                path = ModsServerOnly,
                env = new { client = "unsupported", server = "required" },
                hashes = new { sha1 = "b" },
                downloads = new[] { "https://cdn.modrinth.com/b.jar" },
            },
            new { path = ConfigFile, hashes = new { sha1 = "c" }, downloads = new[] { "https://cdn.modrinth.com/c.jar" } },
            new { path = ModsB, hashes = new { sha1 = "d" }, downloads = new[] { "https://cdn.modrinth.com/d.jar" } },
        },
    });

    private static string CurseForgeManifest() => JsonSerializer.Serialize(new
    {
        name = "Forge Pack",
        version = "1.2.0",
        minecraft = new
        {
            version = "1.20.1",
            modLoaders = new[] { new { id = "forge-47.4.0", primary = true } },
        },
        files = new object[]
        {
            new { projectID = 101, fileID = 1001, required = true },
            new { projectID = 102, fileID = 1002 },
            new { projectID = 103, fileID = 1003, required = false },
            new { projectID = 104, fileID = 0, required = true },
        },
    });

    private static byte[] CreateMrpackPayload()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "modrinth.index.json", ModrinthIndex());
            WriteEntry(archive, "overrides/config/test.txt", "hello");
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static FakeModrinthClient CreateModrinthClient(int payloadSize)
    {
        var client = new FakeModrinthClient();
        var version = new ModrinthVersion
        {
            Id = "ver1",
            ProjectId = "testpack",
            VersionNumber = "1.0.0",
            VersionType = "release",
            GameVersions = ["1.21.1"],
            Loaders = ["fabric"],
            DatePublished = DateTimeOffset.UtcNow,
            Files =
            [
                new ModrinthFile
                {
                    Url = "https://cdn.modrinth.com/data/testpack/versions/ver1/test.mrpack",
                    Filename = "test.mrpack",
                    Primary = true,
                    Size = payloadSize,
                },
            ],
        };
        client.Versions["testpack"] = [version];
        client.VersionsById["ver1"] = version;
        return client;
    }

    private static ContentHit CreateModpackHit() => new(
        ContentSource.Modrinth,
        "testpack",
        "testpack",
        "Test Pack",
        "A test modpack",
        "author",
        100,
        DateTimeOffset.UtcNow,
        "https://modrinth.com/modpack/testpack",
        null,
        ContentKind.ModPack);

    private static ContentVersion CreateModpackVersion(int payloadSize) => new(
        ContentSource.Modrinth,
        "testpack",
        "ver1",
        "1.0.0",
        ["1.21.1"],
        ["fabric"],
        "test.mrpack",
        payloadSize,
        DateTimeOffset.UtcNow);
}
