// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Engine.Lifecycle;
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
        Touch(Path.Combine(_root, "downloads", "fresh.part"), DateTime.UtcNow.AddMinutes(-1));
        Touch(Path.Combine(_root, "cache", "thumbnails", "a.png"), old);
        Touch(Path.Combine(_root, "logs", "ecl-20200101.log"), old);
        Touch(Path.Combine(_root, "logs", "crash-20200101.log"), old);
        Touch(Path.Combine(_root, "logs", "sessions", "20260101-000000-aaaaaa", "manifest.json"), old);
        Touch(Path.Combine(_root, "backups", "worlds", "inst1", "a.zip"), old);
        Touch(Path.Combine(_root, "config", "global.json"), old);
        Touch(Path.Combine(_root, "Runtimes", "jdk-21", "bin", "java.exe"), old);

        var inventory = await CreateService(instance).ScanAsync();

        Assert.Equal(20, inventory.Categories.Count);
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
        // F-07：陈旧 .part（无活动下载、超过保留期）可回收；新鲜 .part 仍按“下载中”保护。
        var stalePartial = downloads.Entries.Single(entry => entry.FileName == "active.part");
        Assert.True(stalePartial.CanClean);
        Assert.Equal("staleDownloadPart", stalePartial.CleanupRule);
        var freshPartial = downloads.Entries.Single(entry => entry.FileName == "fresh.part");
        Assert.False(freshPartial.CanClean);
        Assert.Equal("inProgressDownload", freshPartial.ProtectionReason);

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
    public async Task LifecycleMaterialsAndDetachedDataAreReportedButNeverCleanable()
    {
        var instance = CreateInstance();
        var trash = Path.Combine(_gameRoot, ".ecl-trash", "inst1-plan");
        Touch(Path.Combine(trash, "instance.json"), DateTime.UtcNow.AddDays(-30));
        var retired = Path.Combine(_gameRoot, ".ecl-retired", "inst1-plan");
        Touch(Path.Combine(retired, "minecraft", "options.txt"), DateTime.UtcNow.AddDays(-30));
        var staging = Path.Combine(_gameRoot, "instances", ".ecl-migrate-plan");
        Touch(Path.Combine(staging, "instance.json"), DateTime.UtcNow.AddDays(-30));
        Touch(
            Path.Combine(_root, "config", "instance-engine", "transactions", "inst1", "tx.json"),
            DateTime.UtcNow.AddDays(-30));
        Touch(
            Path.Combine(_root, "cache", "instance-engine", "recovery", "inst1", "before.bin"),
            DateTime.UtcNow.AddDays(-30));
        Touch(
            Path.Combine(_gameRoot, "instances", "orphan-instance", "minecraft", "saves", "world", "level.dat"),
            DateTime.UtcNow.AddDays(-30));

        var inventory = await CreateService(instance).ScanAsync();

        var lifecycle = Category(inventory, "ecl.lifecycleMaterials");
        Assert.Contains(lifecycle.Entries, entry => entry.FullPath == trash);
        Assert.Contains(lifecycle.Entries, entry => entry.FullPath == retired);
        Assert.Contains(lifecycle.Entries, entry => entry.FullPath == staging);
        Assert.All(lifecycle.Entries, entry =>
        {
            Assert.False(entry.CanClean);
            Assert.Equal("lifecycleMaterial", entry.ProtectionReason);
        });

        var engine = Category(inventory, "ecl.instanceEngine");
        Assert.Contains(engine.Entries, entry => entry.FileName == "instance-engine");
        Assert.All(engine.Entries, entry => Assert.False(entry.CanClean));

        var detached = Category(inventory, "ecl.detachedGameData");
        var orphan = Assert.Single(detached.Entries);
        Assert.Equal("orphan-instance", orphan.InstanceId);
        Assert.False(orphan.CanClean);
        Assert.Equal("detachedGameData", orphan.ProtectionReason);

        // 供 F-04 使用的 legacy 恢复材料不能落入 14 天可删的元数据缓存分类。
        Assert.DoesNotContain(
            Category(inventory, "ecl.metadataCache").Entries,
            entry => entry.FullPath.Contains("instance-engine", StringComparison.OrdinalIgnoreCase));

        var deletion = await CreateService(instance).DeleteAsync(lifecycle.Entries.Concat(detached.Entries).ToArray());
        Assert.Equal(0, deletion.Succeeded);
        Assert.True(Directory.Exists(trash));
        Assert.True(Directory.Exists(retired));
        Assert.True(Directory.Exists(staging));
        Assert.True(Directory.Exists(Path.Combine(_gameRoot, "instances", "orphan-instance")));
    }

    [Fact]
    public async Task ExternalInstanceUserDataIsNotOfferedForCleanup()
    {
        var managed = CreateInstance();
        var externalRoot = Path.Combine(_root, "external", "game");
        var external = new GameInstance
        {
            Id = "external-inst",
            Name = "External",
            GameVersion = "1.20.1",
            VersionId = "1.20.1",
            GameDirectory = externalRoot,
        };
        Touch(Path.Combine(externalRoot, "logs", "old.log"), DateTime.UtcNow.AddDays(-30));
        Touch(Path.Combine(externalRoot, "screenshots", "a.png"), DateTime.UtcNow.AddDays(-30));
        Touch(Path.Combine(externalRoot, "saves", "world", "level.dat"), DateTime.UtcNow.AddDays(-30));
        Touch(Path.Combine(managed.GameDirectory!, "logs", "old.log"), DateTime.UtcNow.AddDays(-30));

        var inventory = await CreateService(managed, external).ScanAsync();

        var externalEntries = inventory.Categories
            .SelectMany(category => category.Entries)
            .Where(entry => string.Equals(entry.InstanceId, "external-inst", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(externalEntries);
        Assert.All(externalEntries, entry =>
        {
            Assert.False(entry.CanClean);
            Assert.Equal("externalInstance", entry.ProtectionReason);
        });

        // 托管实例的同类数据保持原有可清理行为。
        var managedLog = Category(inventory, "instance.logs").Entries
            .Single(entry => entry.FullPath.EndsWith("old.log", StringComparison.Ordinal) &&
                             entry.InstanceId == managed.Id);
        Assert.True(managedLog.CanClean);
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

    /// <summary>
    /// F-07 活动门禁扩展：实例事务进行中（含 NeedsRecovery）时，即使条目满足年龄策略也不得删除。
    /// </summary>
    [Fact]
    public async Task InstanceWithPendingTransactionIsProtectedFromCleanup()
    {
        var instance = CreateInstance();
        var log = Touch(
            Path.Combine(instance.GameDirectory!, "logs", "old.log"),
            DateTime.UtcNow.AddDays(-30));
        var service = new StorageInventoryService(
            new AppPaths(_root, _gameRoot),
            new FakeInstanceService([instance]),
            new Ecl.Download.DownloadArtifactLifecycle(),
            new GameSessionService(new AppPaths(_root, _gameRoot), NullLogger<GameSessionService>.Instance),
            NullLogger<StorageInventoryService>.Instance,
            workspace: null,
            activityGuard: new FakeActivityGuard(instance.Id, pendingTransaction: true));

        var inventory = await service.ScanAsync();
        var entry = Category(inventory, "instance.logs").Entries.Single(item => item.FileName == "old.log");
        Assert.True(entry.CanClean);

        var result = await service.DeleteAsync([entry]);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("activeInstanceOperation", Assert.Single(result.Items).Reason);
        Assert.True(File.Exists(log));
    }

    private StorageInventoryService CreateService(params GameInstance[] instances) =>
        new(
            new AppPaths(_root, _gameRoot),
            new FakeInstanceService(instances),
            new Ecl.Download.DownloadArtifactLifecycle(),
            new GameSessionService(new AppPaths(_root, _gameRoot), NullLogger<GameSessionService>.Instance),
            NullLogger<StorageInventoryService>.Instance);

    private sealed class FakeActivityGuard(string instanceId, bool pendingTransaction) : IInstanceActivityGuard
    {
        public Task<Result<InstanceActivityGuardState>> InspectAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<InstanceActivityGuardState>.Ok(new InstanceActivityGuardState(
                instanceId,
                Exists: true,
                HasActiveSession: false,
                SessionId: null,
                ProcessId: null,
                ProcessAlive: false,
                HasPendingTransaction: pendingTransaction,
                pendingTransaction
                    ? Ecl.Minecraft.Engine.Transactions.InstanceActivityState.Writing
                    : Ecl.Minecraft.Engine.Transactions.InstanceActivityState.Idle,
                IdentityConflict: false,
                Detail: null)));
    }

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
