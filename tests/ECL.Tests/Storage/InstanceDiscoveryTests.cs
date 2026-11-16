// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Storage;
using Ecl.Core.Storage.Discovery;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Storage;

/// <summary>F-02 S3/S4/S5/S6：持久发现目录、只读扫描、跨根去重与外部实例导入。</summary>
public sealed class InstanceDiscoveryTests
{
    [Fact]
    public async Task ScanPersistsCatalogAndKeepsDiscoveryIdsStable()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var source = CreateVersionedRoot(environment, "hmcl", ("1.20.1", true));
        try
        {
            Assert.True((await workspace.RegisterExternalAsync(source)).IsSuccess);

            var first = await discovery.ScanAsync();
            Assert.True(first.IsSuccess, first.Error?.ToString());
            var catalog = (await discovery.GetCatalogAsync(includeInactive: true)).Value!;
            var entry = Assert.Single(catalog.Entries);
            Assert.Equal(DiscoveryEntryKind.ExternalVersioned, entry.Kind);
            Assert.Equal("1.20.1", entry.GameVersion);
            Assert.Equal("fabric", entry.Loader);

            var second = await discovery.ScanAsync();
            Assert.True(second.IsSuccess);
            var again = (await discovery.GetCatalogAsync(includeInactive: true)).Value!;
            var entryAgain = Assert.Single(again.Entries);
            Assert.Equal(entry.DiscoveryId, entryAgain.DiscoveryId);
            Assert.True(File.Exists(Path.Combine(
                environment.Root,
                "config",
                "instance-engine",
                "discovery-catalog.json")));
        }
        finally
        {
            TryDelete(source);
        }
    }

    [Fact]
    public async Task ScanDoesNotWriteSourceDirectory()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var source = CreateVersionedRoot(environment, "pcl", ("1.19.2", true));
        try
        {
            var before = Snapshot(source);
            Assert.True((await workspace.RegisterExternalAsync(source)).IsSuccess);
            var scan = await discovery.ScanAsync();
            Assert.True(scan.IsSuccess);

            Assert.Equal(before, Snapshot(source));
            Assert.False(Directory.Exists(Path.Combine(source, ".ecl")));
            Assert.Empty(Directory.EnumerateFiles(source, ".ecl-*", SearchOption.AllDirectories));
        }
        finally
        {
            TryDelete(source);
        }
    }

    [Fact]
    public async Task MissingRootKeepsEntriesAndReportsSourceMissing()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var source = CreateVersionedRoot(environment, "removable", ("1.20.1", true));
        Assert.True((await workspace.RegisterExternalAsync(source)).IsSuccess);
        Assert.True((await discovery.ScanAsync()).IsSuccess);
        Directory.Delete(source, recursive: true);

        var second = await discovery.ScanAsync();
        Assert.True(second.IsSuccess);
        var report = Assert.Single(second.Value!.Roots);
        Assert.Equal(WorkspaceStatus.Missing, report.RootStatus);
        Assert.Contains(report.Errors, error => error.Code == "source.missing");

        var catalog = (await discovery.GetCatalogAsync(includeInactive: true)).Value!;
        var entry = Assert.Single(catalog.Entries);
        Assert.Equal(DiscoveryEntryStatus.SourceMissing, entry.Status);
        Assert.True(entry.LastSeenAt <= DateTimeOffset.UtcNow);
        Assert.Equal(
            WorkspaceStatus.Missing,
            workspace.Roots.Single(root => root.Path == source).Status);
    }

    [Fact]
    public async Task UnknownStructureIsReportedAndNotImported()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var source = NewTempDirectory("unknown");
        Directory.CreateDirectory(Path.Combine(source, "random"));
        try
        {
            Assert.True((await workspace.RegisterExternalAsync(source)).IsSuccess);
            var scan = await discovery.ScanAsync();

            Assert.True(scan.IsSuccess);
            var report = Assert.Single(scan.Value!.Roots);
            Assert.Equal(InstanceRootStructure.Unknown, report.Structure);
            Assert.Contains(report.Errors, error => error.Code == "source.unknown_structure");
            Assert.Empty((await discovery.GetCatalogAsync(includeInactive: true)).Value!.Entries);
            Assert.Empty((await provider.GetRequiredService<IInstanceService>().GetInstancesAsync()));
        }
        finally
        {
            TryDelete(source);
        }
    }

    [Fact]
    public async Task NonIsolatedMultiVersionRootIsSharedCandidateAndNotLaunchable()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var source = CreateVersionedRoot(
            environment,
            "shared",
            ("1.20.1", false),
            ("1.20.4", false));
        try
        {
            Assert.True((await workspace.RegisterExternalAsync(source)).IsSuccess);
            var scan = await discovery.ScanAsync();
            Assert.True(scan.IsSuccess);

            var catalog = (await discovery.GetCatalogAsync(includeInactive: true)).Value!;
            Assert.Equal(2, catalog.Entries.Count);
            Assert.All(catalog.Entries, entry => Assert.Equal(DiscoveryEntryKind.SharedRootCandidate, entry.Kind));

            var import = await provider.GetRequiredService<IExternalInstanceImportService>()
                .ImportAsync(catalog.Entries[0].DiscoveryId);
            Assert.False(import.IsSuccess);
            Assert.Equal("discovery.not_importable", import.Error!.Code);
        }
        finally
        {
            TryDelete(source);
        }
    }

    [Fact]
    public async Task SameMarkerOnDifferentPathsIsIdentityConflict()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var first = CreateVersionedRootWithMarker(environment, "first", [("1.20.1", true)], "ins-shared");
        var second = CreateVersionedRootWithMarker(environment, "second", [("1.20.1", true)], "ins-shared");
        try
        {
            Assert.True((await workspace.RegisterExternalAsync(first)).IsSuccess);
            Assert.True((await workspace.RegisterExternalAsync(second)).IsSuccess);
            var scan = await discovery.ScanAsync();
            Assert.True(scan.IsSuccess);

            var catalog = (await discovery.GetCatalogAsync(includeInactive: true)).Value!;
            Assert.Equal(2, catalog.Entries.Count);
            Assert.All(catalog.Entries, entry =>
            {
                Assert.Equal(DiscoveryEntryStatus.IdentityConflict, entry.Status);
                Assert.Equal("discovery.identity_conflict", entry.ProblemCode);
            });
        }
        finally
        {
            TryDelete(first);
            TryDelete(second);
        }
    }

    [Fact]
    public async Task CorruptCatalogIsArchivedAndRebuilt()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var catalogPath = Path.Combine(environment.Root, "config", "instance-engine", "discovery-catalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        File.WriteAllText(catalogPath, "{ not json");

        var result = await discovery.GetCatalogAsync(includeInactive: true);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Entries);
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.GetDirectoryName(catalogPath)!,
            "discovery-catalog.corrupt-*.json"));
    }

    [Fact]
    public async Task CancelledScanDoesNotCommitAndLeavesOldCatalog()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var source = CreateVersionedRoot(environment, "cancel", ("1.20.1", true));
        try
        {
            Assert.True((await workspace.RegisterExternalAsync(source)).IsSuccess);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var scan = await discovery.ScanAsync(cancellationToken: cancellation.Token);

            Assert.False(scan.IsSuccess);
            Assert.Equal("source.cancelled", scan.Error!.Code);
            var catalog = (await discovery.GetCatalogAsync(includeInactive: true)).Value!;
            Assert.Empty(catalog.Entries);
        }
        finally
        {
            TryDelete(source);
        }
    }

    [Fact]
    public async Task LimitReachedIsReportedAsIncomplete()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var source = CreateVersionedRoot(
            environment,
            "many",
            ("1.20.1", true),
            ("1.20.2", true),
            ("1.20.3", true));
        try
        {
            Assert.True((await workspace.RegisterExternalAsync(source)).IsSuccess);
            var scan = await discovery.ScanAsync(new DiscoveryScanRequest { MaxEntriesPerRoot = 1 });

            Assert.True(scan.IsSuccess);
            var report = Assert.Single(scan.Value!.Roots);
            Assert.True(report.SkippedByLimit > 0);
            Assert.False(scan.Value.IsComplete);
            Assert.Contains(report.Errors, error => error.Code == "source.limit_reached");
            var catalog = (await discovery.GetCatalogAsync(includeInactive: true)).Value!;
            Assert.Single(catalog.Entries);
        }
        finally
        {
            TryDelete(source);
        }
    }

    [Fact]
    public async Task ImportWritesOnlyLocalRecordAndKeepsExternalDirectoryUntouched()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var primary = NewTempDirectory("primary");
        var source = CreateVersionedRoot(environment, "hmcl", ("1.20.1", true));
        try
        {
            Assert.True((await workspace.InitializeAsync(primary)).IsSuccess);
            Assert.True((await workspace.RegisterExternalAsync(source)).IsSuccess);
            Assert.True((await discovery.ScanAsync()).IsSuccess);
            var entry = (await discovery.GetCatalogAsync(includeInactive: true)).Value!.Entries.Single();
            var before = Snapshot(source);

            var imported = await provider.GetRequiredService<IExternalInstanceImportService>()
                .ImportAsync(entry.DiscoveryId);

            Assert.True(imported.IsSuccess, imported.Error?.ToString());
            var instance = imported.Value!;
            Assert.Equal(PathIdentity.Normalize(entry.GameDirectory), PathIdentity.Normalize(instance.GameDirectory));
            Assert.Equal("1.20.1", instance.GameVersion);
            Assert.True(Directory.Exists(Path.Combine(primary, "instances", instance.Id)));
            Assert.True(File.Exists(Path.Combine(primary, "instances", instance.Id, "instance.json")));
            Assert.Equal(before, Snapshot(source));
            Assert.False(Directory.Exists(Path.Combine(source, ".ecl")));

            var refreshed = (await discovery.GetCatalogAsync(includeInactive: true)).Value!.Entries.Single();
            Assert.Equal(instance.Id, refreshed.ImportedInstanceId);

            var duplicate = await provider.GetRequiredService<IExternalInstanceImportService>()
                .ImportAsync(entry.DiscoveryId);
            Assert.False(duplicate.IsSuccess);
            Assert.Equal("instance.already_imported", duplicate.Error!.Code);
        }
        finally
        {
            TryDelete(source);
        }
    }

    [Fact]
    public async Task ManagedForeignEntryRequiresAdoptionAndCannotBeImported()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var source = NewTempDirectory("other-ecl");
        var record = Path.Combine(source, "instances", "demo-abc123");
        Directory.CreateDirectory(record);
        File.WriteAllText(
            Path.Combine(record, "instance.json"),
            JsonSerializer.Serialize(new
            {
                id = "demo-abc123",
                name = "其他工作区实例",
                gameVersion = "1.20.1",
                versionId = "1.20.1",
            }));
        try
        {
            Assert.True((await workspace.RegisterExternalAsync(source)).IsSuccess);
            Assert.True((await discovery.ScanAsync()).IsSuccess);

            var entry = (await discovery.GetCatalogAsync(includeInactive: true)).Value!.Entries.Single();
            Assert.Equal(DiscoveryEntryKind.ManagedForeign, entry.Kind);
            Assert.Equal(DiscoveryEntryStatus.AdoptionRequired, entry.Status);

            var import = await provider.GetRequiredService<IExternalInstanceImportService>()
                .ImportAsync(entry.DiscoveryId);
            Assert.False(import.IsSuccess);
            Assert.Equal("discovery.adoption_required", import.Error!.Code);
        }
        finally
        {
            TryDelete(source);
        }
    }

    [Fact]
    public async Task ForgetRootEntriesRemovesOnlyDiscoveryEntries()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var discovery = provider.GetRequiredService<IInstanceDiscoveryService>();
        var source = CreateVersionedRoot(environment, "forget", ("1.20.1", true));
        try
        {
            Assert.True((await workspace.RegisterExternalAsync(source)).IsSuccess);
            Assert.True((await discovery.ScanAsync()).IsSuccess);
            var rootId = workspace.Roots.Single(root => root.Path == source).Id;

            var forgotten = await discovery.ForgetRootEntriesAsync(rootId);

            Assert.True(forgotten.IsSuccess);
            Assert.Equal(1, forgotten.Value);
            Assert.Empty((await discovery.GetCatalogAsync(includeInactive: true)).Value!.Entries);
            Assert.True(Directory.Exists(source));
        }
        finally
        {
            TryDelete(source);
        }
    }

    private static string CreateVersionedRoot(
        TestEnvironment environment,
        string name,
        params (string VersionId, bool Isolated)[] versions) =>
        CreateVersionedRootWithMarker(environment, name, versions, markerInstanceId: null);

    private static string CreateVersionedRootWithMarker(
        TestEnvironment environment,
        string name,
        IReadOnlyList<(string VersionId, bool Isolated)> versions,
        string? markerInstanceId)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ecl-f02-discovery",
            Guid.NewGuid().ToString("N"),
            name);
        Directory.CreateDirectory(root);
        foreach (var (versionId, isolated) in versions)
        {
            var directory = Path.Combine(root, "versions", versionId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, versionId + ".json"),
                JsonSerializer.Serialize(new
                {
                    id = versionId == "1.20.1" ? "fabric-loader-0.16.0-1.20.1" : versionId,
                    clientVersion = versionId,
                    inheritsFrom = versionId,
                }));
            if (isolated || markerInstanceId is not null)
            {
                Directory.CreateDirectory(Path.Combine(directory, "mods"));
            }

            if (markerInstanceId is not null)
            {
                var markerDirectory = Path.Combine(directory, ".ecl");
                Directory.CreateDirectory(markerDirectory);
                File.WriteAllText(
                    Path.Combine(markerDirectory, "instance.json"),
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        instanceId = markerInstanceId,
                        createdAt = DateTimeOffset.UtcNow,
                    }));
            }
        }

        return root;
    }

    private static string NewTempDirectory(string name)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ecl-f02-discovery-temp",
            Guid.NewGuid().ToString("N"),
            name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Snapshot(string directory)
    {
        var entries = Directory
            .EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var info = new FileInfo(path);
                var length = Directory.Exists(path) ? -1 : info.Length;
                return $"{path}|{length}|{info.LastWriteTimeUtc:O}|{info.Attributes}";
            });
        return string.Join("\n", entries);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }
}
