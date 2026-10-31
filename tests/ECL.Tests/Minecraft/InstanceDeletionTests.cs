// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Json;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 删除实例必须先区分 ECL 托管目录与外部/共享引用目录：
/// 仅移除记录不得删除游戏数据，外部目录在任何模式下都不得被递归删除。
/// </summary>
public sealed class InstanceDeletionTests
{
    [Fact]
    public async Task PlanForManagedInstanceListsRecordAndManagedGameData()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var created = await instances.CreateAsync("Managed", "1.21.1");
        Assert.True(created.IsSuccess, created.Error?.ToString());
        var instance = created.Value!;

        var planResult = await instances.GetDeletionPlanAsync(instance.Id);
        Assert.True(planResult.IsSuccess, planResult.Error?.ToString());
        var plan = planResult.Value!;

        Assert.Equal(InstanceDirectoryOwnership.EclManaged, plan.GameDirectoryOwnership);
        Assert.True(plan.HasManagedGameData);
        Assert.False(plan.HasExternalGameData);
        Assert.Empty(plan.SharedWith);

        var gameData = Assert.IsType<InstanceDeletionEntry>(
            plan.Find(InstanceDeletionEntryKind.ManagedGameData));
        Assert.Equal(environment.Paths.GetInstanceGameDirectory(instance.Id), gameData.Path);
        Assert.True(gameData.CanDelete);
        Assert.True(gameData.Exists);

        Assert.Contains(
            plan.Entries,
            entry => entry.Kind == InstanceDeletionEntryKind.LauncherRecord && entry.Path.EndsWith("instance.json"));
    }

    [Fact]
    public async Task RemovingOnlyTheRecordKeepsManagedGameData()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var created = await instances.CreateAsync("Keep Save", "1.21.1");
        Assert.True(created.IsSuccess, created.Error?.ToString());
        var instance = created.Value!;
        var gameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var saveFile = Path.Combine(gameDirectory, "saves", "world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(saveFile)!);
        await File.WriteAllTextAsync(saveFile, "user-world");

        var deleted = await instances.DeleteAsync(instance.Id, InstanceDeleteOptions.RemoveRecordOnly);

        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.False(File.Exists(environment.Paths.GetInstanceMetadataFile(instance.Id)));
        Assert.Empty(await instances.GetInstancesAsync());
        Assert.True(Directory.Exists(gameDirectory));
        Assert.Equal("user-world", await File.ReadAllTextAsync(saveFile));
    }

    [Fact]
    public async Task DeletingManagedDataRemovesOnlyEclManagedDirectory()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var created = await instances.CreateAsync("Delete All", "1.21.1");
        Assert.True(created.IsSuccess, created.Error?.ToString());
        var instance = created.Value!;
        var recordDirectory = environment.Paths.GetInstanceDirectory(instance.Id);

        var deleted = await instances.DeleteAsync(instance.Id);

        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.False(Directory.Exists(recordDirectory));
        Assert.Empty(await instances.GetInstancesAsync());
    }

    [Fact]
    public async Task ImportedInstanceDeletionNeverDeletesExternalGameDirectory()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var external = CreateExternalInstallation(environment, "external-one");
        var saveFile = Path.Combine(external, "saves", "world", "level.dat");
        await WriteImportedMetadataAsync(environment, "imported-one", "Imported", external);

        var planResult = await instances.GetDeletionPlanAsync("imported-one");
        Assert.True(planResult.IsSuccess, planResult.Error?.ToString());
        var plan = planResult.Value!;
        Assert.Equal(InstanceDirectoryOwnership.External, plan.GameDirectoryOwnership);
        Assert.False(plan.HasManagedGameData);
        Assert.True(plan.HasExternalGameData);
        var gameData = Assert.IsType<InstanceDeletionEntry>(
            plan.Find(InstanceDeletionEntryKind.ExternalGameData));
        Assert.False(gameData.CanDelete);

        // 即使调用方要求删除托管数据，外部目录也必须保留。
        var deleted = await instances.DeleteAsync("imported-one", InstanceDeleteOptions.DeleteManagedData);

        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.Empty(await instances.GetInstancesAsync());
        Assert.True(Directory.Exists(external));
        Assert.Equal("user-world", await File.ReadAllTextAsync(saveFile));
    }

    [Fact]
    public async Task SharedExternalDirectoryIsReportedAndSurvivesDeletingOneReference()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var shared = CreateExternalInstallation(environment, "shared");
        await WriteImportedMetadataAsync(environment, "import-a", "A", shared);
        await WriteImportedMetadataAsync(environment, "import-b", "B", shared);

        var planResult = await instances.GetDeletionPlanAsync("import-a");
        Assert.True(planResult.IsSuccess, planResult.Error?.ToString());
        var plan = planResult.Value!;
        Assert.Equal(InstanceDirectoryOwnership.SharedExternal, plan.GameDirectoryOwnership);
        Assert.True(plan.IsShared);
        var reference = Assert.Single(plan.SharedWith);
        Assert.Equal("import-b", reference.Id);
        Assert.Equal("B", reference.Name);

        var deleted = await instances.DeleteAsync("import-a", InstanceDeleteOptions.DeleteManagedData);
        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());

        Assert.True(Directory.Exists(shared));
        Assert.NotNull(await instances.GetAsync("import-b"));
        Assert.Single(Directory.GetFiles(Path.Combine(shared, "saves", "world")));
    }

    [Fact]
    public async Task MissingExternalDirectoryStillAllowsRemovingTheRecord()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var missing = Path.Combine(environment.Root, "not-there");
        await WriteImportedMetadataAsync(environment, "broken-import", "Broken", missing);

        var planResult = await instances.GetDeletionPlanAsync("broken-import");
        Assert.True(planResult.IsSuccess, planResult.Error?.ToString());
        var plan = planResult.Value!;
        var gameData = Assert.IsType<InstanceDeletionEntry>(
            plan.Find(InstanceDeletionEntryKind.ExternalGameData));
        Assert.False(gameData.Exists);

        var deleted = await instances.DeleteAsync("broken-import", InstanceDeleteOptions.DeleteManagedData);

        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.False(Directory.Exists(environment.Paths.GetInstanceDirectory("broken-import")));
        Assert.Empty(await instances.GetInstancesAsync());
    }

    [Fact]
    public async Task DeletionPlanRejectsUnknownInstance()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var plan = await instances.GetDeletionPlanAsync("does-not-exist");
        Assert.False(plan.IsSuccess);
        Assert.Equal("instance.not_found", plan.Error?.Code);

        var deleted = await instances.DeleteAsync("does-not-exist");
        Assert.False(deleted.IsSuccess);
        Assert.Equal("instance.not_found", deleted.Error?.Code);
    }

    private static string CreateExternalInstallation(TestEnvironment environment, string name)
    {
        var directory = Path.Combine(environment.Root, "external", name);
        var saveFile = Path.Combine(directory, "saves", "world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(saveFile)!);
        File.WriteAllText(saveFile, "user-world");
        return directory;
    }

    private static async Task WriteImportedMetadataAsync(
        TestEnvironment environment,
        string instanceId,
        string name,
        string gameDirectory)
    {
        var recordDirectory = environment.Paths.GetInstanceDirectory(instanceId);
        Directory.CreateDirectory(recordDirectory);
        var instance = new GameInstance
        {
            Id = instanceId,
            Name = name,
            GameVersion = "1.21.1",
            VersionId = "1.21.1",
            GameDirectory = gameDirectory,
        };

        await File.WriteAllTextAsync(
            environment.Paths.GetInstanceMetadataFile(instanceId),
            JsonSerializer.Serialize(instance, EclJson.Default));
    }
}
