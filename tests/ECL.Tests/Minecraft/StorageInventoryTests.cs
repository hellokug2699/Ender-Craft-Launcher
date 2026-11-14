// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Storage;
using Ecl.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 存储清单（B7 / ADR 0022、storage-management.md）：固定分类、未知大小不当作 0、
/// 用户数据默认受保护、白名单之外的项目即使勾选也不能删除。
/// </summary>
public sealed class StorageInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ecl-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly string _gameRoot;

    public StorageInventoryTests()
    {
        _gameRoot = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(_gameRoot);
    }

    [Fact]
    public async Task ScanClassifiesFixedCategoriesAndProtectsUserData()
    {
        var instance = CreateInstance();
        var old = DateTime.UtcNow.AddDays(-30);
        Touch(Path.Combine(instance.GameDirectory!, "logs", "old.log"), old);
        Touch(Path.Combine(instance.GameDirectory!, "logs", "new.log"), DateTime.UtcNow.AddDays(-1));
        Touch(Path.Combine(instance.GameDirectory!, "mods", "a.jar"), DateTime.UtcNow.AddDays(-2));
        Touch(Path.Combine(instance.GameDirectory!, "saves", "world", "level.dat"), DateTime.UtcNow.AddDays(-2));
        Touch(Path.Combine(instance.GameDirectory!, "config", "a.txt"), DateTime.UtcNow.AddDays(-2));
        Touch(Path.Combine(instance.GameDirectory!, "screenshots", "a.png"), DateTime.UtcNow.AddDays(-2));
        Touch(Path.Combine(instance.GameDirectory!, "crash-reports", "crash-1.txt"), DateTime.UtcNow.AddDays(-2));
        Touch(Path.Combine(_root, "downloads", "done.zip"), old);
        Touch(Path.Combine(_root, "downloads", "active.part"), old);
        Touch(Path.Combine(_root, "cache", "thumbnails", "a.png"), old);
        Touch(Path.Combine(_root, "logs", "sessions", "20260101-000000-aaaaaa", "manifest.json"), old);
        Touch(Path.Combine(_root, "backups", "worlds", "inst1", "a.zip"), old);

        var inventory = await CreateService(instance).ScanAsync();

        Assert.Equal(32, inventory.Categories.Count);
        var instanceLogs = Category(inventory, "instance.logs");
        Assert.Equal(2, instanceLogs.Entries.Count);
        Assert.True(instanceLogs.Entries.Single(entry => entry.FileName == "old.log").CanClean);
        var newLog = instanceLogs.Entries.Single(entry => entry.FileName == "new.log");
        Assert.False(newLog.CanClean);
        Assert.Equal("tooRecent", newLog.ProtectionReason);

        Assert.False(Category(inventory, "instance.mods").Entries.Single().CanClean);
        Assert.Equal("userData", Category(inventory, "instance.saves").Entries.Single().ProtectionReason);
        Assert.False(Category(inventory, "instance.crashReports").Entries.Single().CanClean);

        var downloads = Category(inventory, "ecl.downloads");
        Assert.True(downloads.Entries.Single(entry => entry.FileName == "done.zip").CanClean);
        var partial = downloads.Entries.Single(entry => entry.FileName == "active.part");
        Assert.False(partial.CanClean);
        Assert.Equal("inProgressDownload", partial.ProtectionReason);

        Assert.True(Category(inventory, "ecl.imageCache").Entries.Single().CanClean);
        Assert.Equal(
            "sessionEvidence",
            Category(inventory, "ecl.diagnostics").Entries.Single().ProtectionReason);
        Assert.Equal("backup", Category(inventory, "backups.world").Entries.Single().ProtectionReason);

        Assert.NotEmpty(inventory.Disks);
        Assert.True(inventory.KnownEclBytes > 0);
        Assert.Equal(1, inventory.InstanceCount);
    }

    [Fact]
    public async Task DeleteRejectsProtectedRecentAndOutsidePaths()
    {
        var instance = CreateInstance();
        var oldLog = Touch(Path.Combine(instance.GameDirectory!, "logs", "old.log"), DateTime.UtcNow.AddDays(-30));
        var newLog = Touch(Path.Combine(instance.GameDirectory!, "logs", "new.log"), DateTime.UtcNow.AddDays(-1));
        var mod = Touch(Path.Combine(instance.GameDirectory!, "mods", "a.jar"), DateTime.UtcNow.AddDays(-30));
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "outside");
        var inventory = await CreateService(instance).ScanAsync();
        var entries = inventory.Categories.SelectMany(category => category.Entries).ToArray();

        var result = await CreateService(instance).DeleteAsync(
        [
            entries.Single(entry => entry.FullPath == mod),
            entries.Single(entry => entry.FullPath == newLog),
            entries.Single(entry => entry.FullPath == oldLog),
            new StorageFileEntry
            {
                Id = "file:" + outside,
                CategoryId = "ecl.downloads",
                Group = StorageGroup.EclOwn,
                FullPath = outside,
                AllowedRoot = Path.Combine(_root, "downloads"),
                Size = 7,
                CanClean = true,
            },
        ]);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(3, result.Skipped);
        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(newLog));
        Assert.True(File.Exists(mod));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task MissingFileIsSkippedWithoutThrowing()
    {
        var instance = CreateInstance();
        var path = Touch(Path.Combine(instance.GameDirectory!, "logs", "old.log"), DateTime.UtcNow.AddDays(-30));
        var inventory = await CreateService(instance).ScanAsync();
        var entry = inventory.Categories.SelectMany(category => category.Entries).Single(item => item.FullPath == path);
        File.Delete(path);

        var result = await CreateService(instance).DeleteAsync([entry]);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("missing", Assert.Single(result.Items).Reason);
    }

    private StorageInventoryService CreateService(GameInstance instance) =>
        new(
            new AppPaths(_root, _gameRoot),
            new FakeInstanceService([instance]),
            new Ecl.Download.DownloadArtifactLifecycle(),
            new GameSessionService(new AppPaths(_root, _gameRoot), NullLogger<GameSessionService>.Instance),
            NullLogger<StorageInventoryService>.Instance);

    private static StorageCategory Category(StorageInventory inventory, string id) =>
        inventory.Categories.Single(category => category.Id == id);

    private GameInstance CreateInstance()
    {
        var gameDirectory = Path.Combine(_gameRoot, "instances", "inst1", "minecraft");
        Directory.CreateDirectory(gameDirectory);
        return new GameInstance
        {
            Id = "inst1",
            Name = "Inst",
            GameVersion = "1.20.1",
            VersionId = "1.20.1",
            Loader = "fabric",
            GameDirectory = gameDirectory,
        };
    }

    private static string Touch(string path, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "payload");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响断言。
        }
    }

    private sealed class FakeInstanceService(IReadOnlyList<GameInstance> instances) : IInstanceService
    {
        public Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(instances);

        public Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> CreateAsync(
            string name,
            string gameVersion,
            string loader = "vanilla",
            string? loaderVersion = null,
            string? fabricApiVersion = null,
            string? optiFineVersion = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<GameInstance>> UpdateConfigurationAsync(
            string instanceId,
            string gameVersion,
            string loader,
            string? loaderVersion = null,
            string? fabricApiVersion = null,
            string? optiFineVersion = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<GameInstance>> RestoreConfigurationAsync(
            GameInstance restorePoint,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> DeleteAsync(string instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstanceDeletionPlan>> GetDeletionPlanAsync(
            string instanceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> DeleteAsync(
            string instanceId,
            InstanceDeleteOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<DetectedVersion>> ScanVersionsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> ImportAsync(
            string versionId,
            string? name = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task TouchLastPlayedAsync(string instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> RelocateAsync(
            string instanceId,
            string newGameDirectory,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<GameInstance>> CloneAsync(
            string sourceInstanceId,
            string name,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
