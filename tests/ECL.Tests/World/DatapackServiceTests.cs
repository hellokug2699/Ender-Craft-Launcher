// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.World;
using Ecl.World.Nbt;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.World;

/// <summary>
/// 数据包领域服务（`download-resource-template.md` §5.3、`resource-library-template.md` §5.1）：
/// 世界目标解析、level.dat DataPacks 的读取与写入、世界 datapacks 目录的受控导入/删除，
/// 以及目标不属于该实例/世界消失/level.dat 不可读时的拒绝路径。
/// </summary>
public sealed class DatapackServiceTests
{
    [Fact]
    public async Task ListsDatapacksAndReadsEnabledStateFromLevelDat()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var world = CreateWorld(
            game,
            "TestWorld",
            enabled: ["vanilla", "file/alpha.zip"],
            disabled: []);
        CreateDatapack(Path.Combine(world, "datapacks"), "alpha.zip", "Alpha Pack", 15);
        CreateDatapack(Path.Combine(world, "datapacks"), "beta.zip", "Beta Pack", 15);

        var service = provider.GetRequiredService<IDatapackService>();
        var worlds = await service.ListWorldsAsync(instance.Id);
        Assert.True(worlds.IsSuccess, worlds.Error?.ToString());
        var listed = Assert.Single(worlds.Value!.Worlds);
        Assert.Equal("TestWorld", listed.FolderName);

        var result = await service.ListAsync(
            instance.Id,
            new DatapackTarget(instance.Id, "TestWorld"));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var list = result.Value!;
        Assert.Equal(DatapackSelectionStatus.Valid, list.Status);
        Assert.Empty(list.MissingEntries);
        Assert.Empty(list.UnmanagedEntries);
        Assert.Empty(list.DuplicateEntries);
        Assert.Equal(2, list.Packs.Count);
        Assert.True(list.Packs.Single(pack => pack.FileName == "alpha.zip").Enabled);
        Assert.False(list.Packs.Single(pack => pack.FileName == "beta.zip").Enabled);
        Assert.Equal("Alpha Pack", list.Packs.Single(pack => pack.FileName == "alpha.zip").Name);
        Assert.Equal(15, list.Packs.Single(pack => pack.FileName == "alpha.zip").PackFormat);
    }

    [Fact]
    public async Task ApplyWritesOrderAndPreservesBuiltInEntries()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var world = CreateWorld(
            game,
            "TestWorld",
            enabled: ["vanilla", "file/alpha.zip"],
            disabled: ["file/legacy.zip"]);
        CreateDatapack(Path.Combine(world, "datapacks"), "alpha.zip", "Alpha", 15);
        CreateDatapack(Path.Combine(world, "datapacks"), "beta.zip", "Beta", 15);

        var service = provider.GetRequiredService<IDatapackService>();
        var applied = await service.ApplyAsync(
            instance.Id,
            new DatapackTarget(instance.Id, "TestWorld"),
            ["beta.zip", "alpha.zip"]);

        Assert.True(applied.IsSuccess, applied.Error?.ToString());
        var (enabled, disabled) = ReadDataPacks(Path.Combine(world, "level.dat"));
        Assert.Equal(["vanilla", "file/beta.zip", "file/alpha.zip"], enabled);
        // 受管条目从 Disabled 清理，内置条目保持原样。
        Assert.Empty(disabled);

        var list = await service.ListAsync(instance.Id, new DatapackTarget(instance.Id, "TestWorld"));
        Assert.True(list.IsSuccess, list.Error?.ToString());
        Assert.Equal(
            ["alpha.zip", "beta.zip"],
            list.Value!.Packs.Where(pack => pack.Enabled).Select(pack => pack.FileName).ToArray());
    }

    [Fact]
    public async Task ApplyCreatesVanillaDefaultForOlderWorlds()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var world = CreateWorld(game, "LegacyWorld", enabled: null, disabled: null);
        CreateDatapack(Path.Combine(world, "datapacks"), "alpha.zip", "Alpha", 15);

        var service = provider.GetRequiredService<IDatapackService>();
        var before = await service.ListAsync(instance.Id, new DatapackTarget(instance.Id, "LegacyWorld"));
        Assert.True(before.IsSuccess, before.Error?.ToString());
        Assert.Equal(DatapackSelectionStatus.NotInitialized, before.Value!.Status);

        var applied = await service.ApplyAsync(
            instance.Id,
            new DatapackTarget(instance.Id, "LegacyWorld"),
            ["alpha.zip"]);

        Assert.True(applied.IsSuccess, applied.Error?.ToString());
        var (enabled, _) = ReadDataPacks(Path.Combine(world, "level.dat"));
        Assert.Equal(["vanilla", "file/alpha.zip"], enabled);
    }

    [Fact]
    public async Task ImportAndDeleteStayInsideWorldAndPruneLevelDat()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var world = CreateWorld(game, "TestWorld", enabled: ["vanilla"], disabled: []);
        var source = CreateDatapackArchive(environment.Root, "imported.zip", "Imported", 15);

        var service = provider.GetRequiredService<IDatapackService>();
        var target = new DatapackTarget(instance.Id, "TestWorld");
        var imported = await service.ImportAsync(instance.Id, target, source);

        Assert.True(imported.IsSuccess, imported.Error?.ToString());
        var destination = Path.Combine(world, "datapacks", "imported.zip");
        Assert.True(File.Exists(destination));
        Assert.False(imported.Value!.Enabled);

        var applied = await service.ApplyAsync(instance.Id, target, ["imported.zip"]);
        Assert.True(applied.IsSuccess, applied.Error?.ToString());
        Assert.Equal(
            ["vanilla", "file/imported.zip"],
            ReadDataPacks(Path.Combine(world, "level.dat")).Enabled);

        var deleted = await service.DeleteAsync(instance.Id, target, "imported.zip");
        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.False(File.Exists(destination));
        Assert.Equal(["vanilla"], ReadDataPacks(Path.Combine(world, "level.dat")).Enabled);
    }

    [Fact]
    public async Task ImportRefusesExistingFileWithoutOverwrite()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var world = CreateWorld(game, "TestWorld", enabled: ["vanilla"], disabled: []);
        CreateDatapack(Path.Combine(world, "datapacks"), "alpha.zip", "Alpha", 15);
        var source = CreateDatapackArchive(environment.Root, "alpha.zip", "Replacement", 15);

        var service = provider.GetRequiredService<IDatapackService>();
        var result = await service.ImportAsync(
            instance.Id,
            new DatapackTarget(instance.Id, "TestWorld"),
            source);

        Assert.False(result.IsSuccess);
        Assert.Equal("datapack.target_exists", result.Error!.Code);
    }

    [Fact]
    public async Task ListReportsMissingDuplicatesAndUnmanagedEntries()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var world = CreateWorld(
            game,
            "TestWorld",
            enabled: ["vanilla", "file/alpha.zip", "file/alpha.zip", "file/missing.zip", "custom_pack"],
            disabled: []);
        CreateDatapack(Path.Combine(world, "datapacks"), "alpha.zip", "Alpha", 15);

        var service = provider.GetRequiredService<IDatapackService>();
        var result = await service.ListAsync(instance.Id, new DatapackTarget(instance.Id, "TestWorld"));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var list = result.Value!;
        Assert.Equal(["file/missing.zip"], list.MissingEntries);
        Assert.Equal(["custom_pack"], list.UnmanagedEntries);
        Assert.Equal(["file/alpha.zip"], list.DuplicateEntries);
    }

    [Fact]
    public async Task TargetMustBelongToTheInstanceAndStillExist()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        CreateWorld(game, "TestWorld", enabled: ["vanilla"], disabled: []);

        var service = provider.GetRequiredService<IDatapackService>();
        var otherInstance = await service.ListAsync(
            instance.Id,
            new DatapackTarget("another-instance", "TestWorld"));
        Assert.False(otherInstance.IsSuccess);
        Assert.Equal("datapack.target_instance_mismatch", otherInstance.Error!.Code);

        var missing = await service.ListAsync(
            instance.Id,
            new DatapackTarget(instance.Id, "MissingWorld"));
        Assert.False(missing.IsSuccess);
        Assert.Equal("datapack.world_missing", missing.Error!.Code);

        var outside = await service.ListAsync(
            instance.Id,
            new DatapackTarget(instance.Id, "../escape"));
        Assert.False(outside.IsSuccess);
    }

    [Fact]
    public async Task UnreadableLevelDatIsReportedAndWritesAreRefused()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var world = CreateWorld(game, "BrokenWorld", enabled: ["vanilla"], disabled: []);
        var levelDat = Path.Combine(world, "level.dat");
        await File.WriteAllBytesAsync(levelDat, [0x01, 0x02, 0x03]);
        CreateDatapack(Path.Combine(world, "datapacks"), "alpha.zip", "Alpha", 15);

        var service = provider.GetRequiredService<IDatapackService>();
        var worlds = await service.ListWorldsAsync(instance.Id);
        Assert.True(worlds.IsSuccess, worlds.Error?.ToString());
        Assert.DoesNotContain(worlds.Value!.Worlds, world => world.FolderName == "BrokenWorld");

        var target = new DatapackTarget(instance.Id, "BrokenWorld");
        var list = await service.ListAsync(instance.Id, target);
        Assert.False(list.IsSuccess);
        Assert.Equal("datapack.level_unreadable", list.Error!.Code);

        var applied = await service.ApplyAsync(instance.Id, target, ["alpha.zip"]);
        Assert.False(applied.IsSuccess);
        Assert.Equal("datapack.level_unreadable", applied.Error!.Code);

        var deleted = await service.DeleteAsync(instance.Id, target, "alpha.zip");
        Assert.False(deleted.IsSuccess);
        Assert.Equal("datapack.level_unreadable", deleted.Error!.Code);
        Assert.True(File.Exists(Path.Combine(world, "datapacks", "alpha.zip")));
    }

    [Fact]
    public async Task CorruptZipIsListedAsCorruptInsteadOfFailingTheList()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var world = CreateWorld(game, "TestWorld", enabled: ["vanilla"], disabled: []);
        var datapacks = Path.Combine(world, "datapacks");
        Directory.CreateDirectory(datapacks);
        await File.WriteAllTextAsync(Path.Combine(datapacks, "broken.zip"), "not a zip");

        var service = provider.GetRequiredService<IDatapackService>();
        var result = await service.ListAsync(instance.Id, new DatapackTarget(instance.Id, "TestWorld"));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var pack = Assert.Single(result.Value!.Packs);
        Assert.Equal(DatapackReadState.Corrupt, pack.ReadState);
    }

    private static async Task<GameInstance> CreateInstanceAsync(IServiceProvider provider)
    {
        var result = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Datapack Test", "1.21.1");
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!;
    }

    /// <summary>
    /// 构造一个世界存档：level.dat 携带（或缺少）DataPacks 配置。
    /// <paramref name="enabled"/> 为 <c>null</c> 时模拟旧世界（没有 DataPacks 键）。
    /// </summary>
    private static string CreateWorld(
        string gameDirectory,
        string folderName,
        IReadOnlyList<string>? enabled,
        IReadOnlyList<string>? disabled)
    {
        var world = Path.Combine(gameDirectory, "saves", folderName);
        Directory.CreateDirectory(Path.Combine(world, "region"));
        var data = NbtTag.Compound("Data")
            .Add("LevelName", NbtTagType.String, folderName)
            .Add("DataVersion", NbtTagType.Int, 3955)
            .Add("LastPlayed", NbtTagType.Long, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (enabled is not null)
        {
            var dataPacks = NbtTag.Compound("DataPacks");
            dataPacks.Add(CreateStringList("Enabled", enabled));
            if (disabled is not null)
            {
                dataPacks.Add(CreateStringList("Disabled", disabled));
            }

            data.Add(dataPacks);
        }

        NbtFile.WriteFile(
            Path.Combine(world, "level.dat"),
            NbtTag.Compound().Add(data),
            NbtCompression.Gzip);
        return world;
    }

    private static NbtTag CreateStringList(string name, IReadOnlyList<string> values)
    {
        var list = NbtTag.List(name, NbtTagType.String);
        foreach (var value in values)
        {
            list.AddTo(new NbtTag(NbtTagType.String, string.Empty, value));
        }

        return list;
    }

    private static void CreateDatapack(
        string directory,
        string fileName,
        string description,
        int packFormat)
    {
        Directory.CreateDirectory(directory);
        using (var archive = ZipFile.Open(Path.Combine(directory, fileName), ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("pack.mcmeta");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(JsonSerializer.Serialize(new
            {
                pack = new
                {
                    pack_format = packFormat,
                    description,
                },
            }));
        }
    }

    private static string CreateDatapackArchive(
        string root,
        string fileName,
        string description,
        int packFormat)
    {
        var path = Path.Combine(root, "sources", fileName);
        CreateDatapack(Path.GetDirectoryName(path)!, fileName, description, packFormat);
        return path;
    }

    private static (IReadOnlyList<string> Enabled, IReadOnlyList<string> Disabled) ReadDataPacks(
        string levelDatPath)
    {
        var root = NbtFile.ReadFile(levelDatPath);
        var dataPacks = root.Child("Data")?.Child("DataPacks");
        return (ReadList(dataPacks?.Child("Enabled")), ReadList(dataPacks?.Child("Disabled")));
    }

    private static IReadOnlyList<string> ReadList(NbtTag? list) =>
        list is { Type: NbtTagType.List }
            ? list.Children.Select(child => child.GetString() ?? string.Empty).ToArray()
            : [];
}
