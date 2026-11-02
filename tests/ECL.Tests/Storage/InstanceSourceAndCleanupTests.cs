// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Storage;
using Ecl.Download;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Storage;

public sealed class InstanceSourceAndCleanupTests
{
    [Fact]
    public async Task ExternalSourcesAreScannedIndependentlyAndSameNameIsNotMerged()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var sourceRoot = CreateSourceRoot();
        try
        {
            var sourceA = Path.Combine(sourceRoot, "hmcl");
            var sourceB = Path.Combine(sourceRoot, "pcl");
            CreateVersionedInstance(sourceA, "1.20.1", "1.20.1");
            CreateVersionedInstance(sourceB, "1.20.1", "1.20.1");
            Assert.True((await workspace.RegisterExternalAsync(sourceA)).IsSuccess);
            Assert.True((await workspace.RegisterExternalAsync(sourceB)).IsSuccess);

            var catalog = await provider.GetRequiredService<InstanceSourceService>().ScanAllAsync();
            Assert.Equal(2, catalog.Groups.Count);
            var instances = catalog.Groups.SelectMany(group => group.Instances).Where(instance => instance.FolderName == "1.20.1").ToArray();
            Assert.Equal(2, instances.Length);
            Assert.NotEqual(instances[0].StableId, instances[1].StableId);
            Assert.All(instances, instance => Assert.Equal("1.20.1", instance.GameVersion));
            Assert.All(instances, instance => Assert.False(instance.CanLaunch));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownStructureIsReportedButNotImported()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        var sourceRoot = CreateSourceRoot();
        try
        {
            var source = Path.Combine(sourceRoot, "unknown");
            Directory.CreateDirectory(Path.Combine(source, "random"));
            Assert.True((await workspace.RegisterExternalAsync(source)).IsSuccess);

            var catalog = await provider.GetRequiredService<InstanceSourceService>().ScanAllAsync();
            var group = Assert.Single(catalog.Groups);
            Assert.Empty(group.Instances);
            Assert.Contains("未识别", group.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StorageAnalysisSeparatesCategoriesAndCleanupProtectsWorlds()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var paths = environment.Paths;
        await File.WriteAllBytesAsync(Path.Combine(paths.CacheDirectory, "cache.bin"), new byte[120]);
        await File.WriteAllBytesAsync(Path.Combine(paths.DownloadsDirectory, "download.bin"), new byte[80]);
        Directory.CreateDirectory(Path.Combine(paths.RuntimesDirectory, "jdk21"));
        await File.WriteAllBytesAsync(Path.Combine(paths.RuntimesDirectory, "jdk21", "java.exe"), new byte[40]);

        var instances = provider.GetRequiredService<IInstanceService>();
        var created = await instances.CreateAsync("Cleanup World", "1.20.1");
        Assert.True(created.IsSuccess);
        var instance = created.Value!;
        var gameDirectory = InstancePaths.GetGameDirectory(paths, instance);
        var world = Path.Combine(gameDirectory, "saves", "MyWorld");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "world-data");
        var oldLog = Path.Combine(gameDirectory, "logs", "2026-01-01.log");
        Directory.CreateDirectory(Path.GetDirectoryName(oldLog)!);
        await File.WriteAllTextAsync(oldLog, "old log");
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-30));
        var recentLog = Path.Combine(gameDirectory, "logs", "latest.log");
        await File.WriteAllTextAsync(recentLog, "recent");
        var oldPart = Path.Combine(paths.DownloadsDirectory, "old.bin.part");
        await File.WriteAllBytesAsync(oldPart, new byte[64]);
        File.SetLastWriteTimeUtc(oldPart, DateTime.UtcNow.AddDays(-30));
        var oldCache = Path.Combine(paths.CacheDirectory, "old.cache");
        await File.WriteAllTextAsync(oldCache, "cache");
        File.SetLastWriteTimeUtc(oldCache, DateTime.UtcNow.AddDays(-30));

        var analysis = await provider.GetRequiredService<StorageAnalysisService>().AnalyzeAsync();
        Assert.Contains(analysis.Usages, usage => usage.Id == "downloads" && usage.Bytes >= 80);
        Assert.Contains(analysis.Usages, usage => usage.Id == "runtimes" && usage.Bytes >= 40);
        Assert.True(analysis.KnownBytes > 0);

        var cleanup = provider.GetRequiredService<StorageCleanupService>();
        var preview = await cleanup.PreviewAsync(new StorageCleanupRequest());
        Assert.Contains(preview.Items, item => item.Kind == CleanupItemKind.DownloadPart && item.Path == oldPart);
        Assert.Contains(preview.Items, item => item.Kind == CleanupItemKind.InstanceLog && item.Path == oldLog);
        Assert.DoesNotContain(preview.Items, item => item.Path == recentLog);
        Assert.DoesNotContain(preview.Items, item => item.Path.StartsWith(Path.Combine(gameDirectory, "saves"), StringComparison.OrdinalIgnoreCase));

        var executed = await cleanup.ExecuteAsync(preview);
        Assert.True(executed.Deleted >= 3);
        Assert.False(File.Exists(oldPart));
        Assert.False(File.Exists(oldLog));
        Assert.False(File.Exists(oldCache));
        Assert.True(File.Exists(Path.Combine(world, "level.dat")));
        Assert.True(File.Exists(recentLog));
    }

    [Fact]
    public async Task CleanupRejectsPathOutsideAllowedRoot()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var cleanup = provider.GetRequiredService<StorageCleanupService>();
        var outside = Path.Combine(environment.Root, "outside.txt");
        await File.WriteAllTextAsync(outside, "should-not-delete");
        var item = new CleanupPreviewItem(
            "evil",
            CleanupItemKind.RebuildableCache,
            outside,
            environment.Paths.CacheDirectory,
            15,
            null,
            "test");
        var result = await cleanup.ExecuteAsync(new CleanupPreview([item], 0, []));
        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(outside));
    }

    private static string CreateSourceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecl-source-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreateVersionedInstance(string root, string folder, string gameVersion)
    {
        var directory = Path.Combine(root, "versions", folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, folder + ".json"),
            JsonSerializer.Serialize(new { id = folder, clientVersion = gameVersion }));
    }
}
