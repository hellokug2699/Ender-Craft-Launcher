// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

public sealed class InstanceTransactionTests
{
    [Fact]
    public async Task CommitWritesFilesAndPersistsJournal()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("Transaction", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);

        var result = await transactions.ExecuteAsync(
            created.Id,
            new InstanceChangePlan(
                [
                    InstanceChangeOperation.WriteText("config/new.toml", "value = 1\n"),
                    InstanceChangeOperation.CreateDirectory("schematics"),
                ],
                "test commit"));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(InstanceTransactionState.Committed, result.Value!.State);
        Assert.Equal("value = 1\n", await File.ReadAllTextAsync(Path.Combine(game, "config", "new.toml")));
        Assert.True(Directory.Exists(Path.Combine(game, "schematics")));

        var journals = await transactions.ListAsync(created.Id);
        Assert.True(journals.IsSuccess);
        Assert.Single(journals.Value!);
    }

    [Fact]
    public async Task FailedApplyRollsBackEarlierWrites()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("Rollback", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);

        // 目标路径是已存在目录：写入会在 Apply 阶段失败，前面的写入必须回滚。
        Directory.CreateDirectory(Path.Combine(game, "blocked"));
        var result = await transactions.ExecuteAsync(
            created.Id,
            new InstanceChangePlan(
                [
                    InstanceChangeOperation.WriteText("first.txt", "first"),
                    InstanceChangeOperation.WriteText("blocked", "cannot-write"),
                ],
                "rollback test"));

        Assert.False(result.IsSuccess);
        Assert.Equal("transaction.apply_failed", result.Error?.Code);
        Assert.False(File.Exists(Path.Combine(game, "first.txt")));
        Assert.True(Directory.Exists(Path.Combine(game, "blocked")));
    }

    [Fact]
    public async Task SameTransactionIdIsIdempotent()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("Idempotent", "1.21.1")).Value!;
        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var plan = new InstanceChangePlan(
            [InstanceChangeOperation.WriteText("data.txt", "one")],
            "idempotent");

        var first = await transactions.ExecuteAsync(created.Id, plan, transactionId: "tx-fixed-1");
        Assert.True(first.IsSuccess, first.Error?.ToString());

        await File.WriteAllTextAsync(Path.Combine(game, "data.txt"), "externally-changed");
        var second = await transactions.ExecuteAsync(created.Id, plan, transactionId: "tx-fixed-1");
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal(InstanceTransactionState.Committed, second.Value!.State);
        Assert.Equal("externally-changed", await File.ReadAllTextAsync(Path.Combine(game, "data.txt")));
    }

    [Fact]
    public async Task RejectsPathTraversalAndProtectedMarker()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("Boundary", "1.21.1")).Value!;

        var traversal = await transactions.ExecuteAsync(
            created.Id,
            new InstanceChangePlan([InstanceChangeOperation.WriteText("../escape.txt", "x")], "escape"));
        Assert.False(traversal.IsSuccess);
        Assert.Equal("transaction.invalid_path", traversal.Error?.Code);

        var marker = await transactions.ExecuteAsync(
            created.Id,
            new InstanceChangePlan([InstanceChangeOperation.WriteText(".ecl/instance.json", "{}")], "marker"));
        Assert.False(marker.IsSuccess);
        Assert.Equal("transaction.protected_target", marker.Error?.Code);
    }

    [Fact]
    public async Task ConcurrentWritesAreSerialized()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();
        var created = (await instances.CreateAsync("Concurrent", "1.21.1")).Value!;

        var tasks = Enumerable.Range(0, 6)
            .Select(index => transactions.ExecuteAsync(
                created.Id,
                new InstanceChangePlan(
                    [InstanceChangeOperation.WriteText("shared.txt", $"writer-{index}\n")],
                    $"concurrent-{index}")))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.ToString()));
        var content = await File.ReadAllTextAsync(
            Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "shared.txt"));
        Assert.StartsWith("writer-", content);
    }
}
