// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Ecl.Core;
using Ecl.Download;
using Ecl.Java;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;
using Ecl.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Install;

public sealed class InstallServiceTests
{
    [Fact]
    public async Task InstallsVersionLibrariesAssetsAndLogging()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));

        var instanceResult = await provider.GetRequiredService<IInstanceService>().CreateAsync("Test", "1.21.1");
        Assert.True(instanceResult.IsSuccess);
        var instance = instanceResult.Value!;

        var stages = new List<InstallStage>();
        var progress = new InlineProgress<InstallProgress>(update => stages.Add(update.Stage));

        var result = await provider.GetRequiredService<IVersionInstallService>()
            .InstallAsync(instance.Id, progress);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.Equal("1.21.1", report.VersionId);
        Assert.True(report.DownloadedFiles >= 4);

        Assert.True(File.Exists(VersionFilePlanner.GetVersionJsonPath(environment.Paths, "1.21.1")));
        Assert.True(File.Exists(VersionFilePlanner.GetClientJarPath(environment.Paths, "1.21.1")));
        Assert.True(File.Exists(VersionFilePlanner.GetAssetIndexPath(environment.Paths, "1.21")));
        Assert.True(File.Exists(VersionFilePlanner.GetLoggingFilePath(environment.Paths, "client-1.12.xml")));
        Assert.True(File.Exists(VersionFilePlanner.GetAssetObjectPath(
            environment.Paths,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.LibrariesDirectory,
            "com/example/demo/1.0/demo-1.0.jar".Replace('/', Path.DirectorySeparatorChar))));

        Assert.Contains(InstallStage.Completed, stages);
        Assert.Contains(fake.Requests, request => request.DisplayName == "1.21.1.jar");
        Assert.Contains(fake.Requests, request => request.DisplayName == "demo-1.0.jar");
    }

    [Fact]
    public async Task RejectsUnsupportedLoader()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));

        var instanceResult = await provider.GetRequiredService<IInstanceService>().CreateAsync("Test", "1.21.1");
        Assert.True(instanceResult.IsSuccess);
        var instance = instanceResult.Value! with { Loader = "quilt" };
        await provider.GetRequiredService<IInstanceStore>().SaveAsync(instance);

        var result = await provider.GetRequiredService<IVersionInstallService>()
            .InstallAsync(instance.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("install.unsupported_loader", result.Error?.Code);
    }

    [Fact]
    public async Task InstallsFabricLoaderAndFabricApi()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        var catalog = new FakeLoaderCatalog();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(fake);
            services.AddSingleton<ILoaderCatalog>(catalog);
        });

        var instanceResult = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Fabric", "1.21.1", "fabric", "0.16.0", "0.116.0+1.21.1");
        Assert.True(instanceResult.IsSuccess);

        var stages = new List<InstallStage>();
        var result = await provider.GetRequiredService<IVersionInstallService>()
            .InstallAsync(
                instanceResult.Value!.Id,
                new InlineProgress<InstallProgress>(update => stages.Add(update.Stage)));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Contains(InstallStage.Loader, stages);
        Assert.Contains(InstallStage.FabricApi, stages);

        var versionId = instanceResult.Value.VersionId;
        Assert.True(File.Exists(VersionFilePlanner.GetVersionJsonPath(environment.Paths, versionId)));
        Assert.True(File.Exists(VersionFilePlanner.GetClientJarPath(environment.Paths, versionId)));
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.GetInstanceGameDirectory(instanceResult.Value.Id),
            "mods",
            "fabric-api-0.116.0+1.21.1.jar")));
        Assert.Contains(result.Value!.Components!, component => component.StartsWith("fabric-api:"));
    }

    [Fact]
    public async Task InstallsForgeLoaderByRunningProcessors()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        var runner = new FakeForgeProcessorRunner();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(fake);
            services.AddSingleton<IJavaSelectionService>(new FakeJavaSelectionService());
            services.AddSingleton<IForgeProcessorRunner>(runner);
        });

        var instanceResult = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Forge", "1.21.1", "forge", "47.4.0");
        Assert.True(instanceResult.IsSuccess);
        var instance = instanceResult.Value!;

        var stages = new List<InstallStage>();
        var result = await provider.GetRequiredService<IVersionInstallService>()
            .InstallAsync(instance.Id, new InlineProgress<InstallProgress>(update => stages.Add(update.Stage)));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Contains(InstallStage.Loader, stages);
        Assert.Contains(InstallStage.Processors, stages);
        Assert.True(runner.Called);
        Assert.Equal("47.4.0", runner.LoaderVersion);
        Assert.Contains(fake.Requests, request => request.DisplayName?.EndsWith("-installer.jar", StringComparison.Ordinal) == true);

        var versionPath = VersionFilePlanner.GetVersionJsonPath(environment.Paths, instance.VersionId);
        Assert.True(File.Exists(versionPath));
        var written = File.ReadAllText(versionPath);
        Assert.Contains("cpw.mods.bootstraplauncher.BootstrapLauncher", written, StringComparison.Ordinal);
        Assert.Contains("1.21.1", written, StringComparison.Ordinal);
        Assert.Contains(result.Value!.Components!, component => component == "forge:47.4.0");
    }

    [Fact]
    public async Task InstallsNeoForgeLoaderByRunningProcessors()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        var runner = new FakeForgeProcessorRunner();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(fake);
            services.AddSingleton<IJavaSelectionService>(new FakeJavaSelectionService());
            services.AddSingleton<IForgeProcessorRunner>(runner);
        });

        var instanceResult = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("NeoForge", "1.21.1", "neoforge", "21.1.264");
        Assert.True(instanceResult.IsSuccess);
        var instance = instanceResult.Value!;

        var result = await provider.GetRequiredService<IVersionInstallService>()
            .InstallAsync(instance.Id);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(runner.Called);
        Assert.Contains(result.Value!.Components!, component => component == "neoforge:21.1.264");
    }

    [Fact]
    public async Task InstallsLegacyForgeWithoutJavaOrProcessors()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService(legacyForgeInstaller: true);
        var runner = new FakeForgeProcessorRunner();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(fake);
            services.AddSingleton<IForgeProcessorRunner>(runner);
            services.AddSingleton<IJavaSelectionService>(new ThrowingJavaSelectionService());
        });

        var instanceResult = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Legacy Forge", "1.21.1", "forge", "47.4.0");
        Assert.True(instanceResult.IsSuccess);
        var instance = instanceResult.Value!;

        var result = await provider.GetRequiredService<IVersionInstallService>()
            .InstallAsync(instance.Id);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.False(runner.Called);
        Assert.Contains(result.Value!.Components!, component => component == "forge:47.4.0");
        Assert.True(File.Exists(VersionFilePlanner.GetVersionJsonPath(environment.Paths, instance.VersionId)));
    }

    [Fact]
    public async Task FailedInstallRollsBackNewFilesAndWritesDiagnostics()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService(
            legacyForgeInstaller: true,
            failOnDisplayName: "client-1.12.xml");
        var runner = new FakeForgeProcessorRunner();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(fake);
            services.AddSingleton<IForgeProcessorRunner>(runner);
            services.AddSingleton<IJavaSelectionService>(new ThrowingJavaSelectionService());
        });

        var instanceResult = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Broken Forge", "1.21.1", "forge", "47.4.0");
        Assert.True(instanceResult.IsSuccess);
        var instance = instanceResult.Value!;

        var result = await provider.GetRequiredService<IVersionInstallService>()
            .InstallAsync(instance.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("install.download_failed", result.Error?.Code);

        var operation = Assert.Single(provider.GetRequiredService<IOperationStore>().Snapshots);
        Assert.Equal(OperationState.Failed, operation.State);
        Assert.Equal("install.download_failed", operation.Error?.Code);
        Assert.Equal(OperationErrorKind.Network, operation.Error?.Kind);

        Assert.False(File.Exists(VersionFilePlanner.GetVersionJsonPath(environment.Paths, instance.VersionId)));
        Assert.False(File.Exists(VersionFilePlanner.GetClientJarPath(environment.Paths, instance.VersionId)));
        Assert.False(File.Exists(Path.Combine(
            environment.Paths.LibrariesDirectory,
            "com/example/demo/1.0/demo-1.0.jar".Replace('/', Path.DirectorySeparatorChar))));

        // 基础版本 JSON 现在也属于安装事务（§6.4 决策 a）：本次新建、安装失败时必须删除，
        // 避免回滚后留下“半个共享版本目录”。原本已存在的同版本文件走恢复而不是删除（见下一个测试）。
        Assert.False(File.Exists(VersionFilePlanner.GetVersionJsonPath(environment.Paths, "1.21.1")));
        Assert.False(File.Exists(VersionFilePlanner.GetAssetIndexPath(environment.Paths, "1.21")));

        var diagnostics = Directory.GetFiles(
            Path.Combine(environment.Paths.LogsDirectory, "install"),
            "*.json");
        Assert.Single(diagnostics);
        var content = await File.ReadAllTextAsync(diagnostics[0]);
        Assert.Contains("install.download_failed", content, StringComparison.Ordinal);
        Assert.Contains(instance.Id, content, StringComparison.Ordinal);
    }

    /// <summary>
    /// §6.4 决策 a：共享的 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> 与 <c>assets/indexes/&lt;id&gt;.json</c>
    /// 被本次安装替换时必须在失败后恢复原始内容（可能是其它启动器写入的合并配置）。
    /// </summary>
    [Fact]
    public async Task FailedInstallRestoresReplacedSharedVersionJsonAndAssetIndex()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService(failOnDisplayName: "client-1.12.xml");
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));

        var versionJsonPath = VersionFilePlanner.GetVersionJsonPath(environment.Paths, "1.21.1");
        var assetIndexPath = VersionFilePlanner.GetAssetIndexPath(environment.Paths, "1.21");
        Directory.CreateDirectory(Path.GetDirectoryName(versionJsonPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(assetIndexPath)!);
        const string sharedVersionJson =
            """{"id":"1.21.1","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[]}""";
        const string sharedAssetIndex = """{"objects":{},"marker":"other-launcher"}""";
        await File.WriteAllTextAsync(versionJsonPath, sharedVersionJson);
        await File.WriteAllTextAsync(assetIndexPath, sharedAssetIndex);

        var instance = (await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Shared Files", "1.21.1")).Value!;

        var outcome = await provider.GetRequiredService<IVersionInstallService>()
            .ReconfigureAsync(instance.Id, "1.21.1", "vanilla");

        Assert.False(outcome.IsSuccess);
        Assert.Equal("install.download_failed", outcome.Error?.Code);
        var failure = outcome.Failure!;
        Assert.True(failure.Rollback.IsComplete);
        Assert.False(failure.RecoveryRequired);

        // 回滚必须写回原始字节，而不是留下官方 JSON。
        Assert.Equal(sharedVersionJson, await File.ReadAllTextAsync(versionJsonPath));
        Assert.Equal(sharedAssetIndex, await File.ReadAllTextAsync(assetIndexPath));
        Assert.Contains(
            failure.Rollback.Actions,
            action => action.Contains(versionJsonPath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            failure.Rollback.Actions,
            action => action.Contains(assetIndexPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §6.4 决策 a 的成功路径：共享版本 JSON / 资产索引被校正为新内容，安装提交后保留（不回滚）。
    /// </summary>
    [Fact]
    public async Task SuccessfulInstallKeepsReplacedSharedVersionJsonAndAssetIndex()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));

        var versionJsonPath = VersionFilePlanner.GetVersionJsonPath(environment.Paths, "1.21.1");
        var assetIndexPath = VersionFilePlanner.GetAssetIndexPath(environment.Paths, "1.21");
        Directory.CreateDirectory(Path.GetDirectoryName(versionJsonPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(assetIndexPath)!);
        await File.WriteAllTextAsync(versionJsonPath, """{"id":"1.21.1","mainClass":"stale.example.Main"}""");
        await File.WriteAllTextAsync(assetIndexPath, """{"objects":{},"marker":"stale"}""");

        var instance = (await provider.GetRequiredService<IInstanceService>().CreateAsync("Shared Ok", "1.21.1")).Value!;

        var result = await provider.GetRequiredService<IVersionInstallService>().InstallAsync(instance.Id);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var installedJson = await File.ReadAllTextAsync(versionJsonPath);
        Assert.DoesNotContain("stale.example.Main", installedJson, StringComparison.Ordinal);
        Assert.Contains("net.minecraft.client.main.Main", installedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("stale", await File.ReadAllTextAsync(assetIndexPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedInstallRemovesForgeProcessorOutput()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        var runner = new FakeForgeProcessorRunner(environment.Paths.LibrariesDirectory);
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(fake);
            services.AddSingleton<ILoaderCatalog>(new FakeLoaderCatalog());
            services.AddSingleton<IJavaSelectionService>(new FakeJavaSelectionService());
            services.AddSingleton<IForgeProcessorRunner>(runner);
        });

        var instanceResult = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Forge OptiFine", "1.21.1", "forge", "47.4.0", optiFineVersion: "HD_U:G5");
        Assert.True(instanceResult.IsSuccess);
        var instance = instanceResult.Value!;

        var result = await provider.GetRequiredService<IVersionInstallService>()
            .InstallAsync(instance.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("install.optifine_missing", result.Error?.Code);
        Assert.True(runner.Called);

        var patched = Path.Combine(
            environment.Paths.LibrariesDirectory,
            "com/example/demo/1.0/demo-1.0-client.jar".Replace('/', Path.DirectorySeparatorChar));
        Assert.False(File.Exists(patched));
        Assert.False(File.Exists(VersionFilePlanner.GetVersionJsonPath(environment.Paths, instance.VersionId)));
    }

    [Fact]
    public async Task ReconfigureFailureReportsCompleteRollbackAndRestoredConfiguration()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService(failOnDisplayName: "1.21.1-fabric-0.16.0.jar");
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(fake);
            services.AddSingleton<ILoaderCatalog>(new FakeLoaderCatalog());
        });

        var original = (await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Rollback Target", "1.21.1")).Value!;

        var outcome = await provider.GetRequiredService<IVersionInstallService>()
            .ReconfigureAsync(original.Id, "1.21.1", "fabric", "0.16.0");

        Assert.False(outcome.IsSuccess);
        Assert.Equal("install.download_failed", outcome.Error?.Code);
        var failure = outcome.Failure!;
        Assert.True(failure.MetadataRestored);
        Assert.True(failure.Rollback.IsComplete);
        Assert.False(failure.RecoveryRequired);
        Assert.NotNull(failure.DiagnosticPath);
        Assert.True(File.Exists(failure.DiagnosticPath!));

        // 失败后实例记录（含 VersionId）必须与变更前完全一致，不能重算成新的版本 ID。
        var restored = await provider.GetRequiredService<IInstanceService>().GetAsync(original.Id);
        Assert.NotNull(restored);
        Assert.Equal(original.VersionId, restored!.VersionId);
        Assert.Equal(original.GameVersion, restored.GameVersion);
        Assert.Equal(original.Loader, restored.Loader);
        Assert.Equal(original.LoaderVersion, restored.LoaderVersion);

        // 本次新写入的加载器版本文件必须已删除。
        Assert.False(File.Exists(VersionFilePlanner.GetVersionJsonPath(environment.Paths, "1.21.1-fabric-0.16.0")));
        Assert.False(File.Exists(VersionFilePlanner.GetClientJarPath(environment.Paths, "1.21.1-fabric-0.16.0")));
    }

    [Fact]
    public async Task ReconfigureFailureKeepsTheOriginalVersionIdOfImportedInstances()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService(failOnDisplayName: "1.21.1-fabric-0.16.0.jar");
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(fake);
            services.AddSingleton<ILoaderCatalog>(new FakeLoaderCatalog());
        });

        var created = (await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Imported", "1.21.1")).Value!;
        var imported = created with { VersionId = "custom-imported-version" };
        await provider.GetRequiredService<IInstanceStore>().SaveAsync(imported);

        var outcome = await provider.GetRequiredService<IVersionInstallService>()
            .ReconfigureAsync(imported.Id, "1.21.1", "fabric", "0.16.0");

        Assert.False(outcome.IsSuccess);
        var restored = await provider.GetRequiredService<IInstanceService>().GetAsync(imported.Id);
        Assert.Equal("custom-imported-version", restored!.VersionId);
        Assert.True(outcome.Failure!.MetadataRestored);
    }

    [Fact]
    public async Task ReconfigureSuccessCommitsTargetAndKeepsInstanceId()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IDownloadService>(fake);
            services.AddSingleton<ILoaderCatalog>(new FakeLoaderCatalog());
        });

        var original = (await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Reconfigure Ok", "1.21.1")).Value!;

        var outcome = await provider.GetRequiredService<IVersionInstallService>()
            .ReconfigureAsync(original.Id, "1.21.1", "fabric", "0.16.0");

        Assert.True(outcome.IsSuccess, outcome.Error?.ToString());
        Assert.Null(outcome.Failure);
        Assert.Contains(outcome.Report!.Components!, component => component.StartsWith("fabric-api:", StringComparison.Ordinal));

        var updated = await provider.GetRequiredService<IInstanceService>().GetAsync(original.Id);
        Assert.Equal(original.Id, updated!.Id);
        Assert.Equal("fabric", updated.Loader);
        Assert.Equal("0.16.0", updated.LoaderVersion);
        Assert.Equal("1.21.1-fabric-0.16.0", updated.VersionId);
        Assert.True(File.Exists(VersionFilePlanner.GetVersionJsonPath(environment.Paths, updated.VersionId)));

        var operation = Assert.Single(provider.GetRequiredService<IOperationStore>().Snapshots);
        Assert.Equal(OperationKind.Install, operation.Kind);
        Assert.Equal(OperationState.Completed, operation.State);
    }

    [Fact]
    public void RollbackReportsFailedRestoresInsteadOfClaimingSuccess()
    {
        var root = Directory.CreateTempSubdirectory("ecl-rollback-").FullName;
        try
        {
            var path = Path.Combine(root, "blocked.jar");
            File.WriteAllText(path, "original content");
            var transaction = new InstallTransaction();
            transaction.TrackFileWithBackup(path);

            // 用同名目录占据文件路径：恢复备份在 Windows 与 Linux 上都必须失败。
            // 不能在 Linux 上依赖“文件仍被打开”来制造删除失败——Unix 允许删除打开的文件，
            // 那样测试会在 Linux CI 上误判为回滚成功。
            File.Delete(path);
            Directory.CreateDirectory(path);

            var result = transaction.Rollback();

            Assert.False(result.IsComplete);
            var failure = Assert.Single(result.Failures);
            Assert.Contains("failed to rollback", failure, StringComparison.Ordinal);
            Assert.Contains(path, failure, StringComparison.Ordinal);
            Assert.Single(result.Actions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RollbackReportsFailedDeletesWhenTheFileIsStillOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Windows 的共享锁语义是此路径的前提；Linux 允许删除仍打开的文件。
            return;
        }

        var root = Directory.CreateTempSubdirectory("ecl-rollback-lock-").FullName;
        try
        {
            var path = Path.Combine(root, "locked.jar");
            var transaction = new InstallTransaction();
            transaction.TrackFile(path);
            File.WriteAllText(path, "new content");

            using var handle = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            var result = transaction.Rollback();

            Assert.False(result.IsComplete);
            var failure = Assert.Single(result.Failures);
            Assert.Contains("failed to rollback", failure, StringComparison.Ordinal);
            Assert.Contains(path, failure, StringComparison.Ordinal);
            Assert.Single(result.Actions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    [Fact]
    public async Task PublishesOneInstallOperationAcrossAllStages()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeDownloadService();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IDownloadService>(fake));
        var store = provider.GetRequiredService<IOperationStore>();

        var instanceResult = await provider.GetRequiredService<IInstanceService>().CreateAsync("Test", "1.21.1");
        Assert.True(instanceResult.IsSuccess);
        var instance = instanceResult.Value!;

        var result = await provider.GetRequiredService<IVersionInstallService>().InstallAsync(instance.Id);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var snapshot = Assert.Single(store.Snapshots);
        Assert.Equal(OperationKind.Install, snapshot.Kind);
        Assert.Equal(OperationState.Completed, snapshot.State);
        Assert.Equal("安装 Test", snapshot.Title);
        Assert.Equal("install.completed", snapshot.StageId);
        Assert.False(snapshot.CanCancel);
        Assert.True(snapshot.Completed > 0, "安装完成后应报告已处理的项目数量。");
        Assert.True(snapshot.BytesDownloaded > 0, "安装完成后应报告累计字节。");

        // 安装内部的下载批次不应再登记一个独立任务。
        Assert.DoesNotContain(store.Snapshots, item => item.Kind == OperationKind.Download);
    }

    private sealed class FakeLoaderCatalog : ILoaderCatalog
    {
        private const string FabricProfile = """
            {
              "id": "fabric-loader-0.16.0-1.21.1",
              "inheritsFrom": "1.21.1",
              "type": "release",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "arguments": {
                "game": [],
                "jvm": ["-DFabricMcEmu= net.minecraft.client.main.Main "]
              },
              "libraries": [
                {
                  "name": "net.fabricmc:fabric-loader:0.16.0",
                  "url": "https://maven.fabricmc.net/"
                }
              ]
            }
            """;

        public Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetFabricLoadersAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<LoaderVersionInfo>>.Ok(
            [
                new LoaderVersionInfo("0.16.0", true, DateTimeOffset.Parse("2024-06-01T00:00:00Z")),
            ]));

        public Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetCleanroomVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<LoaderVersionInfo>>.Ok([]));

        public Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetForgeVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<LoaderVersionInfo>>.Ok(
            [
                new LoaderVersionInfo("47.4.0", true, null),
            ]));

        public Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetNeoForgeVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<LoaderVersionInfo>>.Ok([]));

        public Task<Result<IReadOnlyList<FabricApiVersionInfo>>> GetFabricApiVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<FabricApiVersionInfo>>.Ok(
            [
                new FabricApiVersionInfo(
                    "0.116.0+1.21.1",
                    "https://cdn.modrinth.com/data/P7dR8mSH/versions/test/fabric-api.jar",
                    "fabric-api-0.116.0+1.21.1.jar",
                    null,
                    1,
                    DateTimeOffset.Parse("2024-06-01T00:00:00Z")),
            ]));

        public Task<Result<IReadOnlyList<OptiFineVersionInfo>>> GetOptiFineVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<OptiFineVersionInfo>>.Ok([]));

        public Task<Result<string>> GetFabricProfileAsync(
            string gameVersion,
            string loaderVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Ok(FabricProfile));
    }

    private sealed class FakeJavaSelectionService : IJavaSelectionService
    {
        public Task<JavaSelectionResult> ResolveAsync(
            GameInstance instance,
            MinecraftVersion? version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new JavaSelectionResult(
                new JavaRuntime("/java", "java", "Test", "x86_64", null),
                null,
                true,
                null,
                true,
                null,
                false,
                [],
                4096));

        public Task<EffectiveJavaRequirements> ResolveRequirementsAsync(
            GameInstance instance,
            MinecraftVersion? version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EffectiveJavaRequirements.Empty);
    }

    private sealed class ThrowingJavaSelectionService : IJavaSelectionService
    {
        public Task<JavaSelectionResult> ResolveAsync(
            GameInstance instance,
            MinecraftVersion? version,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("spec 0 安装不应解析 Java。");

        public Task<EffectiveJavaRequirements> ResolveRequirementsAsync(
            GameInstance instance,
            MinecraftVersion? version,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("spec 0 安装不应解析 Java。");
    }

    private sealed class FakeForgeProcessorRunner(string? librariesDirectory = null) : IForgeProcessorRunner
    {
        public bool Called { get; private set; }

        public string? LoaderVersion { get; private set; }

        public Task<Result> RunAsync(
            ForgeProcessorRunRequest request,
            IProgress<InstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            LoaderVersion = request.Profile.Version;

            if (librariesDirectory is not null)
            {
                var patched = ForgeProcessorPlanner.ResolveClientPatchedPath(request.Profile, librariesDirectory);
                if (!string.IsNullOrWhiteSpace(patched))
                {
                    var directory = Path.GetDirectoryName(patched);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(patched, "patched");
                }
            }

            progress?.Report(new InstallProgress(InstallStage.Processors, "processors", 1, 1, 0, 0, null));
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class FakeDownloadService(
        bool legacyForgeInstaller = false,
        string? failOnDisplayName = null) : IDownloadService
    {
        public ConcurrentBag<DownloadRequest> Requests { get; } = [];

        public Task<DownloadBatchResult> DownloadAsync(
            IReadOnlyList<DownloadRequest> requests,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (failOnDisplayName is not null &&
                requests.Any(request => string.Equals(request.DisplayName, failOnDisplayName, StringComparison.Ordinal)))
            {
                throw new DownloadException("fake download failure", [failOnDisplayName])
                {
                    Kind = OperationErrorKind.Network,
                };
            }

            foreach (var request in requests)
            {
                Requests.Add(request);
                var directory = Path.GetDirectoryName(request.DestinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (request.DestinationPath.EndsWith("-installer.jar", StringComparison.OrdinalIgnoreCase))
                {
                    if (legacyForgeInstaller)
                    {
                        WriteLegacyInstaller(request.DestinationPath);
                    }
                    else
                    {
                        WriteInstaller(request.DestinationPath);
                    }
                }
                else
                {
                    File.WriteAllBytes(request.DestinationPath, Encoding.UTF8.GetBytes("x"));
                }
            }

            return Task.FromResult(new DownloadBatchResult(requests.Count, 0, requests.Count));
        }

        private static void WriteLegacyInstaller(string path)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

            WriteEntry(archive, "install_profile.json", """
                {
                  "install": {
                    "profileName": "Forge",
                    "minecraft": "1.21.1"
                  },
                  "versionInfo": {
                    "id": "1.21.1-forge-47.4.0",
                    "inheritsFrom": "1.21.1",
                    "type": "release",
                    "mainClass": "net.minecraft.launchwrapper.Launch",
                    "minecraftArguments": "--username ${auth_player_name} --tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker",
                    "libraries": [
                      {
                        "name": "net.minecraftforge:forge:47.4.0",
                        "url": "https://maven.minecraftforge.net/"
                      }
                    ]
                  }
                }
                """);

            WriteEntry(archive, "version.json", "{}");
        }

        private static void WriteInstaller(string path)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

            WriteEntry(archive, "install_profile.json", """
                {
                  "spec": 1,
                  "minecraft": "1.21.1",
                  "version": "47.4.0",
                  "data": {
                    "PATCHED": { "client": "[com.example:demo:1.0:client]" }
                  },
                  "processors": [
                    {
                      "sides": ["client"],
                      "jar": "com.example:demo:1.0",
                      "classpath": [],
                      "args": ["--input", "{MINECRAFT_JAR}", "--output", "{PATCHED}"]
                    }
                  ],
                  "libraries": [
                    {
                      "name": "com.example:demo:1.0",
                      "downloads": {
                        "artifact": {
                          "path": "com/example/demo/1.0/demo-1.0.jar",
                          "url": "https://maven.minecraftforge.net/com/example/demo/1.0/demo-1.0.jar",
                          "sha1": "0000000000000000000000000000000000000000",
                          "size": 1
                        }
                      }
                    }
                  ]
                }
                """);

            WriteEntry(archive, "version.json", """
                {
                  "id": "1.21.1-forge-47.4.0",
                  "inheritsFrom": "1.21.1",
                  "type": "release",
                  "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
                  "arguments": {
                    "game": ["--launchTarget", "forgeclient", "--fml.forgeVersion", "47.4.0"],
                    "jvm": ["-DlibraryDirectory=${library_directory}"]
                  },
                  "libraries": []
                }
                """);
        }

        private static void WriteEntry(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(content);
        }

        public Task<string> DownloadTextAsync(
            IReadOnlyList<Uri> urls,
            string? expectedHash = null,
            long? expectedSize = null,
            HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
            CancellationToken cancellationToken = default)
        {
            var url = urls.FirstOrDefault()?.ToString() ?? string.Empty;

            if (url.Contains("version_manifest", StringComparison.Ordinal))
            {
                return Task.FromResult(ManifestJson);
            }

            if (url.EndsWith("1.21.1.json", StringComparison.Ordinal))
            {
                return Task.FromResult(VersionJson);
            }

            if (url.EndsWith("1.21.json", StringComparison.Ordinal))
            {
                return Task.FromResult(AssetIndexJson);
            }

            return Task.FromResult("{}");
        }

        private const string ManifestJson = """
            {
              "latest": { "release": "1.21.1", "snapshot": "24w14a" },
              "versions": [
                {
                  "id": "1.21.1",
                  "type": "release",
                  "url": "https://piston-meta.mojang.com/v1/packages/abc/1.21.1.json",
                  "sha1": "0000000000000000000000000000000000000000",
                  "size": 1
                }
              ]
            }
            """;

        private const string VersionJson = """
            {
              "id": "1.21.1",
              "type": "release",
              "mainClass": "net.minecraft.client.main.Main",
              "assetIndex": {
                "id": "1.21",
                "url": "https://piston-meta.mojang.com/v1/packages/def/1.21.json",
                "sha1": "0000000000000000000000000000000000000000",
                "size": 1
              },
              "downloads": {
                "client": {
                  "url": "https://piston-data.mojang.com/v1/objects/abc/client.jar",
                  "sha1": "0000000000000000000000000000000000000000",
                  "size": 1
                }
              },
              "logging": {
                "client": {
                  "argument": "-Dlog4j.configurationFile=${path}",
                  "file": {
                    "id": "client-1.12.xml",
                    "url": "https://piston-data.mojang.com/v1/objects/def/client-1.12.xml",
                    "sha1": "0000000000000000000000000000000000000000",
                    "size": 1
                  }
                }
              },
              "libraries": [
                {
                  "name": "com.example:demo:1.0",
                  "downloads": {
                    "artifact": {
                      "path": "com/example/demo/1.0/demo-1.0.jar",
                      "url": "https://libraries.minecraft.net/com/example/demo/1.0/demo-1.0.jar",
                      "sha1": "0000000000000000000000000000000000000000",
                      "size": 1
                    }
                  }
                }
              ]
            }
            """;

        private const string AssetIndexJson = """
            {
              "objects": {
                "minecraft/test.txt": {
                  "hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size": 1
                }
              }
            }
            """;
    }
}
