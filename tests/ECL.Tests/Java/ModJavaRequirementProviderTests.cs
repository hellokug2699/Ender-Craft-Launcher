// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class ModJavaRequirementProviderTests
{
    [Fact]
    public async Task UsesEnabledModClassFileLowerBound()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var mods = ModsDirectory(environment, instance);
        Directory.CreateDirectory(mods);

        CreateJar(Path.Combine(mods, "java21-mod.jar"), 65);
        CreateJar(Path.Combine(mods, "java8-mod.jar"), 52);

        var sources = await Provider(environment)
            .GetRequirementsAsync(instance);

        var source = Assert.Single(sources);
        Assert.Equal(JavaRequirementKind.Mod, source.Kind);
        Assert.Equal(21, source.MinimumMajor);
        Assert.Equal(JavaRequirementEvidence.ModClassFileLowerBound, source.Evidence);
        Assert.Contains("java21-mod.jar", source.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoresDisabledAndMultiReleaseClasses()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var mods = ModsDirectory(environment, instance);
        Directory.CreateDirectory(mods);

        CreateJar(Path.Combine(mods, "disabled.jar.disabled"), 65);
        CreateJar(
            Path.Combine(mods, "multi-release.jar"),
            52,
            ("META-INF/versions/21/example/Modern.class", 65));

        var sources = await Provider(environment).GetRequirementsAsync(instance);

        Assert.Empty(sources);
    }

    [Fact]
    public async Task OneCorruptJarDoesNotAbortOtherCandidates()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var mods = ModsDirectory(environment, instance);
        Directory.CreateDirectory(mods);

        await File.WriteAllTextAsync(Path.Combine(mods, "broken.jar"), "not a zip");
        CreateJar(Path.Combine(mods, "works.jar"), 61);

        var sources = await Provider(environment).GetRequirementsAsync(instance);

        var source = Assert.Single(sources);
        Assert.Equal(17, source.MinimumMajor);
        Assert.Contains("works.jar", source.Id, StringComparison.Ordinal);
    }

    private static ModJavaRequirementProvider Provider(TestEnvironment environment) =>
        new(environment.Paths, NullLogger<ModJavaRequirementProvider>.Instance);

    private static async Task<GameInstance> CreateInstanceAsync(IServiceProvider provider)
    {
        var created = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Mod Java Test", "1.20.1");
        Assert.True(created.IsSuccess);
        return created.Value!;
    }

    private static string ModsDirectory(TestEnvironment environment, GameInstance instance) =>
        Path.Combine(environment.Paths.GetInstanceGameDirectory(instance.Id), "mods");

    private static void CreateJar(
        string path,
        int classMajor,
        params (string Name, int Major)[] extraEntries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddClass(archive, "example/Example.class", classMajor);
        foreach (var (name, major) in extraEntries)
        {
            AddClass(archive, name, major);
        }
    }

    private static void AddClass(ZipArchive archive, string name, int classMajor)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0xCAFEBABE);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6, 2), (ushort)classMajor);
        stream.Write(bytes);
    }
}
