// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using Ecl.Auth;
using Ecl.Configuration.Settings;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Eula;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Mods;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// Roadmap 1.1：导入实例回归。
/// 覆盖扫描有效性、导入的只读性与重复处理、共享/隔离目录的启动、缺失文件诊断，
/// 以及 Mod、资源包、光影包、崩溃报告与诊断包是否指向实际游戏目录。
/// </summary>
public sealed class ImportRegressionTests
{
    [Fact]
    public async Task ScanListsOnlyDirectoriesWithACompleteVersionManifest()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        ImportInstallationFixture.WriteSharedVanilla(environment);
        ImportInstallationFixture.WriteDirectoryWithoutManifest(environment, "assets");
        ImportInstallationFixture.WriteDirectoryWithoutManifest(environment, "unrelated");
        ImportInstallationFixture.WriteCorruptVersion(environment, "Broken");
        ImportInstallationFixture.WriteIncompleteVersion(environment, "Hollow");
        ImportInstallationFixture.WriteOrphanVersion(environment, "Orphan", "GhostParent");

        var detected = await instances.ScanVersionsAsync();

        Assert.Equal(
            ["1.21.1", "Broken", "Hollow", "Orphan"],
            detected.Select(version => version.VersionId).ToArray());
        Assert.Equal(DetectedVersionProblem.None, Find(detected, "1.21.1").Problem);
        Assert.True(Find(detected, "1.21.1").CanImport);

        AssertProblem(detected, "Broken", DetectedVersionProblem.InvalidManifest, "无法解析");
        AssertProblem(detected, "Hollow", DetectedVersionProblem.InvalidManifest, "mainClass");
        AssertProblem(detected, "Orphan", DetectedVersionProblem.IncompleteInheritance, "GhostParent");

        // 扫描只读：还没有导入任何实例，也没有创建 ECL 自建游戏目录。
        Assert.Empty(await instances.GetInstancesAsync());
        Assert.False(Directory.Exists(environment.Paths.GetInstanceGameDirectory("1.21.1")));

        // 重复扫描返回同一组结果，不会凭空产生实例。
        var rescanned = await instances.ScanVersionsAsync();
        Assert.Equal(
            detected.Select(version => version.VersionId),
            rescanned.Select(version => version.VersionId));

        var imported = await instances.ImportAsync("1.21.1");
        Assert.True(imported.IsSuccess, imported.Error?.ToString());

        var afterImport = await instances.ScanVersionsAsync();
        var importedVersion = Find(afterImport, "1.21.1");
        Assert.True(importedVersion.AlreadyImported);
        Assert.False(importedVersion.CanImport);
        Assert.Single(await instances.GetInstancesAsync());
    }

    [Fact]
    public async Task ImportRejectsCorruptAndIncompleteVersionsWithDiagnosticCodes()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();

        ImportInstallationFixture.WriteCorruptVersion(environment, "Broken");
        ImportInstallationFixture.WriteOrphanVersion(environment, "Orphan", "GhostParent");

        var corrupt = await instances.ImportAsync("Broken");
        Assert.False(corrupt.IsSuccess);
        Assert.Equal("instance.version_invalid", corrupt.Error?.Code);
        Assert.Contains("Broken", corrupt.Error?.Message, StringComparison.Ordinal);

        var orphan = await instances.ImportAsync("Orphan");
        Assert.False(orphan.IsSuccess);
        Assert.Equal("instance.version_incomplete", orphan.Error?.Code);
        Assert.Contains("GhostParent", orphan.Error?.Message, StringComparison.Ordinal);

        Assert.Empty(await instances.GetInstancesAsync());
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("isolated")]
    public async Task ImportAndRepeatedImportLeaveTheOriginalInstallationUntouched(string layout)
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var paths = environment.Paths;

        var versionId = string.Equals(layout, "shared", StringComparison.Ordinal)
            ? ImportInstallationFixture.WriteSharedVanilla(environment)
            : ImportInstallationFixture.WriteIsolatedFabric(environment);

        var gameDirectory = string.Equals(layout, "shared", StringComparison.Ordinal)
            ? paths.GameRoot
            : Path.Combine(paths.VersionsDirectory, versionId);

        var before = GameSnapshot(gameDirectory, layout);
        Assert.NotEmpty(before.Files);

        var imported = await instances.ImportAsync(versionId);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());
        Assert.Equal(
            Path.GetFullPath(gameDirectory),
            Path.GetFullPath(imported.Value!.GameDirectory!));

        var duplicate = await instances.ImportAsync(versionId);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal("instance.already_imported", duplicate.Error?.Code);

        var after = GameSnapshot(gameDirectory, layout);
        Assert.Empty(after.Diff(before));

        // 导入只建立引用：不会在 ECL 自建目录里创建第二份游戏目录。
        Assert.False(Directory.Exists(paths.GetInstanceGameDirectory(imported.Value.Id)));
    }

    [Fact]
    public async Task SharedVanillaImportLaunchesFromTheExistingGameRoot()
    {
        using var environment = new TestEnvironment();
        var launcher = new RecordingProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(launcher));
        var paths = environment.Paths;

        var versionId = ImportInstallationFixture.WriteSharedVanilla(environment);
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Steve", makeDefault: true);

        var imported = await provider.GetRequiredService<IInstanceService>().ImportAsync(versionId);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());

        var dryRun = await provider.GetRequiredService<ILaunchService>().LaunchAsync(imported.Value!.Id, dryRun: true);
        Assert.True(dryRun.Success, Describe(dryRun));
        Assert.Equal(LaunchStepStatus.Success, Step(dryRun, "version").Status);
        Assert.Equal(LaunchStepStatus.Success, Step(dryRun, "files").Status);
        Assert.Contains(
            Path.Combine(paths.VersionsDirectory, versionId, $"{versionId}.jar"),
            dryRun.CommandLine,
            StringComparison.Ordinal);
        Assert.Contains("-Dlog4j.configurationFile=", dryRun.CommandLine, StringComparison.Ordinal);

        ConfigureFakeJavaAndEula(environment, provider);
        var real = await provider.GetRequiredService<ILaunchService>().LaunchAsync(imported.Value.Id, dryRun: false);

        Assert.True(real.Success, Describe(real));
        var call = Assert.Single(launcher.Calls);
        Assert.Equal(Path.GetFullPath(paths.GameRoot), Path.GetFullPath(call.WorkingDirectory));
        Assert.Equal(Path.GetFullPath(paths.GameRoot), Path.GetFullPath(ArgumentValue(call.Command.Arguments, "--gameDir")));
        Assert.Equal(versionId, ArgumentValue(call.Command.Arguments, "--version"));
    }

    [Fact]
    public async Task IsolatedFabricImportUsesTheInheritedClientJarAndVersionDirectory()
    {
        using var environment = new TestEnvironment();
        var launcher = new RecordingProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(launcher));
        var paths = environment.Paths;

        var versionId = ImportInstallationFixture.WriteIsolatedFabric(environment);
        var versionDirectory = Path.Combine(paths.VersionsDirectory, versionId);
        var inheritedJar = Path.Combine(
            paths.VersionsDirectory,
            ImportInstallationFixture.VanillaVersion,
            $"{ImportInstallationFixture.VanillaVersion}.jar");
        var profileJar = Path.Combine(versionDirectory, $"{versionId}.jar");

        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Steve", makeDefault: true);
        var imported = await provider.GetRequiredService<IInstanceService>().ImportAsync(versionId);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());
        Assert.Equal("fabric", imported.Value!.Loader);
        Assert.Equal(ImportInstallationFixture.VanillaVersion, imported.Value.GameVersion);

        var dryRun = await provider.GetRequiredService<ILaunchService>().LaunchAsync(imported.Value.Id, dryRun: true);
        Assert.True(dryRun.Success, Describe(dryRun));
        Assert.Equal(LaunchStepStatus.Success, Step(dryRun, "files").Status);
        Assert.Contains(inheritedJar, dryRun.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain(profileJar, dryRun.CommandLine, StringComparison.Ordinal);

        ConfigureFakeJavaAndEula(environment, provider);
        var real = await provider.GetRequiredService<ILaunchService>().LaunchAsync(imported.Value.Id, dryRun: false);

        Assert.True(real.Success, Describe(real));
        var call = Assert.Single(launcher.Calls);
        Assert.Equal(Path.GetFullPath(versionDirectory), Path.GetFullPath(call.WorkingDirectory));

        var classpath = ArgumentValue(call.Command.Arguments, "-cp");
        Assert.Contains(inheritedJar, classpath, StringComparison.Ordinal);
        Assert.DoesNotContain(profileJar, classpath, StringComparison.Ordinal);
        Assert.Contains(versionDirectory, ArgumentValue(call.Command.Arguments, "--gameDir"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsolatedForgeImportWithMissingOptionalFilesWarnsAndStillLaunches()
    {
        using var environment = new TestEnvironment();
        var launcher = new RecordingProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(launcher));
        var paths = environment.Paths;

        var versionId = ImportInstallationFixture.WriteIsolatedForge(
            environment,
            new InstallFileOptions { Libraries = false, AssetObjects = false, LoggingConfig = false });
        var versionDirectory = Path.Combine(paths.VersionsDirectory, versionId);

        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Steve", makeDefault: true);
        var imported = await provider.GetRequiredService<IInstanceService>().ImportAsync(versionId);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());

        ConfigureFakeJavaAndEula(environment, provider);
        var real = await provider.GetRequiredService<ILaunchService>().LaunchAsync(imported.Value!.Id, dryRun: false);

        // 导入的实例由其它启动器维护：文件与清单不一致时给出警告，但不阻止启动。
        Assert.True(real.Success, Describe(real));
        var files = Step(real, "files");
        Assert.Equal(LaunchStepStatus.Warning, files.Status);
        Assert.Contains(paths.LibrariesDirectory, files.Message, StringComparison.OrdinalIgnoreCase);

        var call = Assert.Single(launcher.Calls);
        Assert.Equal(Path.GetFullPath(versionDirectory), Path.GetFullPath(call.WorkingDirectory));
        Assert.DoesNotContain(
            call.Command.Arguments,
            argument => argument.StartsWith("-Dlog4j.configurationFile=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportedInstanceWithMissingParentFailsWithAPathDiagnostic()
    {
        using var environment = new TestEnvironment();
        var launcher = new RecordingProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(launcher));
        var paths = environment.Paths;

        var versionId = ImportInstallationFixture.WriteIsolatedFabric(environment);
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Steve", makeDefault: true);
        var imported = await provider.GetRequiredService<IInstanceService>().ImportAsync(versionId);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());

        // 模拟外部启动器删除了被继承的原版版本。
        var parentDirectory = Path.Combine(paths.VersionsDirectory, ImportInstallationFixture.VanillaVersion);
        var parentJson = Path.Combine(parentDirectory, $"{ImportInstallationFixture.VanillaVersion}.json");
        File.Delete(parentJson);

        ConfigureFakeJavaAndEula(environment, provider);
        var real = await provider.GetRequiredService<ILaunchService>().LaunchAsync(imported.Value!.Id, dryRun: false);

        Assert.False(real.Success);
        Assert.Empty(launcher.Calls);
        var version = Step(real, "version");
        Assert.Equal(LaunchStepStatus.Failed, version.Status);
        Assert.Contains(ImportInstallationFixture.VanillaVersion, version.Message, StringComparison.Ordinal);
        Assert.Contains(parentJson, version.Message, StringComparison.OrdinalIgnoreCase);

        // 启动仅在读取版本：不能替用户重建已被删除的父版本。
        Assert.False(File.Exists(parentJson));
    }

    [Fact]
    public async Task ImportedInstanceWithRemovedVersionDirectoryFailsWithoutRecreatingIt()
    {
        using var environment = new TestEnvironment();
        var launcher = new RecordingProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(launcher));
        var paths = environment.Paths;

        var versionId = ImportInstallationFixture.WriteIsolatedFabric(environment);
        var versionDirectory = Path.Combine(paths.VersionsDirectory, versionId);
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Steve", makeDefault: true);
        var imported = await provider.GetRequiredService<IInstanceService>().ImportAsync(versionId);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());

        Directory.Delete(versionDirectory, recursive: true);

        ConfigureFakeJavaAndEula(environment, provider);
        var real = await provider.GetRequiredService<ILaunchService>().LaunchAsync(imported.Value!.Id, dryRun: false);

        Assert.False(real.Success);
        Assert.Empty(launcher.Calls);
        Assert.Equal(LaunchStepStatus.Failed, Step(real, "version").Status);
        Assert.False(Directory.Exists(versionDirectory));
    }

    [Fact]
    public async Task ImportedInstanceModsUseTheRealGameDirectory()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var paths = environment.Paths;

        var versionId = ImportInstallationFixture.WriteIsolatedFabric(environment);
        var versionDirectory = Path.Combine(paths.VersionsDirectory, versionId);
        var imported = await provider.GetRequiredService<IInstanceService>().ImportAsync(versionId);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());
        var instance = imported.Value!;

        var mods = provider.GetRequiredService<IModService>();
        var scan = await mods.ScanAsync(instance.Id);
        Assert.True(scan.IsSuccess, scan.Error?.ToString());
        Assert.Equal(Path.Combine(versionDirectory, "mods"), scan.Value!.ModsDirectory);

        var mod = Assert.Single(scan.Value.Mods, entry => entry.FileName == "examplemod-1.0.jar");
        Assert.Equal(ModPlatform.Fabric, mod.Metadata!.Platform);

        Assert.True((await mods.SetEnabledAsync(instance.Id, "examplemod-1.0.jar", false)).IsSuccess);
        Assert.True(File.Exists(Path.Combine(versionDirectory, "mods", "examplemod-1.0.jar.disabled")));
        Assert.False(File.Exists(Path.Combine(versionDirectory, "mods", "examplemod-1.0.jar")));

        var source = Path.Combine(environment.Root, "samples", "added-1.0.jar");
        ImportInstallationFixture.WriteFabricMod(source, "added", "Added Mod");
        var addResult = await mods.ImportAsync(instance.Id, [source]);
        Assert.True(addResult.IsSuccess);
        Assert.Equal(1, addResult.Value!.Succeeded);
        Assert.True(File.Exists(Path.Combine(versionDirectory, "mods", "added-1.0.jar")));

        Assert.True((await mods.DeleteAsync(instance.Id, "added-1.0.jar")).IsSuccess);
        Assert.False(File.Exists(Path.Combine(versionDirectory, "mods", "added-1.0.jar")));

        // 所有内容操作都发生在导入目录内，ECL 自建游戏目录始终不存在。
        Assert.False(Directory.Exists(paths.GetInstanceGameDirectory(instance.Id)));
    }

    [Fact]
    public async Task ImportedInstanceContentPacksUseTheRealGameDirectory()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var paths = environment.Paths;

        var versionId = ImportInstallationFixture.WriteIsolatedFabric(environment);
        var versionDirectory = Path.Combine(paths.VersionsDirectory, versionId);
        var existingPack = ImportInstallationFixture.WriteResourcePack(environment, "existing.zip", "Existing Pack");
        var packDirectory = Path.Combine(versionDirectory, "resourcepacks");
        Directory.CreateDirectory(packDirectory);
        File.Copy(existingPack, Path.Combine(packDirectory, "existing.zip"));
        File.WriteAllText(
            Path.Combine(versionDirectory, "options.txt"),
            "resourcePacks:[\"vanilla\",\"file/existing.zip\"]\n");

        var imported = await provider.GetRequiredService<IInstanceService>().ImportAsync(versionId);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());
        var instance = imported.Value!;

        var packs = provider.GetRequiredService<IContentPackService>();
        var listed = await packs.ListAsync(instance.Id, ContentPackKind.ResourcePack);
        Assert.True(listed.IsSuccess, listed.Error?.ToString());
        var existing = Assert.Single(listed.Value!.Packs);
        Assert.Equal(Path.Combine(packDirectory, "existing.zip"), existing.FullPath);
        Assert.True(existing.Enabled);

        var source = ImportInstallationFixture.WriteResourcePack(environment, "new-pack.zip", "New Pack");
        var added = await packs.ImportAsync(instance.Id, ContentPackKind.ResourcePack, source);
        Assert.True(added.IsSuccess, added.Error?.ToString());
        Assert.Equal(
            Path.GetFullPath(Path.Combine(packDirectory, "new-pack.zip")),
            Path.GetFullPath(added.Value!.FullPath));

        Assert.True((await packs.SetEnabledAsync(
            instance.Id,
            ContentPackKind.ResourcePack,
            "new-pack.zip",
            enabled: true)).IsSuccess);
        var options = File.ReadAllText(Path.Combine(versionDirectory, "options.txt"));
        Assert.Contains("file/existing.zip", options, StringComparison.Ordinal);
        Assert.Contains("file/new-pack.zip", options, StringComparison.Ordinal);

        var shaderSource = ImportInstallationFixture.WriteShaderPack(environment, "new-shader.zip");
        var shader = await packs.ImportAsync(instance.Id, ContentPackKind.ShaderPack, shaderSource);
        Assert.True(shader.IsSuccess, shader.Error?.ToString());
        Assert.Equal(
            Path.GetFullPath(Path.Combine(versionDirectory, "shaderpacks", "new-shader.zip")),
            Path.GetFullPath(shader.Value!.FullPath));

        Assert.False(Directory.Exists(paths.GetInstanceGameDirectory(instance.Id)));
    }

    [Fact]
    public async Task ImportedInstanceCrashReportsAndDiagnosticsComeFromTheRealGameDirectory()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var paths = environment.Paths;

        var versionId = ImportInstallationFixture.WriteIsolatedFabric(environment);
        var versionDirectory = Path.Combine(paths.VersionsDirectory, versionId);
        ImportInstallationFixture.WriteCrashReport(versionDirectory);

        var imported = await provider.GetRequiredService<IInstanceService>().ImportAsync(versionId);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());
        var instance = imported.Value!;

        var crash = await provider.GetRequiredService<ICrashReportService>().AnalyzeLatestAsync(instance.Id);
        Assert.True(crash.IsSuccess, crash.Error?.ToString());
        Assert.NotNull(crash.Value);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(versionDirectory, "crash-reports", "crash-2026-01-02_03.04.05-client.txt")),
            Path.GetFullPath(crash.Value!.SourcePath!));
        Assert.Equal("Rendering overlay", crash.Value.Description);

        var output = Path.Combine(environment.Root, "diagnostics.zip");
        var package = await provider.GetRequiredService<IDiagnosticPackageService>().ExportAsync(instance.Id, output);
        Assert.True(package.IsSuccess, package.Error?.ToString());

        using var archive = ZipFile.OpenRead(output);
        var crashEntry = Assert.Single(
            archive.Entries,
            entry => entry.FullName == "crash-reports/crash-2026-01-02_03.04.05-client.txt");
        using (var reader = new StreamReader(crashEntry.Open()))
        {
            Assert.Contains("Rendering overlay", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }

        Assert.Contains(archive.Entries, entry => entry.FullName == "minecraft/logs/latest.log");
        Assert.False(Directory.Exists(paths.GetInstanceGameDirectory(instance.Id)));
    }

    [Fact]
    public async Task ScanResolvesTheMinecraftVersionThroughNestedLoaderProfiles()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var paths = environment.Paths;

        var fabricProfile = ImportInstallationFixture.WriteIsolatedFabric(environment);
        var packId = "MyPack";
        var packDirectory = Path.Combine(paths.VersionsDirectory, packId);
        Directory.CreateDirectory(packDirectory);
        File.WriteAllText(
            Path.Combine(packDirectory, $"{packId}.json"),
            $$"""
            {
              "id": "{{packId}}",
              "inheritsFrom": "{{fabricProfile}}",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient"
            }
            """);

        var detected = await provider.GetRequiredService<IInstanceService>().ScanVersionsAsync();
        var pack = Find(detected, packId);
        Assert.Equal(DetectedVersionProblem.None, pack.Problem);
        Assert.Equal(ImportInstallationFixture.VanillaVersion, pack.GameVersion);
        Assert.Equal("fabric", pack.Loader);

        var imported = await provider.GetRequiredService<IInstanceService>().ImportAsync(packId);
        Assert.True(imported.IsSuccess, imported.Error?.ToString());
        Assert.Equal(ImportInstallationFixture.VanillaVersion, imported.Value!.GameVersion);

        // 读取实例列表时的自我修复必须继续解析到原版版本，不能把加载器配置名写成 GameVersion。
        var listed = await provider.GetRequiredService<IInstanceService>().GetInstancesAsync();
        Assert.Equal(
            ImportInstallationFixture.VanillaVersion,
            Assert.Single(listed, instance => instance.Id == imported.Value.Id).GameVersion);
    }

    [Fact]
    public async Task ImportUsesTheRequestedDisplayName()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();

        var versionId = ImportInstallationFixture.WriteSharedVanilla(environment);
        var imported = await provider.GetRequiredService<IInstanceService>()
            .ImportAsync(versionId, name: "我的整合包");

        Assert.True(imported.IsSuccess, imported.Error?.ToString());
        Assert.Equal("我的整合包", imported.Value!.Name);
        Assert.Equal(versionId, imported.Value.VersionId);
    }

    private static DetectedVersion Find(IReadOnlyList<DetectedVersion> detected, string versionId) =>
        Assert.Single(detected, version => string.Equals(version.VersionId, versionId, StringComparison.Ordinal));

    private static void AssertProblem(
        IReadOnlyList<DetectedVersion> detected,
        string versionId,
        DetectedVersionProblem problem,
        string detailFragment)
    {
        var version = Find(detected, versionId);
        Assert.Equal(problem, version.Problem);
        Assert.False(version.CanImport);
        Assert.Contains(detailFragment, version.ProblemDetail, StringComparison.OrdinalIgnoreCase);
    }

    private static FileTreeSnapshot GameSnapshot(string root, string layout) =>
        string.Equals(layout, "shared", StringComparison.Ordinal)
            ? FileTreeSnapshot.Capture(root, GameDataFiles.IsGameData)
            : FileTreeSnapshot.Capture(root);

    private static LaunchStepResult Step(LaunchReport report, string stepId) =>
        Assert.Single(report.Steps, step => string.Equals(step.StepId, stepId, StringComparison.Ordinal));

    private static string Describe(LaunchReport report) =>
        string.Join(" | ", report.Steps.Select(step => $"{step.StepId}:{step.Status}:{step.Message}"));

    private static string ArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        throw new InvalidOperationException($"启动参数中未找到 {name}：{string.Join(' ', arguments)}");
    }

    private static void ConfigureFakeJavaAndEula(TestEnvironment environment, IServiceProvider provider)
    {
        var fakeJavaHome = Path.Combine(environment.Root, "fakejava");
        var fakeJavaBin = Path.Combine(fakeJavaHome, "bin");
        Directory.CreateDirectory(fakeJavaBin);
        var fakeJava = Path.Combine(fakeJavaBin, OperatingSystem.IsWindows() ? "java.exe" : "java");
        File.WriteAllText(fakeJava, string.Empty);

        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("java.defaultPath", fakeJava, null, out var error), error);
        Assert.True(provider.GetRequiredService<IEulaService>().Accept().IsSuccess);
    }

    private sealed class RecordingProcessLauncher : IProcessLauncher
    {
        public List<LaunchCall> Calls { get; } = [];

        public int Start(LaunchCommand command, string workingDirectory, bool showConsole, string instanceId)
        {
            Calls.Add(new LaunchCall(command, workingDirectory));
            return 4242;
        }

        public sealed record LaunchCall(LaunchCommand Command, string WorkingDirectory);
    }
}
