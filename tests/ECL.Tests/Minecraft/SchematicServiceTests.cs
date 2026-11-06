// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Schematics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 投影文件领域服务（B2c）：扫描工具实际使用的目录、按签名区分已识别/未验证格式，
/// 删除经过实例事务且只允许投影目录中的已知格式。
/// </summary>
public sealed class SchematicServiceTests
{
    private static readonly byte[] GzipHeader = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00];

    [Fact]
    public async Task ListsKnownFormatsFromTheSchematicsWorldEditAndAxiomFolders()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);

        var schematics = Path.Combine(game, "schematics");
        var worldEdit = Path.Combine(game, "config", "worldedit", "schematics");
        var axiom = Path.Combine(game, "config", "axiom", "blueprints");
        Directory.CreateDirectory(Path.Combine(schematics, "nested"));
        Directory.CreateDirectory(worldEdit);
        Directory.CreateDirectory(axiom);

        await File.WriteAllBytesAsync(Path.Combine(schematics, "build.litematic"), GzipHeader);
        await File.WriteAllBytesAsync(Path.Combine(schematics, "nested", "deep.litematic"), GzipHeader);
        await File.WriteAllBytesAsync(Path.Combine(schematics, "create.nbt"), GzipHeader);
        await File.WriteAllBytesAsync(Path.Combine(worldEdit, "sponge.schem"), GzipHeader);
        await File.WriteAllBytesAsync(Path.Combine(worldEdit, "legacy.schematic"), GzipHeader);
        await File.WriteAllBytesAsync(Path.Combine(axiom, "plan.bp"), GzipHeader);
        await File.WriteAllTextAsync(Path.Combine(schematics, "broken.schem"), "not nbt");
        await File.WriteAllTextAsync(Path.Combine(schematics, "readme.txt"), "ignored");

        var service = provider.GetRequiredService<ISchematicService>();
        var result = await service.ListAsync(instance.Id);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var items = result.Value!.Items;
        Assert.Equal(7, items.Count);
        Assert.Contains(items, item => item.FileName == "build.litematic" && item.Format == SchematicFormat.Litematica);
        Assert.Contains(items, item => item.FileName == "deep.litematic");
        Assert.Contains(items, item => item.FileName == "create.nbt" && item.Format == SchematicFormat.NbtStructure);
        Assert.Contains(items, item => item.FileName == "sponge.schem" && item.Format == SchematicFormat.SpongeSchematic);
        Assert.Contains(items, item => item.FileName == "legacy.schematic" && item.Format == SchematicFormat.MceSchematic);
        Assert.Contains(items, item => item.FileName == "plan.bp" && item.Format == SchematicFormat.AxiomBlueprint);

        Assert.Equal(
            SchematicRecognition.Recognized,
            items.Single(item => item.FileName == "build.litematic").Recognition);
        Assert.Equal(
            SchematicRecognition.Unverified,
            items.Single(item => item.FileName == "broken.schem").Recognition);
        Assert.Equal(
            SchematicRecognition.Unverified,
            items.Single(item => item.FileName == "plan.bp").Recognition);
    }

    [Fact]
    public async Task ReturnsEmptyListWhenNoSchematicFoldersExist()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);

        var service = provider.GetRequiredService<ISchematicService>();
        var result = await service.ListAsync(instance.Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
    }

    [Fact]
    public async Task DeletesFilesFromMultipleSchematicRootsThroughTheInstanceTransaction()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var schematics = Path.Combine(game, "schematics");
        var worldEdit = Path.Combine(game, "config", "worldedit", "schematics");
        Directory.CreateDirectory(schematics);
        Directory.CreateDirectory(worldEdit);
        var first = Path.Combine(schematics, "build.litematic");
        var second = Path.Combine(worldEdit, "sponge.schem");
        var keep = Path.Combine(schematics, "keep.nbt");
        await File.WriteAllBytesAsync(first, GzipHeader);
        await File.WriteAllBytesAsync(second, GzipHeader);
        await File.WriteAllBytesAsync(keep, GzipHeader);

        var service = provider.GetRequiredService<ISchematicService>();
        var paths = new[]
        {
            Path.Combine("schematics", "build.litematic"),
            Path.Combine("config", "worldedit", "schematics", "sponge.schem"),
        };
        var result = await service.DeleteAsync(instance.Id, paths);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(keep));
        Assert.False(result.Value!.RecoveryRequired);
    }

    [Fact]
    public async Task DeleteRejectsKnownExtensionsOutsideSchematicRoots()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var mods = Path.Combine(game, "mods");
        Directory.CreateDirectory(mods);
        var disguised = Path.Combine(mods, "not-a-schematic.litematic");
        await File.WriteAllBytesAsync(disguised, GzipHeader);
        await File.WriteAllTextAsync(Path.Combine(game, "options.txt"), "version:1");

        var service = provider.GetRequiredService<ISchematicService>();
        foreach (var path in new[]
                 {
                     Path.Combine("mods", "not-a-schematic.litematic"),
                     "options.txt",
                     Path.Combine("schematics", "..", "options.txt"),
                 })
        {
            var rejected = await service.DeleteAsync(instance.Id, [path]);
            Assert.False(rejected.IsSuccess, $"路径应被拒绝：{path}");
        }

        Assert.True(File.Exists(disguised));
    }

    [Fact]
    public async Task DeleteFailsAsAWholeWhenOneSelectedFileIsMissing()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var schematics = Path.Combine(game, "schematics");
        Directory.CreateDirectory(schematics);
        var existing = Path.Combine(schematics, "build.litematic");
        await File.WriteAllBytesAsync(existing, GzipHeader);

        var service = provider.GetRequiredService<ISchematicService>();
        var result = await service.DeleteAsync(
            instance.Id,
            [
                Path.Combine("schematics", "build.litematic"),
                Path.Combine("schematics", "missing.litematic"),
            ]);

        Assert.False(result.IsSuccess);
        Assert.True(File.Exists(existing));
    }

    private static async Task<GameInstance> CreateInstanceAsync(IServiceProvider provider)
    {
        var result = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Schematic Test", "1.21.1", "fabric", "0.16.0");
        Assert.True(result.IsSuccess);
        return result.Value!;
    }
}
