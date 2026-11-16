// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Engine.Lifecycle;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

/// <summary>
/// F-03 S5：重定位只重新关联既有目录，带内容/标记证据与计划哈希。
/// </summary>
public sealed class InstanceRelocationLifecycleTests
{
    [Fact]
    public async Task RelocateKeepsInstanceIdAndReanchorsAfterDirectoryMove()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var relocations = provider.GetRequiredService<IInstanceRelocationService>();
        var created = (await instances.CreateAsync("RelocateLifecycle", "1.21.1")).Value!;
        var source = environment.Paths.GetInstanceGameDirectory(created.Id);
        await File.WriteAllTextAsync(Path.Combine(source, "options.txt"), "fov:70\n");
        var target = Path.Combine(Path.GetTempPath(), "ecl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            Directory.Move(source, target);

            var plan = await relocations.PlanAsync(created.Id, target);
            Assert.True(plan.IsSuccess, plan.Error?.ToString());
            Assert.Empty(plan.Value!.Blockers);
            Assert.Contains("target_marker_matches_instance", plan.Value.Evidence);

            var relocated = await relocations.ExecuteAsync(plan.Value);
            Assert.True(relocated.IsSuccess, relocated.Error?.ToString());
            Assert.Equal(created.Id, relocated.Value!.Instance.Id);
            var reloaded = await instances.GetAsync(created.Id);
            Assert.Equal(target, reloaded!.GameDirectory);
            Assert.True(File.Exists(Path.Combine(target, "options.txt")));
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RelocationRejectsMarkerOwnedByAnotherInstance()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var relocations = provider.GetRequiredService<IInstanceRelocationService>();
        var first = (await instances.CreateAsync("RelocateFirst", "1.21.1")).Value!;
        var second = (await instances.CreateAsync("RelocateSecond", "1.21.1")).Value!;
        var secondDirectory = environment.Paths.GetInstanceGameDirectory(second.Id);
        var target = Path.Combine(Path.GetTempPath(), "ecl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            Directory.Move(secondDirectory, target);
            var plan = await relocations.PlanAsync(first.Id, target);
            Assert.True(plan.IsSuccess, plan.Error?.ToString());
            Assert.Contains("identity.marker_conflict", plan.Value!.Blockers);
            var blocked = await relocations.ExecuteAsync(plan.Value);
            Assert.False(blocked.IsSuccess);
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RelocationPlanHashRejectsChangedTarget()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var relocations = provider.GetRequiredService<IInstanceRelocationService>();
        var created = (await instances.CreateAsync("RelocateStale", "1.21.1")).Value!;
        var source = environment.Paths.GetInstanceGameDirectory(created.Id);
        await File.WriteAllTextAsync(Path.Combine(source, "options.txt"), "fov:70\n");
        var target = Path.Combine(Path.GetTempPath(), "ecl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            Directory.Move(source, target);
            var plan = (await relocations.PlanAsync(created.Id, target)).Value!;
            await File.WriteAllTextAsync(Path.Combine(target, "options.txt"), "fov:90\n");
            var blocked = await relocations.ExecuteAsync(plan);
            Assert.False(blocked.IsSuccess);
            Assert.Equal("instance.lifecycle.plan_stale", blocked.Error?.Code);
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }
}
