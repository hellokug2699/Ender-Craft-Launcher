// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Storage;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Modrinth;
using Ecl.Tests.CurseForge;
using Ecl.Tests.Modrinth;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Content;

/// <summary>
/// F-06 S1–S3：依赖图计划（稳定身份/状态/边）、实例更新策略与可证明来源的更新检查。
/// </summary>
public sealed class ContentInstallPlanTests
{
    [Fact]
    public async Task PlannerBuildsGraphWithRequiredDependencyAndPlannedFiles()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("Plan Me", "1.21.1", "fabric", "0.16.0")).Value!;
        var modrinth = new FakeModrinthClient();
        modrinth.VersionsById["v-root"] = Version("v-root", "mod-a", "1.0.0", "mod-a.jar", ["1.21.1"], ["fabric"],
            [new ModrinthDependency { ProjectId = "mod-b", DependencyType = "required" }]);
        modrinth.Versions["mod-b"] =
        [
            Version("v-dep", "mod-b", "2.0.0", "mod-b.jar", ["1.21.1"], ["fabric"], []),
        ];
        var planner = new ContentInstallPlanner(instances, modrinth, new FakeCurseForgeClient());

        var planned = await planner.PlanAsync(
            instance.Id,
            new ContentInstallPlanRequest(ContentSourceKind.Modrinth, "mod-a", "v-root"));

        Assert.True(planned.IsSuccess, planned.Error?.ToString());
        var plan = planned.Value!;
        Assert.True(plan.IsInstallable);
        Assert.Equal(2, plan.Nodes.Count);
        Assert.Single(plan.Edges);
        Assert.Equal(2, plan.Files.Count);
        Assert.Contains(plan.Files, file => file.FileName == "mod-a.jar");
        Assert.Contains(plan.Files, file => file.FileName == "mod-b.jar");
        Assert.All(plan.Files, file => Assert.False(string.IsNullOrWhiteSpace(file.VersionId)));
        Assert.Empty(plan.Blockers);
    }

    [Fact]
    public async Task PlannerBlocksIncompatibleRequiredDependencyInsteadOfSkippingSilently()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("Plan Incompatible", "1.21.1", "fabric", "0.16.0")).Value!;
        var modrinth = new FakeModrinthClient();
        modrinth.VersionsById["v-root"] = Version("v-root", "mod-a", "1.0.0", "mod-a.jar", ["1.21.1"], ["fabric"],
            [new ModrinthDependency { VersionId = "v-old", ProjectId = "mod-b", DependencyType = "required" }]);
        modrinth.VersionsById["v-old"] = Version("v-old", "mod-b", "0.9.0", "mod-b-old.jar", ["1.19.2"], ["fabric"], []);
        var planner = new ContentInstallPlanner(instances, modrinth, new FakeCurseForgeClient());

        var planned = await planner.PlanAsync(
            instance.Id,
            new ContentInstallPlanRequest(ContentSourceKind.Modrinth, "mod-a", "v-root"));

        Assert.True(planned.IsSuccess, planned.Error?.ToString());
        var plan = planned.Value!;
        Assert.False(plan.IsInstallable);
        Assert.Contains("content.plan.incompatible_dependency", plan.Blockers);
        Assert.Contains("modrinth:mod-b@v-old", plan.IncompatibleSkipped);
        Assert.Single(plan.Files);
    }

    [Fact]
    public async Task PlannerReportsMissingDependencyAsBlocker()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("Plan Missing", "1.21.1", "fabric", "0.16.0")).Value!;
        var modrinth = new FakeModrinthClient();
        modrinth.VersionsById["v-root"] = Version("v-root", "mod-a", "1.0.0", "mod-a.jar", ["1.21.1"], ["fabric"],
            [new ModrinthDependency { ProjectId = "mod-missing", DependencyType = "required" }]);
        var planner = new ContentInstallPlanner(instances, modrinth, new FakeCurseForgeClient());

        var planned = await planner.PlanAsync(
            instance.Id,
            new ContentInstallPlanRequest(ContentSourceKind.Modrinth, "mod-a", "v-root"));

        Assert.True(planned.IsSuccess, planned.Error?.ToString());
        var plan = planned.Value!;
        Assert.False(plan.IsInstallable);
        Assert.Contains("content.plan.unresolved_dependency", plan.Blockers);
        Assert.Contains(plan.Nodes, node => node.Status == ContentDependencyStatus.NotFound && node.Required);
    }

    [Fact]
    public async Task UpdatePolicyDefaultsToAllowAndPersistsBlock()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var policies = provider.GetRequiredService<IInstanceContentUpdatePolicyService>();
        var instance = (await instances.CreateAsync("Policy", "1.21.1")).Value!;

        var initial = await policies.GetAsync(instance.Id);
        Assert.True(initial.IsSuccess, initial.Error?.ToString());
        Assert.Equal(InstanceContentUpdatePolicy.Allow, initial.Value);

        var written = await policies.SetAsync(instance.Id, InstanceContentUpdatePolicy.Block);
        Assert.True(written.IsSuccess, written.Error?.ToString());
        var blocked = await policies.GetAsync(instance.Id);
        Assert.Equal(InstanceContentUpdatePolicy.Block, blocked.Value);

        var settings = provider.GetRequiredService<Ecl.Configuration.Settings.ISettingsService>();
        // 非法值被设置服务拒绝（不把损坏值解释成允许更新），策略保持 Block。
        Assert.False(settings.TrySetRaw(InstanceContentUpdatePolicyKeys.Key, "bogus", instance.Id, out _));
        var stillBlocked = await policies.GetAsync(instance.Id);
        Assert.Equal(InstanceContentUpdatePolicy.Block, stillBlocked.Value);

        Assert.Equal("notify", InstanceContentUpdatePolicyService.ToRaw(InstanceContentUpdatePolicy.NotifyOnly));
        Assert.True(InstanceContentUpdatePolicyService.TryParse("notify-only", out var parsed));
        Assert.Equal(InstanceContentUpdatePolicy.NotifyOnly, parsed);
    }

    [Fact]
    public async Task UpdateCheckRespectsInstancePolicyAndInstalledCurseForgeRecords()
    {
        using var environment = new TestEnvironment();
        var curseForge = new FakeCurseForgeClient();
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<ICurseForgeClient>(curseForge));
        var instances = provider.GetRequiredService<IInstanceService>();
        var policies = provider.GetRequiredService<IInstanceContentUpdatePolicyService>();
        var updates = provider.GetRequiredService<IContentUpdateService>();
        var instance = (await instances.CreateAsync("Updates", "1.21.1", "fabric", "0.16.0")).Value!;

        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(
            Path.Combine(game, ".ecl-curseforge.json"),
            """
            {"mods":[{"projectId":42,"fileId":7,"fileName":"jei-7.jar","installedAt":"2026-11-01T00:00:00+00:00"}]}
            """);
        curseForge.ListedFiles.Add(new CurseForgeFile
        {
            Id = 9,
            ModId = 42,
            FileName = "jei-9.jar",
            DisplayName = "JEI 9",
            FileDate = DateTimeOffset.UtcNow,
        });

        var allowed = await updates.CheckAsync(instance.Id);
        Assert.True(allowed.IsSuccess, allowed.Error?.ToString());
        Assert.Equal(InstanceContentUpdatePolicy.Allow, allowed.Value!.Policy);
        var candidate = Assert.Single(allowed.Value.Candidates);
        Assert.True(candidate.Allowed);
        Assert.Equal("9", candidate.LatestVersionId);

        await policies.SetAsync(instance.Id, InstanceContentUpdatePolicy.NotifyOnly);
        var notify = await updates.CheckAsync(instance.Id);
        Assert.False(Assert.Single(notify.Value!.Candidates).Allowed);
        Assert.Equal("policy.notify", notify.Value.Candidates[0].Reason);

        await policies.SetAsync(instance.Id, InstanceContentUpdatePolicy.Block);
        var blocked = await updates.CheckAsync(instance.Id);
        Assert.True(blocked.Value!.UpdatesBlocked);
        Assert.Equal(0, blocked.Value.AvailableCount);
        Assert.Equal("policy.block", Assert.Single(blocked.Value.Candidates).Reason);
    }

    [Fact]
    public async Task CurseForgePlanResolvesStableFileAndRequiredDependency()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("Plan CF", "1.21.1", "fabric", "0.16.0")).Value!;
        var curseForge = new FakeCurseForgeClient();
        curseForge.Mods[1] = new CurseForgeMod
        {
            Id = 1,
            Slug = "demo",
            Name = "Demo",
            LatestFilesIndexes =
            [
                new CurseForgeFileIndex
                {
                    GameVersion = "1.21.1",
                    FileId = 10,
                    ModLoader = (int)CurseForgeModLoader.Fabric,
                    ReleaseType = 1,
                },
            ],
        };
        curseForge.Mods[2] = new CurseForgeMod
        {
            Id = 2,
            Slug = "library",
            Name = "Library",
            LatestFilesIndexes =
            [
                new CurseForgeFileIndex
                {
                    GameVersion = "1.21.1",
                    FileId = 20,
                    ModLoader = (int)CurseForgeModLoader.Fabric,
                    ReleaseType = 1,
                },
            ],
        };
        curseForge.Files[10] = new CurseForgeFile
        {
            Id = 10,
            ModId = 1,
            FileName = "demo.jar",
            DownloadUrl = "https://edge.forgecdn.net/files/0/10/demo.jar",
            FileLength = 10,
            GameVersions = ["1.21.1", "Fabric"],
            Dependencies = [new CurseForgeFileDependency { ModId = 2, RelationType = 3 }],
        };
        curseForge.Files[20] = new CurseForgeFile
        {
            Id = 20,
            ModId = 2,
            FileName = "library.jar",
            DownloadUrl = "https://edge.forgecdn.net/files/0/20/library.jar",
            FileLength = 8,
            GameVersions = ["1.21.1", "Fabric"],
        };

        var planner = new ContentInstallPlanner(instances, new FakeModrinthClient(), curseForge);
        var planned = await planner.PlanAsync(
            instance.Id,
            new ContentInstallPlanRequest(ContentSourceKind.CurseForge, "1"));

        Assert.True(planned.IsSuccess, planned.Error?.ToString());
        var plan = planned.Value!;
        Assert.Equal(ContentSourceKind.CurseForge, plan.Source);
        Assert.True(plan.IsInstallable);
        Assert.Equal(2, plan.Files.Count);
        var root = plan.Root!;
        Assert.Equal("10", root.VersionId);
        Assert.Equal("demo.jar", root.FileName);
        Assert.Contains(plan.Files, file => file.FileName == "library.jar");
        Assert.All(plan.Files, file => Assert.False(string.IsNullOrWhiteSpace(file.DownloadUrl)));
    }

    [Fact]
    public async Task ExecutorRejectsPlanWhenInstanceConfigurationChanged()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("Plan Stale", "1.21.1", "fabric", "0.16.0")).Value!;
        var modrinth = new FakeModrinthClient();
        modrinth.VersionsById["v-root"] = Version("v-root", "mod-a", "1.0.0", "mod-a.jar", ["1.21.1"], ["fabric"], []);
        var planner = new ContentInstallPlanner(instances, modrinth, new FakeCurseForgeClient());
        var plan = (await planner.PlanAsync(
            instance.Id,
            new ContentInstallPlanRequest(ContentSourceKind.Modrinth, "mod-a", "v-root"))).Value!;

        var executor = provider.GetRequiredService<IContentPlanExecutor>();
        var stale = await executor.ExecuteAsync(plan with { GameVersion = "1.20.1", Loader = "forge" });

        Assert.False(stale.IsSuccess);
        Assert.Equal("content.plan.stale", stale.Error!.Code);
    }

    [Fact]
    public async Task ExecutorAppliesOnlyThePlannedFilesWithoutAskingTheSourceAgain()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("demo");
        using var environment = new TestEnvironment();
        var modrinth = new FakeModrinthClient();
        modrinth.VersionsById["v-root"] = Version("v-root", "mod-a", "1.0.0", "mod-a.jar", ["1.21.1"], ["fabric"], []);
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<Ecl.Download.IDownloadService>(new PayloadDownloadService(payload));
            services.AddSingleton<IModrinthClient>(modrinth);
        });
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("Plan Execute", "1.21.1", "fabric", "0.16.0")).Value!;
        var planner = new ContentInstallPlanner(instances, modrinth, new FakeCurseForgeClient());
        var plan = (await planner.PlanAsync(
            instance.Id,
            new ContentInstallPlanRequest(ContentSourceKind.Modrinth, "mod-a", "v-root"))).Value!;

        // 计划生成后来源不可用：执行器仍必须只按计划文件安装。
        modrinth.Versions.Clear();
        modrinth.VersionsById.Clear();
        var versionCallsBefore = modrinth.VersionCalls;

        var executed = await provider.GetRequiredService<IContentPlanExecutor>().ExecuteAsync(plan);

        Assert.True(executed.IsSuccess, executed.Error?.ToString());
        Assert.Equal(versionCallsBefore, modrinth.VersionCalls);
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.GetInstanceGameDirectory(instance.Id),
            "mods",
            "mod-a.jar")));
    }

    [Fact]
    public async Task ExecutorRejectsBlockedPlanWithoutDownloading()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("demo");
        using var environment = new TestEnvironment();
        var downloads = new PayloadDownloadService(payload);
        var modrinth = new FakeModrinthClient();
        modrinth.VersionsById["v-root"] = Version("v-root", "mod-a", "1.0.0", "mod-a.jar", ["1.21.1"], ["fabric"], []);
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<Ecl.Download.IDownloadService>(downloads);
            services.AddSingleton<IModrinthClient>(modrinth);
        });
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("Plan Blocked", "1.21.1", "fabric", "0.16.0")).Value!;
        var planner = new ContentInstallPlanner(instances, modrinth, new FakeCurseForgeClient());
        var plan = (await planner.PlanAsync(
            instance.Id,
            new ContentInstallPlanRequest(ContentSourceKind.Modrinth, "mod-a", "v-root"))).Value!;

        var blocked = await provider.GetRequiredService<IContentPlanExecutor>()
            .ExecuteAsync(plan with { Blockers = ["content.plan.unresolved_dependency"] });

        Assert.False(blocked.IsSuccess);
        Assert.Equal("content.plan.blocked", blocked.Error!.Code);
        Assert.Empty(downloads.Requests);
    }

    private static ModrinthVersion Version(
        string id,
        string projectId,
        string versionNumber,
        string fileName,
        IReadOnlyList<string> gameVersions,
        IReadOnlyList<string> loaders,
        IReadOnlyList<ModrinthDependency> dependencies) => new()
        {
            Id = id,
            ProjectId = projectId,
            VersionNumber = versionNumber,
            GameVersions = gameVersions,
            Loaders = loaders,
            Dependencies = dependencies,
            DatePublished = DateTimeOffset.UtcNow,
            Files =
            [
                new ModrinthFile
                {
                    Url = $"https://cdn.modrinth.com/data/{projectId}/versions/{id}/{fileName}",
                    Filename = fileName,
                    Primary = true,
                    Size = 42,
                    Hashes = new ModrinthFileHashes { Sha1 = "sha1" },
                },
            ],
        };
}
