// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Instances;
using Ecl.Tests.CurseForge;
using Ecl.Tests.Modrinth;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Content;

/// <summary>
/// F-06 S5：批量更新计划、策略门禁、逐项失败报告、事务内替换旧文件与安装记录更新。
/// </summary>
public sealed class ContentBatchUpdateTests
{
    private const string OldRecordJson =
        """{"mods":[{"projectId":42,"fileId":7,"fileName":"jei-7.jar","installedAt":"2026-11-01T00:00:00+00:00"}]}""";

    [Fact]
    public async Task PlanIsReadOnlyAndProducesOneReadyItem()
    {
        await using var fixture = await BatchFixture.CreateAsync();

        var planned = await fixture.Batch.PlanAsync(fixture.Instance.Id);

        Assert.True(planned.IsSuccess, planned.Error?.ToString());
        var plan = planned.Value!;
        Assert.Equal(InstanceContentUpdatePolicy.Allow, plan.Policy);
        Assert.True(plan.HasApplicableItems);
        var item = Assert.Single(plan.Items);
        Assert.Equal(ContentBatchUpdateItemStatus.Ready, item.Status);
        Assert.Equal("42", item.ProjectId);
        Assert.Equal("jei-7.jar", item.FileName);
        Assert.Equal("9", item.LatestVersionId);
        Assert.NotNull(item.Plan);

        // 计划阶段不下载、不写实例内容、不改安装记录。
        Assert.Empty(fixture.Downloads.Requests);
        Assert.True(File.Exists(fixture.OldModPath));
        Assert.False(File.Exists(fixture.NewModPath));
        Assert.Contains("jei-7.jar", await File.ReadAllTextAsync(fixture.RecordPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyReplacesOldFileInSameTransactionAndUpdatesRecords()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        var plan = (await fixture.Batch.PlanAsync(fixture.Instance.Id)).Value!;

        var applied = await fixture.Batch.ApplyAsync(plan);

        Assert.True(applied.IsSuccess, applied.Error?.ToString());
        var result = applied.Value!;
        Assert.True(result.AllSucceeded);
        var item = Assert.Single(result.Items);
        Assert.Equal(ContentBatchUpdateItemStatus.Updated, item.Status);
        Assert.Contains("jei-9.jar", item.InstalledFiles);
        Assert.Contains("jei-7.jar", item.RemovedFiles);

        Assert.True(File.Exists(fixture.NewModPath));
        Assert.False(File.Exists(fixture.OldModPath));
        var record = await File.ReadAllTextAsync(fixture.RecordPath);
        Assert.Contains("jei-9.jar", record, StringComparison.Ordinal);
        Assert.Contains("\"fileId\": 9", record, StringComparison.Ordinal);
        Assert.DoesNotContain("jei-7.jar", record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlockPolicyPreventsPlanExecutionAndAnyWrite()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        await fixture.Policies.SetAsync(fixture.Instance.Id, InstanceContentUpdatePolicy.Block);

        var plan = (await fixture.Batch.PlanAsync(fixture.Instance.Id)).Value!;
        Assert.False(plan.PolicyAllowsUpdate);
        Assert.Equal(
            ContentBatchUpdateItemStatus.BlockedByPolicy,
            Assert.Single(plan.Items).Status);
        Assert.False(plan.HasApplicableItems);

        var applied = await fixture.Batch.ApplyAsync(plan);
        var result = applied.Value!;
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.BlockedCount);
        Assert.Empty(fixture.Downloads.Requests);
        Assert.True(File.Exists(fixture.OldModPath));
        Assert.False(File.Exists(fixture.NewModPath));
        Assert.Contains("jei-7.jar", await File.ReadAllTextAsync(fixture.RecordPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyOnlyPolicyNeverProducesExecutableItems()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        await fixture.Policies.SetAsync(fixture.Instance.Id, InstanceContentUpdatePolicy.NotifyOnly);

        var plan = (await fixture.Batch.PlanAsync(fixture.Instance.Id)).Value!;

        var item = Assert.Single(plan.Items);
        Assert.Equal(ContentBatchUpdateItemStatus.BlockedByPolicy, item.Status);
        Assert.Equal("policy.notify", item.Reason);
        Assert.Empty(fixture.Downloads.Requests);
    }

    [Fact]
    public async Task ExplicitSelectionWithoutCandidateIsReportedInsteadOfSilentlyIgnored()
    {
        await using var fixture = await BatchFixture.CreateAsync();

        var plan = (await fixture.Batch.PlanAsync(
            fixture.Instance.Id,
            new ContentBatchUpdateSelection(["999"]))).Value!;

        var item = Assert.Single(plan.Items);
        Assert.Equal(ContentBatchUpdateItemStatus.NoCandidate, item.Status);
        Assert.Equal("999", item.ProjectId);
        Assert.Equal(0, plan.ReadyCount);
        Assert.Empty(fixture.Downloads.Requests);
    }

    [Fact]
    public async Task PlanBlockedByMissingRequiredDependencyDoesNotWrite()
    {
        await using var fixture = await BatchFixture.CreateAsync(missingRequiredDependency: true);

        var plan = (await fixture.Batch.PlanAsync(fixture.Instance.Id)).Value!;
        var item = Assert.Single(plan.Items);
        Assert.Equal(ContentBatchUpdateItemStatus.PlanBlocked, item.Status);
        Assert.Equal("content.plan.blocked", item.ErrorCode);
        Assert.False(plan.HasApplicableItems);

        var applied = await fixture.Batch.ApplyAsync(plan);
        Assert.Equal(0, applied.Value!.UpdatedCount);
        Assert.Empty(fixture.Downloads.Requests);
        Assert.True(File.Exists(fixture.OldModPath));
        Assert.False(File.Exists(fixture.NewModPath));
    }

    [Fact]
    public async Task InstallFailureKeepsOldFileAndOldRecord()
    {
        await using var fixture = await BatchFixture.CreateAsync(useFailingDownload: true);
        var plan = (await fixture.Batch.PlanAsync(fixture.Instance.Id)).Value!;

        var applied = await fixture.Batch.ApplyAsync(plan);

        Assert.True(applied.IsSuccess, applied.Error?.ToString());
        var result = applied.Value!;
        Assert.False(result.AllSucceeded);
        var item = Assert.Single(result.Items);
        Assert.Equal(ContentBatchUpdateItemStatus.InstallFailed, item.Status);
        Assert.Equal("download.failed", item.ErrorCode);
        Assert.True(File.Exists(fixture.OldModPath));
        Assert.False(File.Exists(fixture.NewModPath));
        Assert.Contains("jei-7.jar", await File.ReadAllTextAsync(fixture.RecordPath), StringComparison.Ordinal);
    }

    private sealed class BatchFixture : IAsyncDisposable
    {
        private BatchFixture(
            TestEnvironment environment,
            ServiceProvider provider,
            FakeCurseForgeClient curseForge,
            IDownloadService downloads,
            GameInstance instance)
        {
            Environment = environment;
            Provider = provider;
            CurseForge = curseForge;
            DownloadService = downloads;
            Instance = instance;
        }

        public TestEnvironment Environment { get; }

        public ServiceProvider Provider { get; }

        public FakeCurseForgeClient CurseForge { get; }

        public IDownloadService DownloadService { get; }

        public GameInstance Instance { get; }

        public IContentBatchUpdateService Batch => Provider.GetRequiredService<IContentBatchUpdateService>();

        public IInstanceContentUpdatePolicyService Policies =>
            Provider.GetRequiredService<IInstanceContentUpdatePolicyService>();

        public PayloadDownloadService Downloads => (PayloadDownloadService)DownloadService;

        public string GameDirectory => Environment.Paths.GetInstanceGameDirectory(Instance.Id);

        public string ModsDirectory => Path.Combine(GameDirectory, "mods");

        public string RecordPath => Path.Combine(GameDirectory, ".ecl-curseforge.json");

        public string OldModPath => Path.Combine(ModsDirectory, "jei-7.jar");

        public string NewModPath => Path.Combine(ModsDirectory, "jei-9.jar");

        public static async Task<BatchFixture> CreateAsync(
            bool missingRequiredDependency = false,
            bool useFailingDownload = false)
        {
            var environment = new TestEnvironment();
            var curseForge = new FakeCurseForgeClient();
            IDownloadService downloads = useFailingDownload
                ? new FailingDownloadService()
                : new PayloadDownloadService(Encoding.UTF8.GetBytes("jei"));
            var provider = environment.CreateServices(services =>
            {
                services.AddSingleton<ICurseForgeClient>(curseForge);
                services.AddSingleton(downloads);
            });

            curseForge.Mods[42] = new CurseForgeMod
            {
                Id = 42,
                Slug = "jei",
                Name = "JEI",
                LatestFilesIndexes =
                [
                    new CurseForgeFileIndex
                    {
                        GameVersion = "1.21.1",
                        FileId = 9,
                        ModLoader = (int)CurseForgeModLoader.Fabric,
                        ReleaseType = 1,
                    },
                ],
            };
            curseForge.Files[7] = new CurseForgeFile
            {
                Id = 7,
                ModId = 42,
                FileName = "jei-7.jar",
                DisplayName = "JEI 7",
                DownloadUrl = "https://edge.forgecdn.net/files/0/7/jei-7.jar",
                FileLength = 2,
                GameVersions = ["1.21.1", "Fabric"],
                FileDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            };
            curseForge.Files[9] = new CurseForgeFile
            {
                Id = 9,
                ModId = 42,
                FileName = "jei-9.jar",
                DisplayName = "JEI 9",
                DownloadUrl = "https://edge.forgecdn.net/files/0/9/jei-9.jar",
                FileLength = 3,
                GameVersions = ["1.21.1", "Fabric"],
                FileDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                Dependencies = missingRequiredDependency
                    ? [new CurseForgeFileDependency { ModId = 99, RelationType = 3 }]
                    : [],
            };

            var instances = provider.GetRequiredService<IInstanceService>();
            var instance = (await instances.CreateAsync("Batch Update", "1.21.1", "fabric", "0.16.0")).Value!;
            var fixture = new BatchFixture(environment, provider, curseForge, downloads, instance);
            Directory.CreateDirectory(fixture.ModsDirectory);
            await File.WriteAllTextAsync(fixture.RecordPath, OldRecordJson);
            await File.WriteAllTextAsync(fixture.OldModPath, "old");
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            Environment.Dispose();
        }
    }

    private sealed class FailingDownloadService : IDownloadService
    {
        public Task<DownloadBatchResult> DownloadAsync(
            IReadOnlyList<DownloadRequest> requests,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new DownloadException("模拟下载失败。", ["jei-9.jar: test"]);

        public Task<string> DownloadTextAsync(
            IReadOnlyList<Uri> urls,
            string? expectedHash = null,
            long? expectedSize = null,
            HashingAlgorithm algorithm = HashingAlgorithm.Sha1,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
