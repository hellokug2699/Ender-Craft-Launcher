// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using Ecl.Diagnostics;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
using Ecl.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Diagnostics;

/// <summary>
/// F-10 S9：Mod 关系事实源只读取实例 mods 目录的本地静态元数据，
/// 缺失/停用依赖只到“高度疑似”，且扫描不创建任何目录或文件。
/// </summary>
public sealed class ModRelationshipFactSourceTests
{
    [Fact]
    public async Task CollectsInstalledModsAndDependencyFactsFromLocalMetadata()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("Mod Facts", "1.21.1", "fabric", "0.16.0")).Value!;
        var modsDirectory = Path.Combine(environment.Paths.GetInstanceGameDirectory(instance.Id), "mods");
        Directory.CreateDirectory(modsDirectory);
        WriteFabricMod(
            Path.Combine(modsDirectory, "alpha-1.0.0.jar"),
            """
            {"id":"alpha","name":"Alpha","version":"1.0.0","depends":{"fabricloader":">=0.15","minecraft":"1.21.1","missing-lib":"*"}}
            """);
        WriteFabricMod(
            Path.Combine(modsDirectory, "beta-2.0.0.jar"),
            """
            {"id":"beta","name":"Beta","version":"2.0.0","depends":{"fabricloader":">=0.15"}}
            """);

        var source = new ModRelationshipFactSource(provider.GetRequiredService<IModService>());
        var facts = await source.CollectAsync(
            CreateContext(instance, Path.Combine(environment.Root, "session")),
            CancellationToken.None);

        var installed = facts
            .Where(fact => fact.Kind == DiagnosticFactKinds.ModInstalled)
            .Select(fact => fact.Subject)
            .ToArray();
        Assert.Contains("alpha", installed);
        Assert.Contains("beta", installed);

        var missing = Assert.Single(
            facts,
            fact => fact.Kind == DiagnosticFactKinds.ModMissingDependency);
        Assert.Equal("alpha", missing.Subject);
        Assert.Equal("missing-lib", missing.Related);
        // 静态元数据不能证明运行时一定缺少依赖（可能由嵌套模块/条件依赖提供）。
        Assert.Equal(DiagnosticConfidence.HighlyLikely, missing.Confidence);
        Assert.Equal(DiagnosticFactConfidence.ModInventory, missing.SourceId);
    }

    [Fact]
    public async Task DuplicateAndUnreadableModsAreReportedAsFacts()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("Mod Duplicates", "1.21.1", "fabric", "0.16.0")).Value!;
        var modsDirectory = Path.Combine(environment.Paths.GetInstanceGameDirectory(instance.Id), "mods");
        Directory.CreateDirectory(modsDirectory);
        WriteFabricMod(
            Path.Combine(modsDirectory, "dup-old.jar"),
            """{"id":"dup","name":"Dup","version":"1.0.0"}""");
        WriteFabricMod(
            Path.Combine(modsDirectory, "dup-new.jar"),
            """{"id":"dup","name":"Dup","version":"2.0.0"}""");
        await File.WriteAllTextAsync(Path.Combine(modsDirectory, "broken.jar"), "not a zip");

        var source = new ModRelationshipFactSource(provider.GetRequiredService<IModService>());
        var facts = await source.CollectAsync(
            CreateContext(instance, Path.Combine(environment.Root, "session")),
            CancellationToken.None);

        Assert.Contains(facts, fact => fact.Kind == DiagnosticFactKinds.ModDuplicate && fact.Subject == "dup");
        Assert.Contains(facts, fact => fact.Kind == DiagnosticFactKinds.ModInvalidMetadata);
    }

    [Fact]
    public async Task ScanDoesNotCreateInstanceDirectoriesWhenModsAreMissing()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("No Mods", "1.21.1", "fabric", "0.16.0")).Value!;
        var modsDirectory = Path.Combine(environment.Paths.GetInstanceGameDirectory(instance.Id), "mods");
        // 实例创建流程可能已经创建 mods 目录；关键是“扫描”不会重新创建它。
        if (Directory.Exists(modsDirectory))
        {
            Directory.Delete(modsDirectory, recursive: true);
        }

        var source = new ModRelationshipFactSource(provider.GetRequiredService<IModService>());
        var facts = await source.CollectAsync(
            CreateContext(instance, Path.Combine(environment.Root, "session")),
            CancellationToken.None);

        Assert.Empty(facts);
        Assert.False(Directory.Exists(modsDirectory));
    }

    private static DiagnosticFactContext CreateContext(GameInstance instance, string sessionDirectory) =>
        new(
            instance.Id,
            instance,
            new GameLogSession(
                new SessionManifest { SessionId = "session-1", InstanceId = instance.Id },
                sessionDirectory),
            []);

    private static void WriteFabricMod(string path, string metadataJson)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("fabric.mod.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(metadataJson);
    }
}
