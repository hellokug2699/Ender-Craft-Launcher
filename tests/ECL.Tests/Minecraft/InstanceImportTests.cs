// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 其它启动器留下的 versions/&lt;id&gt; 安装应能被扫描、导入并直接启动，
/// 且游戏目录按“是否隔离”落到正确位置，不复制文件。
/// </summary>
public sealed class InstanceImportTests
{
    [Fact]
    public async Task ScanDetectsIsolatedAndSharedVersions()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var paths = environment.Paths;

        CreateVersion(paths.VersionsDirectory, "Iso", "net.fabricmc.loader.impl.launch.knot.KnotClient", isolated: true);
        CreateVersion(paths.VersionsDirectory, "Shared", "net.minecraft.client.main.Main", isolated: false);
        // 没有同名 JSON 的目录（例如 assets/libraries）必须被忽略。
        Directory.CreateDirectory(Path.Combine(paths.VersionsDirectory, "assets"));

        var detected = await instances.ScanVersionsAsync();

        Assert.Equal(2, detected.Count);
        var isolated = Assert.Single(detected, item => item.VersionId == "Iso");
        Assert.True(isolated.Isolated);
        Assert.Equal("fabric", isolated.Loader);
        Assert.Equal(Path.Combine(paths.VersionsDirectory, "Iso"), isolated.GameDirectory);
        Assert.False(isolated.AlreadyImported);

        var shared = Assert.Single(detected, item => item.VersionId == "Shared");
        Assert.False(shared.Isolated);
        Assert.Equal("vanilla", shared.Loader);
        Assert.Equal(paths.GameRoot, shared.GameDirectory);
    }

    [Fact]
    public async Task ImportCreatesInstanceThatReferencesExistingDirectory()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        CreateVersion(environment.Paths.VersionsDirectory, "Iso", "net.fabricmc.loader.impl.launch.knot.KnotClient", isolated: true);

        var imported = await instances.ImportAsync("Iso");
        Assert.True(imported.IsSuccess, imported.Error?.ToString());

        var instance = imported.Value!;
        Assert.Equal("Iso", instance.VersionId);
        Assert.Equal("fabric", instance.Loader);
        Assert.True(instance.VersionIsolation);
        Assert.Equal(Path.Combine(environment.Paths.VersionsDirectory, "Iso"), instance.GameDirectory);

        // 导入不创建 ECL 自己的游戏目录，避免复制或污染用户的安装。
        Assert.False(Directory.Exists(environment.Paths.GetInstanceGameDirectory(instance.Id)));

        var again = await instances.ImportAsync("Iso");
        Assert.False(again.IsSuccess);
        Assert.Equal("instance.already_imported", again.Error?.Code);

        var missing = await instances.ImportAsync("DoesNotExist");
        Assert.False(missing.IsSuccess);
        Assert.Equal("instance.version_not_found", missing.Error?.Code);
    }

    [Theory]
    [InlineData("Iso", true)]
    [InlineData("Shared", false)]
    public async Task ImportedInstanceLaunchesInItsExistingGameDirectory(string versionId, bool isolated)
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var accounts = provider.GetRequiredService<Ecl.Auth.IAccountService>();
        var paths = environment.Paths;

        var mainClass = isolated
            ? "net.fabricmc.loader.impl.launch.knot.KnotClient"
            : "net.minecraft.client.main.Main";
        CreateVersion(paths.VersionsDirectory, versionId, mainClass, isolated);

        Assert.True((await accounts.AddOfflineAsync("Steve", makeDefault: true)).IsSuccess);
        var imported = await instances.ImportAsync(versionId);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());

        var report = await provider.GetRequiredService<ILaunchService>()
            .LaunchAsync(imported.Value!.Id, dryRun: true);

        Assert.True(report.Success, string.Join(" | ", report.Steps.Select(step => $"{step.StepId}:{step.Status}:{step.Message}")));
        var expected = isolated
            ? Path.Combine(paths.VersionsDirectory, versionId)
            : paths.GameRoot;
        var actualGameDirectory = System.Text.RegularExpressions.Regex
            .Match(report.CommandLine!, "--gameDir\\s+\"?([^\"\\s]+)\"?")
            .Groups[1].Value.Trim();
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actualGameDirectory));

        // logging 资源不存在时不能把不存在的路径传给 log4j。
        Assert.DoesNotContain("-Dlog4j.configurationFile", report.CommandLine!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoggingArgumentIsAddedOnlyWhenTheConfigExists()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var accounts = provider.GetRequiredService<Ecl.Auth.IAccountService>();
        var paths = environment.Paths;

        Assert.True((await accounts.AddOfflineAsync("Steve", makeDefault: true)).IsSuccess);
        CreateVersion(paths.VersionsDirectory, "Shared", "net.minecraft.client.main.Main", isolated: false);
        var imported = await instances.ImportAsync("Shared");
        Assert.True(imported.IsSuccess);

        var loggingDirectory = Path.Combine(paths.AssetsDirectory, "logging");
        Directory.CreateDirectory(loggingDirectory);
        File.WriteAllText(Path.Combine(loggingDirectory, "client-1.12.xml"), "<Configuration/>");

        var report = await provider.GetRequiredService<ILaunchService>()
            .LaunchAsync(imported.Value!.Id, dryRun: true);

        Assert.True(report.Success, string.Join(" | ", report.Steps.Select(step => $"{step.StepId}:{step.Status}:{step.Message}")));
        Assert.Contains("-Dlog4j.configurationFile=", report.CommandLine!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportedInstanceDowngradesFileMismatchToWarning()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var accounts = provider.GetRequiredService<Ecl.Auth.IAccountService>();
        CreateVersion(environment.Paths.VersionsDirectory, "Shared", "net.minecraft.client.main.Main", isolated: false);
        Assert.True((await accounts.AddOfflineAsync("Steve", makeDefault: true)).IsSuccess);

        var imported = await instances.ImportAsync("Shared");
        Assert.True(imported.IsSuccess);

        // 版本 JSON 引用了不存在的客户端 jar；导入的实例不应因此阻塞启动。
        var report = await provider.GetRequiredService<ILaunchService>()
            .LaunchAsync(imported.Value!.Id, dryRun: false);

        var fileStep = Assert.Single(report.Steps, step => step.StepId == "files");
        Assert.Equal(LaunchStepStatus.Warning, fileStep.Status);
    }

    private static void CreateVersion(string versionsDirectory, string versionId, string mainClass, bool isolated)
    {
        var directory = Path.Combine(versionsDirectory, versionId);
        Directory.CreateDirectory(directory);
        var profile = new
        {
            id = versionId,
            mainClass,
            logging = new
            {
                client = new
                {
                    argument = "-Dlog4j.configurationFile=${path}",
                    file = new { id = "client-1.12.xml", sha1 = "00", size = 1 },
                    type = "log4j2-xml",
                },
            },
            libraries = mainClass.Contains("fabric", StringComparison.OrdinalIgnoreCase)
                ?
                [
                    new { name = "net.fabricmc:fabric-loader:0.16.0" },
                ]
                : Array.Empty<object>(),
        };
        File.WriteAllText(Path.Combine(directory, $"{versionId}.json"), JsonSerializer.Serialize(profile));

        if (isolated)
        {
            Directory.CreateDirectory(Path.Combine(directory, "mods"));
            File.WriteAllText(Path.Combine(directory, "options.txt"), "lang:zh_cn");
        }
    }
}
