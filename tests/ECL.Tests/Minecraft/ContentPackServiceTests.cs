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

            var listed = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            Assert.True(listed.IsSuccess);
            Assert.Single(listed.Value!);

            Assert.True((await service.DeleteAsync(
                instance.Id,
                ContentPackKind.ResourcePack,
                Path.GetFileName(packPath))).IsSuccess);
            var afterDelete = await service.ListAsync(instance.Id, ContentPackKind.ResourcePack);
            Assert.Empty(afterDelete.Value!);
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
            Assert.Empty(resources.Value!);
            Assert.Single(shaders.Value!);
        }
        finally
        {
            File.Delete(shaderPath);
        }
    }

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
