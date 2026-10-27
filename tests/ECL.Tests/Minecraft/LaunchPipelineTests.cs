// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;
using Ecl.Configuration.Settings;
using Ecl.Minecraft.Eula;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class LaunchPipelineTests
{
    [Fact]
    public async Task DryRunBuildsCommandWithoutStartingProcess()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(fake));

        var instance = await CreateInstanceAsync(provider);
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: true);

        Assert.True(report.Success);
        Assert.NotNull(report.CommandLine);
        Assert.Contains("-Xmx4096M", report.CommandLine);
        Assert.Contains("--username", report.CommandLine);
        Assert.Contains("--gameDir", report.CommandLine);
        Assert.Contains("--width", report.CommandLine);
        Assert.Equal(0, fake.Calls);
        Assert.Contains(report.Steps, step => step.StepId == "start" && step.Status == LaunchStepStatus.Skipped);
    }

    [Fact]
    public async Task InstanceSettingsOverrideGlobalLaunchSettings()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(fake));

        var instance = await CreateInstanceAsync(provider);
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");

        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("instance.java.memoryMb", "8192", instance.Id, out var memoryError), memoryError);
        Assert.True(settings.TrySetRaw("java.extraArguments", "-Dglobal=1", null, out var globalError), globalError);
        Assert.True(settings.TrySetRaw("instance.java.extraArguments", "-Dinstance=\"a b\"", instance.Id, out var jvmError), jvmError);

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: true);

        Assert.True(report.Success);
        Assert.NotNull(report.CommandLine);
        Assert.Contains("-Xmx8192M", report.CommandLine);
        Assert.Contains("-Dinstance=a b", report.CommandLine);
        Assert.DoesNotContain("-Dglobal=1", report.CommandLine);
    }

    [Fact]
    public async Task RealLaunchFailsBeforeStartWhenVersionIsMissing()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(fake));

        var instance = await CreateInstanceAsync(provider);
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: false);

        Assert.False(report.Success);
        Assert.Equal(0, fake.Calls);
        Assert.Contains(report.Steps, step => step.StepId == "version" && step.Status == LaunchStepStatus.Failed);
    }

    [Fact]
    public async Task RealLaunchStartsProcessWhenJavaAndVersionExist()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(fake));

        var instance = await CreateInstanceAsync(provider);
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");

        var fakeJavaHome = Path.Combine(environment.Root, "fakejava");
        var fakeJavaBin = Path.Combine(fakeJavaHome, "bin");
        Directory.CreateDirectory(fakeJavaBin);
        var fakeJava = Path.Combine(fakeJavaBin, OperatingSystem.IsWindows() ? "java.exe" : "java");
        await File.WriteAllTextAsync(fakeJava, string.Empty);

        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("java.defaultPath", fakeJava, null, out var javaError), javaError);
        Assert.True(provider.GetRequiredService<IEulaService>().Accept().IsSuccess);

        var versionDirectory = Path.Combine(environment.Paths.VersionsDirectory, instance.VersionId);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(versionDirectory, $"{instance.VersionId}.json"),
            """
            {
              "id": "1.21.1",
              "type": "release",
              "mainClass": "net.minecraft.client.main.Main",
              "assetIndex": { "id": "1.21" },
              "libraries": []
            }
            """);
        await File.WriteAllBytesAsync(
            Path.Combine(versionDirectory, $"{instance.VersionId}.jar"),
            Array.Empty<byte>());

        var assetIndexDirectory = Path.Combine(environment.Paths.AssetsDirectory, "indexes");
        Directory.CreateDirectory(assetIndexDirectory);
        await File.WriteAllTextAsync(Path.Combine(assetIndexDirectory, "1.21.json"), """{ "objects": {} }""");

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: false);

        Assert.True(report.Success);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(4242, report.ProcessId);
        var reloaded = await provider.GetRequiredService<IInstanceService>().GetAsync(instance.Id);
        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.LastPlayedAt);
    }

    [Fact]
    public void SplitsQuotedExtraArguments()
    {
        var arguments = CommandLineBuilder.SplitArguments("-Dfoo=\"a b\" 'c d' -Xmx2G");

        Assert.Equal(["-Dfoo=a b", "c d", "-Xmx2G"], arguments);
    }

    [Fact]
    public async Task FabricProfileJvmArgumentsAreAdded()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(fake));

        var created = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Fabric", "1.21.1", "fabric", "0.16.0");
        Assert.True(created.IsSuccess);
        var instance = created.Value!;
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");
        ConfigureFakeJava(environment, provider);

        WriteVersion(environment, "1.21.1", "1.21.1", "net.minecraft.client.main.Main");
        WriteVersion(
            environment,
            instance.VersionId,
            "1.21.1",
            "net.fabricmc.loader.impl.launch.knot.KnotClient",
            """
            "arguments": { "jvm": ["-DFabricMcEmu= net.minecraft.client.main.Main "] },
            """);

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: true);

        Assert.True(report.Success);
        Assert.Contains("FabricMcEmu", report.CommandLine);
    }

    [Fact]
    public async Task CleanroomTweakClassIsAddedFromLegacyArguments()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(fake));

        var created = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Cleanroom", "1.12.2", "cleanroom", "0.7.12-alpha");
        Assert.True(created.IsSuccess);
        var instance = created.Value!;
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");
        ConfigureFakeJava(environment, provider);

        WriteVersion(
            environment,
            instance.VersionId,
            instance.VersionId,
            "top.outlands.foundation.boot.Foundation",
            """
            "minecraftArguments": "--username ${auth_player_name} --gameDir ${game_directory} --tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker --versionType Cleanroom",
            """);

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: true);

        Assert.True(report.Success);
        Assert.Contains("--tweakClass", report.CommandLine);
        Assert.Contains("FMLTweaker", report.CommandLine);
        Assert.DoesNotContain("${auth_player_name}", report.CommandLine);
    }

    private static void ConfigureFakeJava(TestEnvironment environment, IServiceProvider provider)
    {
        var fakeJavaHome = Path.Combine(environment.Root, "fakejava");
        var fakeJavaBin = Path.Combine(fakeJavaHome, "bin");
        Directory.CreateDirectory(fakeJavaBin);
        var fakeJava = Path.Combine(fakeJavaBin, OperatingSystem.IsWindows() ? "java.exe" : "java");
        File.WriteAllText(fakeJava, string.Empty);
        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("java.defaultPath", fakeJava, null, out var error), error);
    }

    private static void WriteVersion(
        TestEnvironment environment,
        string versionId,
        string parent,
        string mainClass,
        string extra = "")
    {
        var directory = Path.Combine(environment.Paths.VersionsDirectory, versionId);
        Directory.CreateDirectory(directory);
        var inherits = string.Equals(versionId, parent, StringComparison.Ordinal)
            ? string.Empty
            : $"\"inheritsFrom\": \"{parent}\",";
        File.WriteAllText(
            Path.Combine(directory, $"{versionId}.json"),
            $$"""
            {
              "id": "{{versionId}}",
              {{inherits}}
              "type": "release",
              "mainClass": "{{mainClass}}",
              {{extra}}
              "libraries": []
            }
            """);
    }

    private static async Task<GameInstance> CreateInstanceAsync(IServiceProvider provider)
    {
        var created = await provider.GetRequiredService<IInstanceService>().CreateAsync("Test", "1.21.1");
        Assert.True(created.IsSuccess);
        return created.Value!;
    }

    private sealed class FakeProcessLauncher : IProcessLauncher
    {
        public int Calls { get; private set; }

        public int Start(LaunchCommand command, string workingDirectory, bool showConsole, string instanceId)
        {
            Calls++;
            Assert.NotEmpty(command.Arguments);
            Assert.True(Directory.Exists(workingDirectory));
            return 4242;
        }
    }
}
