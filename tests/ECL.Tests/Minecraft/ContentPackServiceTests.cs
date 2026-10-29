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
