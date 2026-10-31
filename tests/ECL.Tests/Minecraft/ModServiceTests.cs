// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class ModServiceTests
{
    [Fact]
    public async Task ScansModsAndDetectsDependenciesConflictsAndLoaderMismatch()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var modsDirectory = GetModsDirectory(environment, instance);

        WriteJar(
            Path.Combine(modsDirectory, "alpha.jar"),
            """
            {
              "id": "alpha",
              "name": "Alpha",
              "version": "1.0",
              "depends": { "missinglib": "*" },
              "breaks": ["beta"]
            }
            """);
        WriteJar(
            Path.Combine(modsDirectory, "beta.jar"),
            """{ "id": "beta", "name": "Beta", "version": "1.0" }""");
        WriteJar(
            Path.Combine(modsDirectory, "gamma.jar.disabled"),
            """{ "id": "gamma", "name": "Gamma", "version": "1.0" }""");
        WriteForgeJar(Path.Combine(modsDirectory, "forge-mod.jar"), "forgemod");

        var report = await provider.GetRequiredService<IModService>().ScanAsync(instance.Id);

        Assert.True(report.IsSuccess, report.Error?.ToString());
        var scan = report.Value!;
        Assert.Equal(3, scan.EnabledCount);
        Assert.Equal(1, scan.DisabledCount);
        Assert.Contains(scan.Issues, issue => issue.Kind == ModIssueKind.MissingDependency && issue.RelatedModId == "missinglib");
        Assert.Contains(scan.Issues, issue => issue.Kind == ModIssueKind.Conflict && issue.RelatedModId == "beta");
        Assert.Contains(scan.Issues, issue => issue.Kind == ModIssueKind.LoaderMismatch && issue.ModId == "forgemod");
    }

    [Fact]
    public async Task EnablesAndDisablesModsIncludingBatchOperations()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var modsDirectory = GetModsDirectory(environment, instance);

        WriteJar(Path.Combine(modsDirectory, "alpha.jar"), """{ "id": "alpha", "name": "Alpha", "version": "1" }""");
        WriteJar(Path.Combine(modsDirectory, "beta.jar"), """{ "id": "beta", "name": "Beta", "version": "1" }""");
        WriteJar(Path.Combine(modsDirectory, "gamma.jar.disabled"), """{ "id": "gamma", "name": "Gamma", "version": "1" }""");

        var service = provider.GetRequiredService<IModService>();
        Assert.True((await service.SetEnabledAsync(instance.Id, "alpha.jar", false)).IsSuccess);
        Assert.True(File.Exists(Path.Combine(modsDirectory, "alpha.jar.disabled")));

        var enableAll = await service.SetAllEnabledAsync(instance.Id, true);
        Assert.True(enableAll.IsSuccess, enableAll.Error?.ToString());
        Assert.Equal(3, enableAll.Value!.Succeeded);
        Assert.False(File.Exists(Path.Combine(modsDirectory, "alpha.jar.disabled")));
        Assert.True(File.Exists(Path.Combine(modsDirectory, "gamma.jar")));

        var disableAll = await service.SetAllEnabledAsync(instance.Id, false);
        Assert.True(disableAll.IsSuccess, disableAll.Error?.ToString());
        Assert.Equal(3, disableAll.Value!.Succeeded);
        Assert.True(File.Exists(Path.Combine(modsDirectory, "alpha.jar.disabled")));
        Assert.True(File.Exists(Path.Combine(modsDirectory, "beta.jar.disabled")));
    }

    [Fact]
    public async Task ImportsDeletesAndRejectsInvalidFiles()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var service = provider.GetRequiredService<IModService>();
        var modsDirectory = GetModsDirectory(environment, instance);

        var source = Path.Combine(Path.GetTempPath(), $"ecl-import-{Guid.NewGuid():N}.jar");
        WriteJar(source, """{ "id": "imported", "name": "Imported", "version": "1" }""");
        var textFile = Path.Combine(Path.GetTempPath(), $"ecl-import-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(textFile, "not a mod");

        try
        {
            var result = await service.ImportAsync(instance.Id, [source, textFile]);
            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Equal(1, result.Value!.Succeeded);
            Assert.Single(result.Value.Errors);
            Assert.True(File.Exists(Path.Combine(modsDirectory, Path.GetFileName(source))));

            Assert.True((await service.DeleteAsync(instance.Id, Path.GetFileName(source))).IsSuccess);
            Assert.False(File.Exists(Path.Combine(modsDirectory, Path.GetFileName(source))));
        }
        finally
        {
            File.Delete(source);
            File.Delete(textFile);
        }
    }

    [Fact]
    public async Task RejectsModOperationsThatEscapeTheInstanceModsDirectory()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var service = provider.GetRequiredService<IModService>();
        var outside = Path.Combine(environment.Root, "outside.jar");
        await File.WriteAllTextAsync(outside, "sentinel");

        foreach (var fileName in new[] { "../outside.jar", "..\\outside.jar", outside })
        {
            var delete = await service.DeleteAsync(instance.Id, fileName);
            Assert.False(delete.IsSuccess);
            Assert.Equal("mod.invalid_file", delete.Error?.Code);

            var toggle = await service.SetEnabledAsync(instance.Id, fileName, enabled: false);
            Assert.False(toggle.IsSuccess);
            Assert.Equal("mod.invalid_file", toggle.Error?.Code);
        }

        Assert.True(File.Exists(outside));
        Assert.Equal("sentinel", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task DetectsDuplicateModIds()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var modsDirectory = GetModsDirectory(environment, instance);

        WriteJar(Path.Combine(modsDirectory, "one.jar"), """{ "id": "duplicate", "name": "One", "version": "1" }""");
        WriteJar(Path.Combine(modsDirectory, "two.jar"), """{ "id": "duplicate", "name": "Two", "version": "2" }""");

        var report = await provider.GetRequiredService<IModService>().ScanAsync(instance.Id);
        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal(2, report.Value!.Issues.Count(issue => issue.Kind == ModIssueKind.DuplicateModId));
    }

    private static async Task<GameInstance> CreateInstanceAsync(IServiceProvider provider)
    {
        var result = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Fabric Test", "1.21.1", "fabric", "0.16.0");
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static string GetModsDirectory(TestEnvironment environment, GameInstance instance)
    {
        var directory = Path.Combine(environment.Paths.GetInstanceGameDirectory(instance.Id), "mods");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteJar(string path, string fabricMetadata)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "fabric.mod.json", fabricMetadata);
    }

    private static void WriteForgeJar(string path, string modId)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "META-INF/mods.toml", $"""
            [[mods]]
            modId="{modId}"
            version="1.0"
            displayName="{modId}"
            """);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }
}
