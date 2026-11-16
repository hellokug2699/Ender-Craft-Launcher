// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

/// <summary>
/// F-04 S1：实例写门必须保证同实例写互斥、读租约可并发且与写互斥，
/// 超时返回稳定错误码，锁文件存在时阻止第二个进程写入。
/// </summary>
public sealed class InstanceWriteGateTests
{
    [Fact]
    public async Task WriteLeaseIsExclusiveAndReleases()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var gate = provider.GetRequiredService<IInstanceWriteGate>();
        var created = (await instances.CreateAsync("Gate", "1.21.1")).Value!;

        var first = await gate.AcquireAsync(created.Id);
        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(gate.IsWriteHeld(created.Id));

        var second = await gate.AcquireAsync(created.Id, TimeSpan.FromMilliseconds(100));
        Assert.False(second.IsSuccess);
        Assert.Equal("instance.write_busy", second.Error?.Code);

        await first.Value!.DisposeAsync();
        Assert.False(gate.IsWriteHeld(created.Id));

        var third = await gate.AcquireAsync(created.Id);
        Assert.True(third.IsSuccess, third.Error?.ToString());
        await third.Value!.DisposeAsync();
    }

    [Fact]
    public async Task ReadersCoexistAndBlockWriter()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var gate = provider.GetRequiredService<IInstanceWriteGate>();
        var created = (await instances.CreateAsync("Readers", "1.21.1")).Value!;

        var read1 = await gate.AcquireReadAsync(created.Id);
        var read2 = await gate.AcquireReadAsync(created.Id);
        Assert.True(read1.IsSuccess, read1.Error?.ToString());
        Assert.True(read2.IsSuccess, read2.Error?.ToString());
        Assert.Equal(2, gate.ActiveReaderCount(created.Id));

        var blocked = await gate.AcquireAsync(created.Id, TimeSpan.FromMilliseconds(100));
        Assert.False(blocked.IsSuccess);
        Assert.Equal("instance.write_busy", blocked.Error?.Code);

        await read1.Value!.DisposeAsync();
        var stillBlocked = await gate.AcquireAsync(created.Id, TimeSpan.FromMilliseconds(100));
        Assert.False(stillBlocked.IsSuccess);

        await read2.Value!.DisposeAsync();
        var acquired = await gate.AcquireAsync(created.Id);
        Assert.True(acquired.IsSuccess, acquired.Error?.ToString());
        await acquired.Value!.DisposeAsync();
    }

    [Fact]
    public async Task WriterBlocksNewReaders()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var gate = provider.GetRequiredService<IInstanceWriteGate>();
        var created = (await instances.CreateAsync("WriterFirst", "1.21.1")).Value!;

        var write = await gate.AcquireAsync(created.Id);
        Assert.True(write.IsSuccess, write.Error?.ToString());

        var read = await gate.AcquireReadAsync(created.Id, TimeSpan.FromMilliseconds(100));
        Assert.False(read.IsSuccess);
        Assert.Equal("instance.read_busy", read.Error?.Code);

        await write.Value!.DisposeAsync();
        var after = await gate.AcquireReadAsync(created.Id);
        Assert.True(after.IsSuccess, after.Error?.ToString());
        await after.Value!.DisposeAsync();
    }

    [Fact]
    public async Task ExternalLockFileBlocksCrossProcessWriter()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var gate = provider.GetRequiredService<IInstanceWriteGate>();
        var created = (await instances.CreateAsync("CrossProcess", "1.21.1")).Value!;

        var lockDirectory = Path.Combine(environment.Paths.Root, "locks", "instance-engine");
        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, $"{created.Id}.gate");
        await using var otherProcessLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var blocked = await gate.AcquireAsync(created.Id, TimeSpan.FromMilliseconds(100));
        Assert.False(blocked.IsSuccess);
        Assert.Equal("instance.write_busy", blocked.Error?.Code);
    }

    [Fact]
    public async Task CancellationExitsWait()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var gate = provider.GetRequiredService<IInstanceWriteGate>();
        var created = (await instances.CreateAsync("CancelGate", "1.21.1")).Value!;

        var write = await gate.AcquireAsync(created.Id);
        Assert.True(write.IsSuccess, write.Error?.ToString());

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.AcquireAsync(created.Id, TimeSpan.FromSeconds(5), cancellation.Token));

        await write.Value!.DisposeAsync();
    }
}
