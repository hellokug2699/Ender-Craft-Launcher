// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class InstanceServiceTests
{
    [Fact]
    public async Task CreatesListsAndDeletesInstance()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var created = await instances.CreateAsync("生存", "1.21.1");
        Assert.True(created.IsSuccess);
        var instance = created.Value!;

        Assert.Matches("^[a-z0-9][a-z0-9._-]{0,63}$", instance.Id);
        Assert.Equal("1.21.1", instance.VersionId);
        Assert.True(Directory.Exists(environment.Paths.GetInstanceDirectory(instance.Id)));
        Assert.True(File.Exists(environment.Paths.GetInstanceMetadataFile(instance.Id)));
        Assert.True(Directory.Exists(Path.Combine(environment.Paths.GetInstanceGameDirectory(instance.Id), "mods")));

        var list = await instances.GetInstancesAsync();
        Assert.Single(list);

        var duplicate = await instances.CreateAsync("生存", "1.21.1");
        Assert.False(duplicate.IsSuccess);
        Assert.Equal("instance.duplicate_name", duplicate.Error?.Code);

        var invalidVersion = await instances.CreateAsync("Other", "../evil");
        Assert.False(invalidVersion.IsSuccess);

        var deleted = await instances.DeleteAsync(instance.Id);
        Assert.True(deleted.IsSuccess);
        Assert.False(Directory.Exists(environment.Paths.GetInstanceDirectory(instance.Id)));
        Assert.Empty(await instances.GetInstancesAsync());
    }

    [Fact]
    public async Task BuildsLoaderVersionId()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var created = await instances.CreateAsync("Fabric Test", "1.21.1", "fabric", "0.16.0");
        Assert.True(created.IsSuccess);
        Assert.Equal("1.21.1-fabric-0.16.0", created.Value!.VersionId);
        Assert.Equal("fabric", created.Value.Loader);
    }

    [Fact]
    public async Task StoresComponentVersionsForCleanroom()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var created = await instances.CreateAsync(
            "Cleanroom",
            "1.12.2",
            "cleanroom",
            "0.7.12-alpha",
            optiFineVersion: "HD_U:G5");

        Assert.True(created.IsSuccess);
        Assert.Equal("1.12.2-cleanroom-0.7.12-alpha", created.Value!.VersionId);
        Assert.Equal("HD_U:G5", created.Value.OptiFineVersion);
        Assert.Null(created.Value.FabricApiVersion);
    }

    [Fact]
    public async Task RejectsOptiFineOutsideCleanroom()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var created = await instances.CreateAsync(
            "Fabric",
            "1.21.1",
            "fabric",
            "0.16.0",
            "0.116.0+1.21.1",
            "HD_U:G5");

        Assert.False(created.IsSuccess);
        Assert.Equal("instance.optifine_requires_cleanroom", created.Error?.Code);
    }

    [Fact]
    public async Task RequiresLoaderVersionForModdedInstances()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var created = await instances.CreateAsync("Fabric", "1.21.1", "fabric");

        Assert.False(created.IsSuccess);
        Assert.Equal("instance.loader_version_required", created.Error?.Code);
    }
}
