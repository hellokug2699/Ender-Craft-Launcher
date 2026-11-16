// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Engine.Inventory;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

/// <summary>
/// F-04 S5：Inventory 扫描/缓存读取必须持有读租约，并按数据代次判断缓存是否仍然有效。
/// </summary>
public sealed class InstanceInventoryReadCoordinationTests
{
    [Fact]
    public async Task ScanAndCacheReadAreBlockedByWriter()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var inventory = provider.GetRequiredService<IInstanceInventoryService>();
        var gate = provider.GetRequiredService<IInstanceWriteGate>();
        var created = (await instances.CreateAsync("ReadGate", "1.21.1")).Value!;

        var write = await gate.AcquireAsync(created.Id);
        Assert.True(write.IsSuccess, write.Error?.ToString());

        var scan = await inventory.ScanAsync(created.Id);
        Assert.False(scan.IsSuccess);
        Assert.Equal("instance.read_busy", scan.Error?.Code);

        var cached = await inventory.GetCachedAsync(created.Id);
        Assert.False(cached.IsSuccess);
        Assert.Equal("instance.read_busy", cached.Error?.Code);

        await write.Value!.DisposeAsync();
        var after = await inventory.ScanAsync(created.Id);
        Assert.True(after.IsSuccess, after.Error?.ToString());
    }

    [Fact]
    public async Task CachedInventoryIsRejectedAfterGenerationIncrement()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var inventory = provider.GetRequiredService<IInstanceInventoryService>();
        var generation = provider.GetRequiredService<InstanceDataGeneration>();
        var created = (await instances.CreateAsync("Generation", "1.21.1")).Value!;

        var scan = await inventory.ScanAsync(created.Id);
        Assert.True(scan.IsSuccess, scan.Error?.ToString());
        Assert.True(scan.Value!.DataGeneration >= 0);

        var cached = await inventory.GetCachedAsync(created.Id);
        Assert.True(cached.IsSuccess, cached.Error?.ToString());
        Assert.Equal(scan.Value.DataGeneration, cached.Value!.DataGeneration);

        generation.Increment(created.Id);
        var stale = await inventory.GetCachedAsync(created.Id);
        Assert.True(stale.IsSuccess, stale.Error?.ToString());
        Assert.Null(stale.Value);

        var rescanned = await inventory.ScanAsync(created.Id);
        Assert.True(rescanned.IsSuccess, rescanned.Error?.ToString());
        Assert.Equal(generation.Get(created.Id), rescanned.Value!.DataGeneration);
    }

    [Fact]
    public async Task TransactionCommitIncrementsGenerationAndInvalidatesCache()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var inventory = provider.GetRequiredService<IInstanceInventoryService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var generation = provider.GetRequiredService<InstanceDataGeneration>();
        var created = (await instances.CreateAsync("TxGeneration", "1.21.1")).Value!;

        var scan = await inventory.ScanAsync(created.Id);
        Assert.True(scan.IsSuccess, scan.Error?.ToString());
        var before = generation.Get(created.Id);

        var committed = await transactions.ExecuteAsync(
            created.Id,
            new InstanceChangePlan(
                [InstanceChangeOperation.WriteText("config/after.txt", "v = 1\n")],
                "generation"));
        Assert.True(committed.IsSuccess, committed.Error?.ToString());

        Assert.Equal(before + 1, generation.Get(created.Id));
        var cached = await inventory.GetCachedAsync(created.Id);
        Assert.True(cached.IsSuccess, cached.Error?.ToString());
        Assert.Null(cached.Value);
    }
}
