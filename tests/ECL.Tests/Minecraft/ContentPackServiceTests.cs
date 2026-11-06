// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class ContentPackServiceTests
{
    [Fact]
    public async Task ListsImportsAndDeletesResourcePacks()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var packPath = CreateResourcePack("Demo Pack");

        try
        {
            var service = provider.GetRequiredService<IContentPackService>();
            var imported = await service.ImportAsync(instance.Id, ContentPackKind.ResourcePack, packPath);

            Assert.True(imported.IsSuccess, imported.Error?.ToString());
            Assert.Equal("Demo Pack", imported.Value!.Name);
            Assert.Equal(15, imported.Value.PackFormat);
            Assert.False(imported.Value.Enabled);

            var listed = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            Assert.True(listed.IsSuccess);
            Assert.Single(listed.Value!.Packs);

            Assert.True((await service.DeleteAsync(
                instance.Id,
                ContentPackKind.ResourcePack,
                Path.GetFileName(packPath))).IsSuccess);
            var afterDelete = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            Assert.Empty(afterDelete.Value!.Packs);
        }
        finally
        {
            File.Delete(packPath);
        }
    }

    [Fact]
    public async Task ListsShaderPacksSeparately()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var shaderPath = CreateShaderPack();

        try
        {
            var service = provider.GetRequiredService<IContentPackService>();
            var imported = await service.ImportAsync(instance.Id, ContentPackKind.ShaderPack, shaderPath);
            Assert.True(imported.IsSuccess, imported.Error?.ToString());

            var resources = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            var shaders = await service.ListAsync(instance.Id, ContentPackKind.ShaderPack);
            Assert.Empty(resources.Value!.Packs);
            Assert.Single(shaders.Value!.Packs);
        }
        finally
        {
            File.Delete(shaderPath);
        }
    }

    [Fact]
    public async Task EnableAndDisableResourcePackPreservesOptionsTxt()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var packPath = CreateResourcePack("Demo Pack");
        var optionsPath = GetOptionsPath(environment, instance);

        try
        {
            var options = string.Join(
                "\r\n",
                "version:3955",
                "# 用户注释",
                "renderDistance:12",
                "resourcePacks:[\"vanilla\"]",
                "soundCategory_master:1.0",
                string.Empty);
            File.WriteAllText(optionsPath, options, new UTF8Encoding(false));

            var service = provider.GetRequiredService<IContentPackService>();
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ResourcePack, packPath)).IsSuccess);

            var enabled = await service.SetEnabledAsync(
                instance.Id,
                ContentPackKind.ResourcePack,
                Path.GetFileName(packPath),
                enabled: true);
            Assert.True(enabled.IsSuccess, enabled.Error?.ToString());

            var afterEnable = File.ReadAllText(optionsPath);
            Assert.Contains($"resourcePacks:[\"vanilla\",\"file/{Path.GetFileName(packPath)}\"]", afterEnable);
            Assert.Contains("version:3955", afterEnable);
            Assert.Contains("# 用户注释", afterEnable);
            Assert.Contains("renderDistance:12", afterEnable);
            Assert.Contains("soundCategory_master:1.0", afterEnable);
            Assert.Contains("\r\n", afterEnable);
            Assert.DoesNotContain("\n", afterEnable.Replace("\r\n", string.Empty));
            Assert.True(File.Exists(optionsPath + ".bak"));
            Assert.Equal(options, File.ReadAllText(optionsPath + ".bak"));

            var listed = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            Assert.True(listed.Value!.Packs.Single().Enabled);
            Assert.Equal(1, listed.Value.EnabledCount);

            var disabled = await service.SetEnabledAsync(
                instance.Id,
                ContentPackKind.ResourcePack,
                Path.GetFileName(packPath),
                enabled: false);
            Assert.True(disabled.IsSuccess, disabled.Error?.ToString());

            var afterDisable = File.ReadAllText(optionsPath);
            Assert.Contains("resourcePacks:[\"vanilla\"]", afterDisable);
            Assert.DoesNotContain("file/", afterDisable.Split('\n')[3]);
            Assert.False((await service.ListAsync(instance.Id, ContentPackKind.ResourcePack)).Value!.Packs.Single().Enabled);
        }
        finally
        {
            File.Delete(packPath);
        }
    }

    [Fact]
    public async Task EnablingCreatesOptionsTxtWithVanillaDefault()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var packPath = CreateResourcePack("Demo Pack");
        var optionsPath = GetOptionsPath(environment, instance);

        try
        {
            var service = provider.GetRequiredService<IContentPackService>();
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ResourcePack, packPath)).IsSuccess);

            var before = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            Assert.Equal(PackOptionsStatus.NotInitialized, before.Value!.Status);
            Assert.False(File.Exists(optionsPath));

            var result = await service.SetEnabledAsync(
                instance.Id,
                ContentPackKind.ResourcePack,
                Path.GetFileName(packPath),
                enabled: true);
            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Equal(
                $"resourcePacks:[\"vanilla\",\"file/{Path.GetFileName(packPath)}\"]",
                File.ReadAllText(optionsPath).Trim());
        }
        finally
        {
            File.Delete(packPath);
        }
    }

    [Fact]
    public async Task EnablingMissingFileFailsWithoutCreatingOptionsTxt()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var optionsPath = GetOptionsPath(environment, instance);
        var service = provider.GetRequiredService<IContentPackService>();

        var result = await service.SetEnabledAsync(
            instance.Id,
            ContentPackKind.ResourcePack,
            "missing.zip",
            enabled: true);

        Assert.False(result.IsSuccess);
        Assert.Equal("pack.not_found", result.Error!.Code);
        Assert.False(File.Exists(optionsPath));
        Assert.False(File.Exists(optionsPath + ".bak"));
    }

    [Fact]
    public async Task CorruptResourcePackListIsReportedAndNeverOverwritten()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var packPath = CreateResourcePack("Demo Pack");
        var optionsPath = GetOptionsPath(environment, instance);
        const string corrupt = "resourcePacks:[\"vanilla\",\"file/broken.zip\"\r\n";
        File.WriteAllText(optionsPath, corrupt, new UTF8Encoding(false));

        try
        {
            var service = provider.GetRequiredService<IContentPackService>();
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ResourcePack, packPath)).IsSuccess);

            var listed = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            Assert.True(listed.IsSuccess);
            Assert.Equal(PackOptionsStatus.Unreadable, listed.Value!.Status);
            Assert.False(string.IsNullOrWhiteSpace(listed.Value.Warning));

            var result = await service.SetEnabledAsync(
                instance.Id,
                ContentPackKind.ResourcePack,
                Path.GetFileName(packPath),
                enabled: true);
            Assert.False(result.IsSuccess);
            Assert.Equal("options.invalid_value", result.Error!.Code);
            Assert.Equal(corrupt, File.ReadAllText(optionsPath));
            Assert.False(File.Exists(optionsPath + ".bak"));
        }
        finally
        {
            File.Delete(packPath);
        }
    }

    [Fact]
    public async Task ReportsMissingDuplicateAndUnmanagedEntriesWithoutTouchingThem()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var packPath = CreateResourcePack("Demo Pack");
        var fileName = Path.GetFileName(packPath);
        var optionsPath = GetOptionsPath(environment, instance);
        var options = string.Join(
            "\r\n",
            "resourcePacks:[\"vanilla\",\"file/missing.zip\",\"file/" + fileName + "\",\"file/" + fileName + "\",\"legacy_pack\"]",
            "incompatibleResourcePacks:[\"file/" + fileName + "\"]",
            string.Empty);
        File.WriteAllText(optionsPath, options, new UTF8Encoding(false));

        try
        {
            var service = provider.GetRequiredService<IContentPackService>();
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ResourcePack, packPath)).IsSuccess);

            var listed = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            var list = listed.Value!;
            var pack = Assert.Single(list.Packs);
            Assert.True(pack.Enabled);
            Assert.True(pack.Incompatible);
            Assert.Contains("file/missing.zip", list.MissingEntries);
            Assert.Contains("legacy_pack", list.UnmanagedEntries);
            Assert.DoesNotContain("vanilla", list.UnmanagedEntries);
            Assert.Contains($"file/{fileName}", list.DuplicateEntries);

            // 只读查询不重写文件。
            Assert.Equal(options, File.ReadAllText(optionsPath));

            // 禁用时移除启用条目与“已确认不兼容”记录，并保留其它条目。
            var result = await service.SetEnabledAsync(
                instance.Id,
                ContentPackKind.ResourcePack,
                fileName,
                enabled: false);
            Assert.True(result.IsSuccess, result.Error?.ToString());

            var after = File.ReadAllText(optionsPath);
            Assert.Contains("resourcePacks:[\"vanilla\",\"file/missing.zip\",\"legacy_pack\"]", after);
            Assert.Contains("incompatibleResourcePacks:[]", after);
        }
        finally
        {
            File.Delete(packPath);
        }
    }

    [Fact]
    public async Task PicksUpExternalOptionsTxtChanges()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var packPath = CreateResourcePack("Demo Pack");
        var optionsPath = GetOptionsPath(environment, instance);

        try
        {
            var service = provider.GetRequiredService<IContentPackService>();
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ResourcePack, packPath)).IsSuccess);

            File.WriteAllText(
                optionsPath,
                $"resourcePacks:[\"file/{Path.GetFileName(packPath)}\"]\r\n",
                new UTF8Encoding(false));

            var listed = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            Assert.True(listed.Value!.Packs.Single().Enabled);
        }
        finally
        {
            File.Delete(packPath);
        }
    }

    [Fact]
    public async Task ShaderPackSelectionReplacesActiveAndClearsOnDisable()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var first = CreateShaderPack();
        var second = CreateShaderPack();
        var optionsPath = GetOptionsPath(environment, instance);

        try
        {
            var service = provider.GetRequiredService<IContentPackService>();
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ShaderPack, first)).IsSuccess);
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ShaderPack, second)).IsSuccess);

            Assert.True((await service.SetEnabledAsync(
                instance.Id,
                ContentPackKind.ShaderPack,
                Path.GetFileName(first),
                enabled: true)).IsSuccess);
            Assert.Contains($"shaderPack:{Path.GetFileName(first)}", File.ReadAllText(optionsPath));

            Assert.True((await service.SetEnabledAsync(
                instance.Id,
                ContentPackKind.ShaderPack,
                Path.GetFileName(second),
                enabled: true)).IsSuccess);
            var afterSwitch = File.ReadAllText(optionsPath);
            Assert.Contains($"shaderPack:{Path.GetFileName(second)}", afterSwitch);
            Assert.DoesNotContain(Path.GetFileName(first), afterSwitch);

            var listed = await service.ListAsync(instance.Id, ContentPackKind.ShaderPack);
            Assert.Equal(1, listed.Value!.EnabledCount);
            Assert.True(listed.Value.Packs.Single(pack => pack.FileName == Path.GetFileName(second)).Enabled);
            Assert.False(listed.Value.Packs.Single(pack => pack.FileName == Path.GetFileName(first)).Enabled);
            Assert.All(listed.Value.Packs, pack => Assert.False(pack.Incompatible));

            Assert.True((await service.SetEnabledAsync(
                instance.Id,
                ContentPackKind.ShaderPack,
                Path.GetFileName(second),
                enabled: false)).IsSuccess);
            Assert.DoesNotContain("shaderPack", File.ReadAllText(optionsPath));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public async Task DeletingEnabledPackRemovesSelectionEntry()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var packPath = CreateResourcePack("Demo Pack");
        var fileName = Path.GetFileName(packPath);
        var optionsPath = GetOptionsPath(environment, instance);

        try
        {
            var service = provider.GetRequiredService<IContentPackService>();
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ResourcePack, packPath)).IsSuccess);
            Assert.True((await service.SetEnabledAsync(
                instance.Id,
                ContentPackKind.ResourcePack,
                fileName,
                enabled: true)).IsSuccess);
            Assert.Contains($"file/{fileName}", File.ReadAllText(optionsPath));

            Assert.True((await service.DeleteAsync(instance.Id, ContentPackKind.ResourcePack, fileName)).IsSuccess);

            var after = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            Assert.Empty(after.Value!.Packs);
            Assert.Empty(after.Value.MissingEntries);
            Assert.DoesNotContain(fileName, File.ReadAllText(optionsPath));
        }
        finally
        {
            File.Delete(packPath);
        }
    }

    [Theory]
    [InlineData("..\\evil.zip")]
    [InlineData("../evil.zip")]
    [InlineData("sub/evil.zip")]
    [InlineData("")]
    public async Task RejectsInvalidFileNames(string fileName)
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var service = provider.GetRequiredService<IContentPackService>();

        var result = await service.SetEnabledAsync(instance.Id, ContentPackKind.ResourcePack, fileName, enabled: true);
        Assert.False(result.IsSuccess);
        Assert.Equal("pack.invalid_file", result.Error!.Code);
    }

    [Fact]
    public async Task ApplyAsyncReordersManagedPacksAndKeepsUnmanagedEntriesInPlace()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var first = CreateResourcePack("First");
        var second = CreateResourcePack("Second");
        var optionsPath = GetOptionsPath(environment, instance);
        var firstEntry = $"file/{Path.GetFileName(first)}";
        var secondEntry = $"file/{Path.GetFileName(second)}";

        try
        {
            var service = provider.GetRequiredService<IContentPackService>();
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ResourcePack, first)).IsSuccess);
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ResourcePack, second)).IsSuccess);

            var options = string.Join(
                "\r\n",
                "version:3955",
                $"resourcePacks:[\"vanilla\",\"legacy_pack\",\"{firstEntry}\",\"{secondEntry}\"]",
                $"incompatibleResourcePacks:[\"{secondEntry}\"]",
                string.Empty);
            File.WriteAllText(optionsPath, options, new UTF8Encoding(false));

            var applied = await service.ApplyAsync(
                instance.Id,
                ContentPackKind.ResourcePack,
                [Path.GetFileName(second), Path.GetFileName(first)]);
            Assert.True(applied.IsSuccess, applied.Error?.ToString());

            var after = File.ReadAllText(optionsPath);
            // 顺序按页面提交的顺序重写；vanilla 与其它启动器写入的裸值保持原位置。
            Assert.Contains(
                $"resourcePacks:[\"vanilla\",\"legacy_pack\",\"{secondEntry}\",\"{firstEntry}\"]",
                after);
            // 仍然启用且被游戏标记为不兼容的条目保持记录，页面据此显示“异常”状态。
            Assert.Contains($"incompatibleResourcePacks:[\"{secondEntry}\"]", after);
            Assert.Contains("version:3955", after);

            // 只写入一次：.bak 保存的是提交前的原文件。
            Assert.Equal(options, File.ReadAllText(optionsPath + ".bak"));

            var listed = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            Assert.Equal(2, listed.Value!.EnabledCount);
            Assert.True(listed.Value.Packs.Single(pack => pack.FileName == Path.GetFileName(second)).Incompatible);

            // 提交空列表等价于禁用全部受管包，未受管条目仍然保留；禁用时清理“已确认不兼容”记录。
            Assert.True((await service.ApplyAsync(instance.Id, ContentPackKind.ResourcePack, [])).IsSuccess);
            var cleared = File.ReadAllText(optionsPath);
            Assert.Contains("resourcePacks:[\"vanilla\",\"legacy_pack\"]", cleared);
            Assert.Contains("incompatibleResourcePacks:[]", cleared);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public async Task ApplyAsyncSelectsOnlyOneShaderPackAndCanClearSelection()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var first = CreateShaderPack();
        var second = CreateShaderPack();
        var optionsPath = GetOptionsPath(environment, instance);

        try
        {
            var service = provider.GetRequiredService<IContentPackService>();
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ShaderPack, first)).IsSuccess);
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ShaderPack, second)).IsSuccess);

            var applied = await service.ApplyAsync(
                instance.Id,
                ContentPackKind.ShaderPack,
                [Path.GetFileName(first), Path.GetFileName(second)]);
            Assert.True(applied.IsSuccess, applied.Error?.ToString());
            // 光影包一次只能选一个：取列表最后一个作为当前选择。
            Assert.Contains($"shaderPack:{Path.GetFileName(second)}", File.ReadAllText(optionsPath));

            Assert.True((await service.ApplyAsync(instance.Id, ContentPackKind.ShaderPack, [])).IsSuccess);
            Assert.DoesNotContain("shaderPack", File.ReadAllText(optionsPath));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public async Task ApplyAsyncRejectsMissingFilesAndUnreadableOptions()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var packPath = CreateResourcePack("Demo Pack");
        var optionsPath = GetOptionsPath(environment, instance);

        try
        {
            var service = provider.GetRequiredService<IContentPackService>();
            Assert.True((await service.ImportAsync(instance.Id, ContentPackKind.ResourcePack, packPath)).IsSuccess);

            // 未导入的文件不能出现在启用列表里。
            var missing = await service.ApplyAsync(instance.Id, ContentPackKind.ResourcePack, ["missing.zip"]);
            Assert.False(missing.IsSuccess);
            Assert.Equal("pack.not_found", missing.Error!.Code);
            Assert.False(File.Exists(optionsPath));

            const string corrupt = "resourcePacks:[\"vanilla\",\"file/broken.zip\"\r\n";
            File.WriteAllText(optionsPath, corrupt, new UTF8Encoding(false));

            var rejected = await service.ApplyAsync(
                instance.Id,
                ContentPackKind.ResourcePack,
                [Path.GetFileName(packPath)]);
            Assert.False(rejected.IsSuccess);
            Assert.Equal("options.invalid_value", rejected.Error!.Code);
            Assert.Equal(corrupt, File.ReadAllText(optionsPath));
            Assert.False(File.Exists(optionsPath + ".bak"));

            var invalid = await service.ApplyAsync(instance.Id, ContentPackKind.ResourcePack, ["..\\evil.zip"]);
            Assert.False(invalid.IsSuccess);
            Assert.Equal("pack.invalid_file", invalid.Error!.Code);
        }
        finally
        {
            File.Delete(packPath);
        }
    }

    [Fact]
    public async Task ReportsReadStateForCorruptAndUnrecognizedPacks()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var resourceDirectory = Path.Combine(environment.Paths.GetInstanceGameDirectory(instance.Id), "resourcepacks");
        var shaderDirectory = Path.Combine(environment.Paths.GetInstanceGameDirectory(instance.Id), "shaderpacks");
        Directory.CreateDirectory(resourceDirectory);
        Directory.CreateDirectory(shaderDirectory);

        // 损坏的压缩包（内容不是 zip）。
        File.WriteAllText(Path.Combine(resourceDirectory, "broken.zip"), "not a zip");
        // 有效 ZIP 但没有 pack.mcmeta。
        using (var archive = ZipFile.Open(Path.Combine(resourceDirectory, "no-metadata.zip"), ZipArchiveMode.Create))
        {
            WriteEntry(archive, "assets/readme.txt", "hello");
        }

        // 有效 ZIP 但没有 shaders/ 目录的光影包。
        using (var archive = ZipFile.Open(Path.Combine(shaderDirectory, "no-shaders.zip"), ZipArchiveMode.Create))
        {
            WriteEntry(archive, "readme.txt", "hello");
        }

        var service = provider.GetRequiredService<IContentPackService>();
        var resources = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
        Assert.True(resources.IsSuccess, resources.Error?.ToString());
        Assert.Equal(
            ContentPackReadState.Corrupt,
            resources.Value!.Packs.Single(pack => pack.FileName == "broken.zip").ReadState);
        Assert.Equal(
            ContentPackReadState.Unrecognized,
            resources.Value.Packs.Single(pack => pack.FileName == "no-metadata.zip").ReadState);
        Assert.All(resources.Value.Packs, pack => Assert.Null(pack.PackFormat));

        var shaders = await service.ListAsync(instance.Id, ContentPackKind.ShaderPack);
        Assert.Equal(
            ContentPackReadState.Unrecognized,
            shaders.Value!.Packs.Single().ReadState);
    }

    [Fact]
    public async Task ReadsDeclaredSupportedFormatsAndResolvesOfficialVersionRanges()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var path = Path.Combine(Path.GetTempPath(), $"ecl-rp-{Guid.NewGuid():N}.zip");

        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "pack.mcmeta", """
                    {
                      "pack": {
                        "pack_format": 15,
                        "supported_formats": { "min_inclusive": 15, "max_inclusive": 34 },
                        "description": "Range Pack"
                      }
                    }
                    """);
            }

            var service = provider.GetRequiredService<IContentPackService>();
            var imported = await service.ImportAsync(instance.Id, ContentPackKind.ResourcePack, path);
            Assert.True(imported.IsSuccess, imported.Error?.ToString());
            var entry = imported.Value!;
            Assert.Equal(ContentPackReadState.Ok, entry.ReadState);
            Assert.Equal(new PackFormatRange(15, 34), entry.SupportedFormats);
            Assert.Equal(new ResourcePackFormats.VersionRange("1.20", "1.21.1"), ResourcePackFormats.Resolve(entry.SupportedFormats!.Value));

            // 官方对照表只覆盖已核实的格式；未收录的格式返回未知，不按经验外推。
            Assert.Equal(new ResourcePackFormats.VersionRange("1.20", "1.20.1"), ResourcePackFormats.Resolve(15));
            Assert.Equal(new ResourcePackFormats.VersionRange("1.21.4", "1.21.4"), ResourcePackFormats.Resolve(46));
            Assert.Null(ResourcePackFormats.Resolve(10));
            Assert.Null(ResourcePackFormats.Resolve(999));

            // 声明的范围有一端无法解析时，不给出猜测的版本范围。
            Assert.Null(ResourcePackFormats.Resolve(new PackFormatRange(15, 999)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string GetOptionsPath(TestEnvironment environment, GameInstance instance) =>
        Path.Combine(environment.Paths.GetInstanceGameDirectory(instance.Id), "options.txt");

    private static async Task<GameInstance> CreateInstanceAsync(IServiceProvider provider)
    {
        var result = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Pack Test", "1.21.1", "fabric", "0.16.0");
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static string CreateResourcePack(string description)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ecl-rp-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "pack.mcmeta", $$"""
            { "pack": { "pack_format": 15, "description": "{{description}}" } }
            """);
        return path;
    }

    private static string CreateShaderPack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ecl-sp-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "shaders/final.fsh", "void main() {}");
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }
}
