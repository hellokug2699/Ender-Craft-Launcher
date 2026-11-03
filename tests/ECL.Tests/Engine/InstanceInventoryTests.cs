// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using Ecl.Minecraft.Engine.Inventory;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

public sealed class InstanceInventoryTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2ZQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task ScanRecognizesModsPacksWorldsAndEvidence()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var inventory = provider.GetRequiredService<IInstanceInventoryService>();
        var created = (await instances.CreateAsync("Inventory", "1.20.1", "fabric", "0.15.0")).Value!;

        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var mods = Path.Combine(game, "mods");
        Directory.CreateDirectory(mods);
        CreateJar(
            Path.Combine(mods, "good.jar"),
            """
            {
              "id": "goodmod",
              "name": "Good Mod",
              "version": "1.2.3",
              "icon": "icon.png",
              "depends": {
                "minecraft": ">=1.20",
                "fabricloader": ">=0.15",
                "missingdep": ">=1.0"
              }
            }
            """,
            OnePixelPng);
        CreateJar(
            Path.Combine(mods, "disabled.jar.disabled"),
            """
            { "id": "disabledmod", "name": "Disabled", "version": "2.0.0" }
            """);

        CreateZip(Path.Combine(game, "resourcepacks", "pack.zip"));
        CreateZip(Path.Combine(game, "shaderpacks", "shader.zip"));
        var world = Path.Combine(game, "saves", "Test World");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "level");
        Directory.CreateDirectory(Path.Combine(game, "crash-reports"));
        await File.WriteAllTextAsync(Path.Combine(game, "crash-reports", "crash-1.txt"), "Minecraft Crash Report");
        Directory.CreateDirectory(Path.Combine(game, "config"));
        await File.WriteAllTextAsync(Path.Combine(game, "config", "example.toml"), "key = 1\n");
        await File.WriteAllTextAsync(
            Path.Combine(game, "options.txt"),
            "resourcePacks:[\"vanilla\",\"file/pack.zip\"]\nshaderPacks:[\"shader.zip\"]\n");

        var result = await inventory.ScanAsync(created.Id);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;

        Assert.Equal(2, report.Totals.Mods);
        Assert.Equal(1, report.Totals.EnabledMods);
        Assert.Equal(1, report.Totals.ResourcePacks);
        Assert.Equal(1, report.Totals.ShaderPacks);
        Assert.Equal(1, report.Totals.Worlds);
        Assert.Equal(1, report.Totals.CrashReports);
        Assert.Equal(1, report.Totals.ConfigFiles);

        var good = Assert.Single(
            report.Resources,
            item => item.Kind == InventoryResourceKind.Mod && item.RelativePath == "mods/good.jar");
        Assert.Equal("Good Mod", good.Name);
        Assert.Equal("1.2.3", good.Version);
        Assert.Equal(ResourceEnablement.Enabled, good.Enablement);
        Assert.Equal(CompatibilityState.Compatible, good.MinecraftCompatibility);
        Assert.Equal(CompatibilityState.Compatible, good.LoaderCompatibility);
        Assert.Contains(good.Dependencies, dependency =>
            dependency.ModId == "missingdep" && dependency.State == DependencyState.Missing);
        Assert.NotNull(good.IconCachePath);
        Assert.True(File.Exists(good.IconCachePath!));

        var disabled = Assert.Single(report.Resources, item => item.RelativePath == "mods/disabled.jar.disabled");
        Assert.Equal(ResourceEnablement.Disabled, disabled.Enablement);

        var pack = Assert.Single(report.Resources, item => item.Kind == InventoryResourceKind.ResourcePack);
        Assert.Equal(ResourceEnablement.Enabled, pack.Enablement);
        Assert.Equal(ResourceEnablement.Enabled,
            Assert.Single(report.Resources, item => item.Kind == InventoryResourceKind.ShaderPack).Enablement);
        Assert.Equal("Test World",
            Assert.Single(report.Resources, item => item.Kind == InventoryResourceKind.World).WorldName);
    }

    [Fact]
    public async Task CacheIsRejectedAfterInstanceConfigurationChanges()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var inventory = provider.GetRequiredService<IInstanceInventoryService>();
        var created = (await instances.CreateAsync("Cache", "1.20.1")).Value!;

        var scanned = await inventory.ScanAsync(created.Id);
        Assert.True(scanned.IsSuccess, scanned.Error?.ToString());

        var cached = await inventory.GetCachedAsync(created.Id);
        Assert.True(cached.IsSuccess);
        Assert.NotNull(cached.Value);

        var updated = await instances.UpdateConfigurationAsync(created.Id, "1.21.1", "vanilla");
        Assert.True(updated.IsSuccess, updated.Error?.ToString());

        var stale = await inventory.GetCachedAsync(created.Id);
        Assert.True(stale.IsSuccess);
        Assert.Null(stale.Value);
    }

    [Fact]
    public async Task IncompatibleMinecraftRangeIsReportedAsIncompatible()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var inventory = provider.GetRequiredService<IInstanceInventoryService>();
        var created = (await instances.CreateAsync("Version", "1.20.1")).Value!;

        var mods = Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "mods");
        Directory.CreateDirectory(mods);
        CreateJar(
            Path.Combine(mods, "newer.jar"),
            """
            { "id": "newermod", "name": "Newer", "version": "1.0.0", "depends": { "minecraft": ">=1.21" } }
            """);

        var report = await inventory.ScanAsync(created.Id);
        Assert.True(report.IsSuccess, report.Error?.ToString());
        var mod = Assert.Single(report.Value!.Resources, item => item.Kind == InventoryResourceKind.Mod);
        Assert.Equal(CompatibilityState.Incompatible, mod.MinecraftCompatibility);
    }

    private static void CreateJar(string path, string fabricMetadata, byte[]? icon = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "fabric.mod.json", System.Text.Encoding.UTF8.GetBytes(fabricMetadata));
        if (icon is not null)
        {
            WriteEntry(archive, "icon.png", icon);
        }
    }

    private static void CreateZip(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "pack.mcmeta", "{\"pack\":{\"pack_format\":15}}"u8.ToArray());
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
