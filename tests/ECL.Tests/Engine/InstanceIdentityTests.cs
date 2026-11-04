// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Json;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

public sealed class InstanceIdentityTests
{
    [Fact]
    public async Task CreateWritesInstanceMarkerAndRegistry()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var identity = provider.GetRequiredService<InstanceIdentityService>();

        var created = await instances.CreateAsync("Marker", "1.21.1");
        Assert.True(created.IsSuccess, created.Error?.ToString());
        var instance = created.Value!;

        var marker = await identity.ReadMarkerAsync(
            environment.Paths.GetInstanceGameDirectory(instance.Id));
        Assert.Equal(InstanceMarkerReadState.Valid, marker.State);
        Assert.Equal(instance.Id, marker.Marker!.InstanceId);

        var registrations = await identity.ListAsync();
        Assert.True(registrations.IsSuccess);
        var registration = Assert.Single(registrations.Value!);
        Assert.Equal(instance.Id, registration.InstanceId);
        Assert.Equal(InstanceRegistrationStatus.Ok, registration.Status);

        var verification = await identity.VerifyAsync(instance.Id);
        Assert.True(verification.IsSuccess, verification.Error?.ToString());
        Assert.Equal(InstanceRegistrationStatus.Ok, verification.Value!.Status);
    }

    [Fact]
    public async Task RelocateKeepsInstanceIdWhenMarkerMovesWithDirectory()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var created = await instances.CreateAsync("Relocate", "1.21.1");
        var instance = created.Value!;
        var source = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var destination = Path.Combine(environment.Root, "moved", instance.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);

        var relocated = await instances.RelocateAsync(instance.Id, destination);
        Assert.True(relocated.IsSuccess, relocated.Error?.ToString());
        Assert.Equal(instance.Id, relocated.Value!.Id);
        Assert.Equal(Path.GetFullPath(destination), relocated.Value.GameDirectory);

        var loaded = await instances.GetAsync(instance.Id);
        Assert.Equal(Path.GetFullPath(destination), loaded!.GameDirectory);
    }

    [Fact]
    public async Task RelocateRejectsMarkerOwnedByAnotherInstance()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var first = (await instances.CreateAsync("First", "1.21.1")).Value!;
        var other = Path.Combine(environment.Root, "other");
        Directory.CreateDirectory(other);
        var identity = provider.GetRequiredService<InstanceIdentityService>();
        var wrote = await identity.WriteMarkerAsync(other, "someone-else", DateTimeOffset.UtcNow);
        Assert.True(wrote.IsSuccess, wrote.Error?.ToString());

        var relocated = await instances.RelocateAsync(first.Id, other);
        Assert.False(relocated.IsSuccess);
        Assert.Equal("identity.marker_conflict", relocated.Error?.Code);
    }

    [Fact]
    public async Task CloneCopiesContentButUsesNewIdentity()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        var source = (await instances.CreateAsync("Source", "1.21.1")).Value!;
        var sourceGame = environment.Paths.GetInstanceGameDirectory(source.Id);
        var mods = Path.Combine(sourceGame, "mods");
        Directory.CreateDirectory(mods);
        await File.WriteAllTextAsync(Path.Combine(mods, "example.jar"), "content");

        var clone = await instances.CloneAsync(source.Id, "Clone");
        Assert.True(clone.IsSuccess, clone.Error?.ToString());
        var copy = clone.Value!;

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(source.GameVersion, copy.GameVersion);
        Assert.True(File.Exists(Path.Combine(
            environment.Paths.GetInstanceGameDirectory(copy.Id),
            "mods",
            "example.jar")));

        var identity = provider.GetRequiredService<InstanceIdentityService>();
        var marker = await identity.ReadMarkerAsync(environment.Paths.GetInstanceGameDirectory(copy.Id));
        Assert.Equal(InstanceMarkerReadState.Valid, marker.State);
        Assert.Equal(copy.Id, marker.Marker!.InstanceId);
    }

    [Fact]
    public async Task ExternalReadOnlyInstanceCanBeRegisteredWithoutMarker()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var identity = provider.GetRequiredService<InstanceIdentityService>();

        var external = Path.Combine(environment.Root, "external");
        Directory.CreateDirectory(external);
        var instance = new GameInstance
        {
            Id = "external-one",
            Name = "External",
            GameVersion = "1.20.1",
            VersionId = "1.20.1",
            GameDirectory = external,
        };

        var registered = await identity.EnsureRegisteredAsync(instance, writeMarker: false);
        Assert.True(registered.IsSuccess, registered.Error?.ToString());
        Assert.Equal(InstanceRegistrationStatus.LocalOnly, registered.Value!.Status);
        Assert.False(File.Exists(InstanceIdentityService.GetMarkerPath(external)));

        var verified = await identity.VerifyAsync(instance.Id);
        Assert.True(verified.IsSuccess);
        Assert.Equal(InstanceRegistrationStatus.LocalOnly, verified.Value!.Status);
    }

    [Fact]
    public async Task MarkerConflictPausesTransactions()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var identity = provider.GetRequiredService<InstanceIdentityService>();
        var transactions = provider.GetRequiredService<InstanceTransactionCoordinator>();

        var created = await instances.CreateAsync("Conflict", "1.21.1");
        var instance = created.Value!;
        var gameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var markerPath = InstanceIdentityService.GetMarkerPath(gameDirectory);
        await File.WriteAllTextAsync(
            markerPath,
            JsonSerializer.Serialize(
                new InstanceMarker(1, "another-instance", DateTimeOffset.UtcNow),
                EclJson.Default));

        var verification = await identity.VerifyAsync(instance.Id);
        Assert.Equal(InstanceRegistrationStatus.Conflict, verification.Value!.Status);

        var result = await transactions.ExecuteAsync(
            instance.Id,
            new InstanceChangePlan(
                [InstanceChangeOperation.WriteText("conflict.txt", "data")],
                "conflict test"));
        Assert.False(result.IsSuccess);
        Assert.Equal("transaction.identity_conflict", result.Error?.Code);
        Assert.False(File.Exists(Path.Combine(gameDirectory, "conflict.txt")));
    }
}
