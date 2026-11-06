// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text.Json;
using Ecl.Minecraft;
using Ecl.Minecraft.Export;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 实例导出（B2c，export.md）：分类与体积、固定输出目录、完整/自定义/诊断包、
/// 永久排除秘密信息，以及部分文件失败时不得谎报完整成功。
/// </summary>
public sealed class InstanceExportServiceTests
{
    [Fact]
    public async Task PlanClassifiesContentWithoutMixingCategories()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Write(game, "mods/a.jar", 1024);
        Write(game, "resourcepacks/rp.zip", 512);
        Write(game, "shaderpacks/sp.zip", 256);
        Write(game, "saves/world/level.dat", 128);
        Write(game, "screenshots/a.png", 64);
        Write(game, "schematics/a.litematic", 32);
        Write(game, "config/worldedit/schematics/a.schem", 16);
        Write(game, "logs/latest.log", 8);
        Write(game, "crash-reports/crash.txt", 4);
        Write(game, "options.txt", 2);
        Write(game, "config/foo.json", 1);
        Write(game, "unknown.bin", 1);

        var service = provider.GetRequiredService<IInstanceExportService>();
        var result = await service.PlanAsync(instance.Id);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var plan = result.Value!;
        Assert.Equal(1, Category(plan, "mods").FileCount);
        Assert.Equal(1, Category(plan, "resourcePacks").FileCount);
        Assert.Equal(1, Category(plan, "shaderPacks").FileCount);
        Assert.Equal(1, Category(plan, "worlds").FileCount);
        Assert.Equal(1, Category(plan, "screenshots").FileCount);
        Assert.Equal(2, Category(plan, "schematics").FileCount);
        Assert.Equal(2, Category(plan, "logs").FileCount);
        // options.txt + crash-reports/crash.txt 之外的 config/foo.json；WorldEdit 目录归投影类别。
        Assert.Equal(2, Category(plan, "config").FileCount);
        Assert.Equal(1, Category(plan, "other").FileCount);
        Assert.Equal(12, plan.FileCount);
        Assert.True(plan.Complete);
    }

    [Fact]
    public async Task PlanPermanentlyExcludesSecretsAndTheExportsFolder()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Write(game, "launcher_accounts.json", 10);
        Write(game, "config/launcher_profiles.json", 10);
        Write(game, "config/private.pem", 10);
        Write(game, "mods/download.jar.part", 10);
        Write(game, "exports/previous.zip", 10);
        Write(game, "mods/keep.jar", 10);

        var service = provider.GetRequiredService<IInstanceExportService>();
        var plan = await service.PlanAsync(instance.Id);

        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        Assert.Equal(1, plan.Value!.FileCount);
        Assert.Equal(5, plan.Value.ExcludedFileCount);
        Assert.Equal(1, Category(plan.Value, "mods").FileCount);
    }

    [Fact]
    public async Task FullExportWritesManifestInstanceMetadataAndMinecraftContent()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Write(game, "mods/a.jar", 1024);
        Write(game, "saves/world/level.dat", 128);

        var service = provider.GetRequiredService<IInstanceExportService>();
        var result = await service.ExportAsync(instance.Id, new ExportRequest(InstanceExportMode.Full));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.True(File.Exists(report.OutputPath));
        Assert.Equal(
            Path.Combine(game, "exports"),
            report.OutputDirectory);
        Assert.True(report.IsComplete);
        Assert.False(report.IsDiagnostics);
        Assert.Empty(report.Skipped);
        Assert.Equal(2, report.FileCount);

        using var archive = ZipFile.OpenRead(report.OutputPath);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("manifest.json", names);
        Assert.Contains("instance/instance.json", names);
        Assert.Contains("minecraft/mods/a.jar", names);
        Assert.Contains("minecraft/saves/world/level.dat", names);

        using var manifest = ReadManifest(archive);
        Assert.Equal("Full", manifest.RootElement.GetProperty("mode").GetString());
        Assert.Equal(2, manifest.RootElement.GetProperty("fileCount").GetInt32());
        Assert.True(manifest.RootElement.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task CustomExportOnlyContainsSelectedCategories()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Write(game, "mods/a.jar", 1024);
        Write(game, "saves/world/level.dat", 128);

        var service = provider.GetRequiredService<IInstanceExportService>();
        var result = await service.ExportAsync(
            instance.Id,
            new ExportRequest(InstanceExportMode.Custom, new HashSet<string>(["mods"], StringComparer.Ordinal)));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        using var archive = ZipFile.OpenRead(result.Value!.OutputPath);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("minecraft/mods/a.jar", names);
        Assert.DoesNotContain("minecraft/saves/world/level.dat", names);
        using var manifest = ReadManifest(archive);
        Assert.Equal(["mods"], manifest.RootElement.GetProperty("categories").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task CustomExportWithoutCategoriesFailsInsteadOfExportingEverything()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Write(game, "mods/a.jar", 10);

        var service = provider.GetRequiredService<IInstanceExportService>();
        var result = await service.ExportAsync(
            instance.Id,
            new ExportRequest(InstanceExportMode.Custom, new HashSet<string>(StringComparer.Ordinal)));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task FullExportNeverContainsPermanentlyExcludedSecrets()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Write(game, "launcher_accounts.json", 10);
        Write(game, "config/private.pem", 10);
        Write(game, "mods/a.jar", 10);

        var service = provider.GetRequiredService<IInstanceExportService>();
        var result = await service.ExportAsync(instance.Id, new ExportRequest(InstanceExportMode.Full));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        using var archive = ZipFile.OpenRead(result.Value!.OutputPath);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("minecraft/mods/a.jar", names);
        Assert.DoesNotContain(names, name => name.Contains("launcher_accounts", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, result.Value.ExcludedFileCount);
    }

    [Fact]
    public async Task UnreadableFileIsReportedAndMarksTheExportIncomplete()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Write(game, "mods/a.jar", 1024);
        var lockedPath = Path.Combine(game, "mods", "locked.jar");
        await File.WriteAllBytesAsync(lockedPath, new byte[512]);

        var service = provider.GetRequiredService<IInstanceExportService>();
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await service.ExportAsync(instance.Id, new ExportRequest(InstanceExportMode.Full));

            Assert.True(result.IsSuccess, result.Error?.ToString());
            var report = result.Value!;
            Assert.False(report.IsComplete);
            Assert.Contains(report.Skipped, item => item.RelativePath == "mods/locked.jar");
            Assert.True(File.Exists(report.OutputPath));
        }
    }

    [Fact]
    public async Task DiagnosticsExportReusesTheDiagnosticServiceAndWritesUnderExports()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var game = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Directory.CreateDirectory(Path.Combine(game, "logs"));
        await File.WriteAllTextAsync(Path.Combine(game, "logs", "latest.log"), "test log");

        var service = provider.GetRequiredService<IInstanceExportService>();
        var result = await service.ExportAsync(
            instance.Id,
            new ExportRequest(InstanceExportMode.Diagnostics));

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.True(report.IsDiagnostics);
        Assert.True(report.IsComplete);
        Assert.True(report.DiagnosticEntries > 0);
        Assert.True(File.Exists(report.OutputPath));
        Assert.Equal(Path.Combine(game, "exports"), report.OutputDirectory);
        using var archive = ZipFile.OpenRead(report.OutputPath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
    }

    private static JsonDocument ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("缺少 manifest.json");
        using var stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static ExportCategorySummary Category(InstanceExportPlan plan, string id) =>
        plan.Categories.Single(category => category.Id == id);

    private static void Write(string gameRoot, string relativePath, int bytes)
    {
        var path = Path.Combine(gameRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private static async Task<GameInstance> CreateInstanceAsync(IServiceProvider provider)
    {
        var result = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Export Test", "1.21.1", "fabric", "0.16.0");
        Assert.True(result.IsSuccess);
        return result.Value!;
    }
}
