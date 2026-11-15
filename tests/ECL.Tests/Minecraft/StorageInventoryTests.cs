// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Storage;
using Ecl.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 存储清理清单（ADR 0022）：列表只包含可安全重建的垃圾，模组/配置/资源包等用户内容
/// 与基础运行文件只计入磁盘占用；崩溃报告、截图、历史日志、服务器资源包缓存与
/// 世界存档文件夹可清理；存档以文件夹为单位，不拆成 .mca 文件。
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
    public async Task ScanListsOnlyCleanupCategoriesAndProtectsUserData()
    {
        var instance = CreateInstance();
        var game = instance.GameDirectory!;
        var old = DateTime.UtcNow.AddDays(-30);
        Touch(Path.Combine(game, "logs", "old.log"), old);
        Touch(Path.Combine(game, "logs", "latest.log"), DateTime.UtcNow.AddMinutes(-1));
        Touch(Path.Combine(game, "logs", "debug", "debug.log"), old);
        Touch(Path.Combine(game, "mods", "a.jar"), old);
        Touch(Path.Combine(game, "config", "a.txt"), old);
        Touch(Path.Combine(game, "resourcepacks", "pack.zip"), old);
        Touch(Path.Combine(game, "shaderpacks", "shader.zip"), old);
        Touch(Path.Combine(game, "datapacks", "pack.zip"), old);
        Touch(Path.Combine(game, "schematics", "build.schem"), old);
        Touch(Path.Combine(game, "versions", "1.20.1", "1.20.1.json"), old);
        Touch(Path.Combine(game, "screenshots", "a.png"), old);
        Touch(Path.Combine(game, "crash-reports", "crash-1.txt"), old);
        Touch(Path.Combine(game, "server-resource-packs", "server.zip"), old);
        Touch(Path.Combine(game, "saves", "world", "level.dat"), old);
        Touch(Path.Combine(game, "saves", "world", "region", "r.0.0.mca"), old);
        Touch(Path.Combine(_root, "downloads", "done.zip"), old);
        Touch(Path.Combine(_root, "downloads", "active.part"), old);
        Touch(Path.Combine(_root, "cache", "thumbnails", "a.png"), old);
        Touch(Path.Combine(_root, "logs", "ecl-20200101.log"), old);
        Touch(Path.Combine(_root, "logs", "crash-20200101.log"), old);
        Touch(Path.Combine(_root, "logs", "sessions", "20260101-000000-aaaaaa", "manifest.json"), old);
        Touch(Path.Combine(_root, "backups", "worlds", "inst1", "a.zip"), old);
        Touch(Path.Combine(_root, "config", "global.json"), old);
        Touch(Path.Combine(_root, "Runtimes", "jdk-21", "bin", "java.exe"), old);

        var inventory = await CreateService(instance).ScanAsync();

        Assert.Equal(17, inventory.Categories.Count);
        Assert.Equal(
            StorageCategorySpec.All.Select(spec => spec.Id).OrderBy(id => id, StringComparer.Ordinal),
            inventory.Categories.Select(category => category.Id).OrderBy(id => id, StringComparer.Ordinal));

        var instanceLogs = Category(inventory, "instance.logs");
        Assert.True(instanceLogs.Entries.Single(entry => entry.FileName == "old.log").CanClean);
        Assert.True(instanceLogs.Entries.Single(entry => entry.FileName == "latest.log").CanClean);

        // 用户内容与基础运行文件被排除在清理列表之外，但仍计入磁盘占用。
        var listedNames = inventory.Categories
            .SelectMany(category => category.Entries)
            .Select(entry => entry.FileName)
            .ToArray();
        Assert.DoesNotContain("a.jar", listedNames);
        Assert.DoesNotContain("a.txt", listedNames);
        Assert.DoesNotContain("pack.zip", listedNames);
        Assert.DoesNotContain("shader.zip", listedNames);
        Assert.DoesNotContain("build.schem", listedNames);
        Assert.DoesNotContain("1.20.1.json", listedNames);
        Assert.DoesNotContain("java.exe", listedNames);

        // 崩溃报告与截图是可清理垃圾。
        Assert.True(Category(inventory, "instance.crashReports").Entries.Single().CanClean);
        Assert.True(Category(inventory, "instance.screenshots").Entries.Single().CanClean);
        Assert.True(Category(inventory, "instance.serverResourcePacks").Entries.Single().CanClean);

        // 存档以世界文件夹为单位列出，不拆成 .mca 文件。
        var saves = Category(inventory, "instance.saves");
        var world = Assert.Single(saves.Entries);
        Assert.Equal("world", world.FileName);
        Assert.True(world.IsDirectory);
        Assert.True(world.CanClean);
        Assert.Equal(14L, world.Size);
        Assert.DoesNotContain(saves.Entries, entry => entry.FileName.EndsWith(".mca", StringComparison.OrdinalIgnoreCase));

        var downloads = Category(inventory, "ecl.downloads");
        Assert.True(downloads.Entries.Single(entry => entry.FileName == "done.zip").CanClean);
        var partial = downloads.Entries.Single(entry => entry.FileName == "active.part");
        Assert.False(partial.CanClean);
        Assert.Equal("inProgressDownload", partial.ProtectionReason);

        Assert.True(Category(inventory, "ecl.imageCache").Entries.Single().CanClean);
        Assert.True(Category(inventory, "ecl.logs").Entries.Single(entry => entry.FileName == "ecl-20200101.log").CanClean);
        Assert.True(Category(inventory, "ecl.crashReports").Entries.Single(entry => entry.FileName == "crash-20200101.log").CanClean);
        Assert.Equal(
            "sessionEvidence",
            Category(inventory, "ecl.diagnostics").Entries.Single().ProtectionReason);

        Assert.NotEmpty(inventory.Disks);
        Assert.True(inventory.KnownEclBytes > 0);
        Assert.Equal(1, inventory.InstanceCount);
    }

    [Fact]
    public async Task CurrentSessionLogIsProtectedWhilePastLogsAreCleanable()
    {
        var instance = CreateInstance();
        var today = Path.Combine(_root, "logs", $"ecl-{DateTime.Today:yyyyMMdd}.log");
        var yesterday = Path.Combine(_root, "logs", "ecl-20200101.log");
        Touch(today, DateTime.UtcNow);
        Touch(yesterday, DateTime.UtcNow.AddDays(-30));

        var inventory = await CreateService(instance).ScanAsync();
        var logs = Category(inventory, "ecl.logs");

        var current = logs.Entries.Single(entry => entry.FullPath == today);
        Assert.False(current.CanClean);
        Assert.Equal("currentSession", current.ProtectionReason);
        Assert.True(logs.Entries.Single(entry => entry.FullPath == yesterday).CanClean);
    }

    [Fact]
    public async Task DeleteRejectsProtectedAndOutOfScopeEntries()
    {
        var instance = CreateInstance();
        var game = instance.GameDirectory!;
        var oldLog = Touch(Path.Combine(game, "logs", "old.log"), DateTime.UtcNow.AddDays(-30));
        var mod = Touch(Path.Combine(game, "mods", "a.jar"), DateTime.UtcNow.AddDays(-30));
        var today = Touch(Path.Combine(_root, "logs", $"ecl-{DateTime.Today:yyyyMMdd}.log"), DateTime.UtcNow);
        var world = Path.Combine(game, "saves", "world");
        Touch(Path.Combine(world, "level.dat"), DateTime.UtcNow.AddDays(-30));
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "outside");

        var inventory = await CreateService(instance).ScanAsync();
        var entries = inventory.Categories.SelectMany(category => category.Entries).ToArray();
        var worldEntry = entries.Single(entry => entry.IsDirectory && entry.FullPath == world);
        var todayEntry = entries.Single(entry => entry.FullPath == today);

        var result = await CreateService(instance).DeleteAsync(
        [
            entries.Single(entry => entry.FullPath == oldLog),
            worldEntry,
            todayEntry,
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

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(2, result.Skipped);
        Assert.False(File.Exists(oldLog));
        Assert.False(Directory.Exists(world));
        Assert.True(File.Exists(today));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(mod));
        Assert.Contains(result.Items, item => item.Reason == "currentSession");
        Assert.Contains(result.Items, item => item.Reason == "outsideAllowedRoot");
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

    [Fact]
    public async Task NestedDirectoryCannotEscapeTheSavesRoot()
    {
        var instance = CreateInstance();
        var game = instance.GameDirectory!;
        var nested = Path.Combine(game, "saves", "world", "region");
        Touch(Path.Combine(nested, "r.0.0.mca"), DateTime.UtcNow.AddDays(-30));
        var inventory = await CreateService(instance).ScanAsync();
        var saves = Category(inventory, "instance.saves");
        var world = Assert.Single(saves.Entries);

        var tampered = world with
        {
            FullPath = nested,
            AllowedRoot = world.AllowedRoot,
            Id = "dir:" + nested,
        };

        var result = await CreateService(instance).DeleteAsync([tampered]);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Skipped);
        Assert.True(Directory.Exists(nested));
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
