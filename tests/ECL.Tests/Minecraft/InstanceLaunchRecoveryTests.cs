// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Auth;
using Ecl.Core.Json;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// F-04 §5.3：存在 NeedsRecovery / 正在变更的事务时，启动准入必须阻止启动并说明原因。
/// </summary>
public sealed class InstanceLaunchRecoveryTests
{
    [Fact]
    public async Task NeedsRecoveryTransactionBlocksLaunch()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("RecoveryLaunch", "1.21.1")).Value!;
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");
        await WriteJournalAsync(environment, instance.Id, "tx-launch-recovery", InstanceTransactionState.NeedsRecovery);

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: true);

        Assert.False(report.Success);
        var validate = Assert.Single(report.Steps, step => step.StepId == "validate");
        Assert.Equal(LaunchStepStatus.Failed, validate.Status);
        Assert.Contains("恢复", validate.Message);
    }

    [Fact]
    public async Task ActiveTransactionBlocksLaunch()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("WritingLaunch", "1.21.1")).Value!;
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");
        await WriteJournalAsync(environment, instance.Id, "tx-launch-writing", InstanceTransactionState.Applying);

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: true);

        Assert.False(report.Success);
        var validate = Assert.Single(report.Steps, step => step.StepId == "validate");
        Assert.Equal(LaunchStepStatus.Failed, validate.Status);
        Assert.Contains("变更", validate.Message);
    }

    private static async Task WriteJournalAsync(
        TestEnvironment environment,
        string instanceId,
        string transactionId,
        InstanceTransactionState state)
    {
        var directory = Path.Combine(
            environment.Paths.ConfigDirectory,
            "instance-engine",
            "transactions",
            "instances",
            instanceId);
        Directory.CreateDirectory(directory);
        var record = new InstanceTransactionRecord(
            transactionId,
            instanceId,
            environment.Paths.GetInstanceGameDirectory(instanceId),
            "launch admission test",
            state,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [],
            [],
            0,
            SchemaVersion: 2);
        await File.WriteAllTextAsync(
            Path.Combine(directory, $"{transactionId}.json"),
            JsonSerializer.Serialize(record, EclJson.Indented));
    }
}
