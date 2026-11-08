// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;
using Ecl.Core.Diagnostics;
using Ecl.Minecraft.Launch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// D-03 全局内存策略：自动分配只依赖真实可用内存；手动值保留；
/// 实例显式内存优先；旧配置的显式数值按手动处理且迁移幂等。
/// </summary>
public sealed class MemoryAllocationTests
{
    [Theory]
    [InlineData(1024, 1024)]
    [InlineData(3000, 1536)]
    [InlineData(8000, 3072)]
    [InlineData(16000, 6144)]
    [InlineData(20000, 8192)]
    [InlineData(32000, 12288)]
    [InlineData(64000, 24576)]
    public void AutomaticAllocationScalesWithAvailableMemory(long availableMb, int expectedMb)
    {
        Assert.Equal(expectedMb, MemoryAllocationPolicy.ComputeAutomaticMb(availableMb));
    }

    [Fact]
    public void UnknownAvailableMemoryHasNoAutomaticValue()
    {
        Assert.Null(MemoryAllocationPolicy.ComputeAutomaticMb(0));
        Assert.Null(MemoryAllocationPolicy.ComputeAutomaticMb(-1));
    }

    [Fact]
    public void AutomaticPlanUsesTheRealProbeValues()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        var service = new MemoryAllocationService(settings, new StubProbe(64 * 1024, 16 * 1024));

        var plan = service.Resolve();

        Assert.Equal(MemoryAllocationMode.Automatic, plan.Mode);
        Assert.Equal(8192, plan.EffectiveMb);
        Assert.Equal(64 * 1024, plan.TotalPhysicalMb);
        Assert.Equal(16 * 1024, plan.AvailablePhysicalMb);
        Assert.True(plan.IsEstimateReliable);
    }

    [Fact]
    public void ManualPolicyUsesTheStoredValueEvenWhenAutomaticWouldDiffer()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("java.memoryPolicy", "manual", null, out var policyError), policyError);
        Assert.True(settings.TrySetRaw("java.defaultMemoryMb", "6144", null, out var memoryError), memoryError);
        var service = new MemoryAllocationService(settings, new StubProbe(32 * 1024, 16 * 1024));

        var plan = service.Resolve();

        Assert.Equal(MemoryAllocationMode.Manual, plan.Mode);
        Assert.Equal(6144, plan.EffectiveMb);
        Assert.True(plan.IsEstimateReliable);
    }

    [Fact]
    public void InstanceMemoryAlwaysWinsOverTheGlobalPolicy()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("java.defaultMemoryMb", "2048", null, out _));
        var service = new MemoryAllocationService(settings, new StubProbe(32 * 1024, 8 * 1024));

        var plan = service.Resolve(instanceMemoryMb: 12288);

        Assert.Equal(MemoryAllocationMode.Manual, plan.Mode);
        Assert.Equal(12288, plan.EffectiveMb);
    }

    [Fact]
    public void LegacyExplicitMemoryWithoutPolicyIsTreatedAsManual()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("java.defaultMemoryMb", "4096", null, out _));
        var service = new MemoryAllocationService(settings, new StubProbe(32 * 1024, 16 * 1024));

        var plan = service.Resolve();

        Assert.Equal(MemoryAllocationMode.Manual, plan.Mode);
        Assert.Equal(4096, plan.EffectiveMb);
    }

    [Fact]
    public void UnknownSystemMemoryFallsBackWithoutPretendingToBeReliable()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        var service = new MemoryAllocationService(settings, new StubProbe(total: 0, available: 0));

        var plan = service.Resolve();

        Assert.Equal(MemoryAllocationMode.Automatic, plan.Mode);
        Assert.Equal(MemoryAllocationPolicy.FallbackMemoryMb, plan.EffectiveMb);
        Assert.Equal(0, plan.TotalPhysicalMb);
        Assert.False(plan.IsEstimateReliable);
    }

    [Fact]
    public void MigrationLeavesDefaultUsersOnAutomatic()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();

        Assert.False(MemoryPolicyMigration.TryMigrate(settings));
        Assert.Equal("auto", settings.Get<string>("java.memoryPolicy"));
        Assert.Equal(SettingsValueSource.Default, settings.GetSource("java.memoryPolicy"));
    }

    [Theory]
    [InlineData("4096")]
    [InlineData("8192")]
    public void MigrationKeepsExplicitValuesAndMarksThemManual(string value)
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("java.defaultMemoryMb", value, null, out _));

        Assert.True(MemoryPolicyMigration.TryMigrate(settings));

        Assert.Equal("manual", settings.Get<string>("java.memoryPolicy"));
        Assert.Equal(SettingsValueSource.Global, settings.GetSource("java.memoryPolicy"));
        Assert.Equal(int.Parse(value), settings.Get<int>("java.defaultMemoryMb"));
    }

    [Fact]
    public void MigrationIsIdempotentAndNeverRewritesAnExplicitPolicy()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("java.defaultMemoryMb", "8192", null, out _));

        Assert.True(MemoryPolicyMigration.TryMigrate(settings));
        Assert.False(MemoryPolicyMigration.TryMigrate(settings));
        Assert.Equal("manual", settings.Get<string>("java.memoryPolicy"));

        // 用户显式改回自动后，迁移不得把它改回手动。
        Assert.True(settings.TrySetRaw("java.memoryPolicy", "auto", null, out _));
        Assert.False(MemoryPolicyMigration.TryMigrate(settings));
        Assert.Equal("auto", settings.Get<string>("java.memoryPolicy"));
    }

    [Fact]
    public void MigrationDoesNotThrowOnCorruptSettingsFile()
    {
        using var environment = new TestEnvironment();
        File.WriteAllText(environment.Paths.GlobalSettingsFile, "{ this is not json");
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();

        Assert.False(MemoryPolicyMigration.TryMigrate(settings));
        Assert.Equal("auto", settings.Get<string>("java.memoryPolicy"));
    }

    private sealed class StubProbe(long total, long available) : ISystemMemoryProbe
    {
        public MemoryPressureSample? Read() => new(
            available * 1024 * 1024,
            total * 1024 * 1024,
            0,
            0);
    }
}
