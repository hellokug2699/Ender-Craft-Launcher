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
        var planner = new ContentInstallPlanner(instances, modrinth);

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
        var planner = new ContentInstallPlanner(instances, modrinth);

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
        var planner = new ContentInstallPlanner(instances, modrinth);

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
