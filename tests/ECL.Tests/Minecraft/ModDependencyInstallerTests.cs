// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Modrinth;
using Ecl.Minecraft.Mods;
using Ecl.Operations;
using Ecl.Tests.CurseForge;
using Ecl.Tests.Modrinth;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class ModDependencyInstallerTests
{
    [Fact]
    public async Task InstallsMissingDependencyFromModrinth()
    {
        using var environment = new TestEnvironment();
        var mods = new FakeModService(Report("demo.jar", "fabric-api"));
        var install = new RecordingContentInstallService();
        var modrinth = new FakeModrinthClient();
        modrinth.Versions["fabric-api"] = [CreateModrinthVersion("fabric-api", "fabric-api-0.116.0.jar")];
        await using var provider = CreateProvider(environment, mods, install, modrinth);
        var instance = await CreateInstanceAsync(provider);

        var result = await provider.GetRequiredService<IModDependencyInstaller>()
            .InstallMissingAsync(instance.Id);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(["fabric-api"], result.Value!.Installed);
        Assert.Empty(result.Value.Unresolved);
        Assert.Empty(result.Value.Failed);

        var hit = Assert.Single(install.Hits);
        Assert.Equal(ContentSource.Modrinth, hit.Source);
        Assert.Equal("fabric-api", hit.Id);
        Assert.Equal(ContentKind.Mod, hit.Kind);

        var snapshot = Assert.Single(provider.GetRequiredService<IOperationStore>().Snapshots);
        Assert.Equal(OperationKind.Install, snapshot.Kind);
        Assert.Equal(OperationState.Completed, snapshot.State);
        Assert.Equal("mods.dependencies.done", snapshot.StageId);
    }

    [Fact]
    public async Task FallsBackToCurseForgeWhenModrinthCannotProvideTheMod()
    {
        using var environment = new TestEnvironment();
        var mods = new FakeModService(Report("demo.jar", "jei"));
        var install = new RecordingContentInstallService();
        var modrinth = new FakeModrinthClient();
        modrinth.MissingProjects.Add("jei");
        var curseForge = new FakeCurseForgeClient();
        curseForge.Mods[42] = new CurseForgeMod
        {
            Id = 42,
            Slug = "jei",
            Name = "Just Enough Items",
            Summary = "Recipe viewer",
        };
        await using var provider = CreateProvider(environment, mods, install, modrinth, curseForge);
        var instance = await CreateInstanceAsync(provider);

        var result = await provider.GetRequiredService<IModDependencyInstaller>()
            .InstallMissingAsync(instance.Id);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(["jei"], result.Value!.Installed);
        var hit = Assert.Single(install.Hits);
        Assert.Equal(ContentSource.CurseForge, hit.Source);
        Assert.Equal("42", hit.Id);
        Assert.Equal("Just Enough Items", hit.Name);
    }

    [Fact]
    public async Task ReportsUnresolvedDependencyWithoutInstallingAnything()
    {
        using var environment = new TestEnvironment();
        var mods = new FakeModService(Report("demo.jar", "unknown-mod-id"));
        var install = new RecordingContentInstallService();
        var modrinth = new FakeModrinthClient();
        modrinth.MissingProjects.Add("unknown-mod-id");
        await using var provider = CreateProvider(environment, mods, install, modrinth);
        var instance = await CreateInstanceAsync(provider);

        var result = await provider.GetRequiredService<IModDependencyInstaller>()
            .InstallMissingAsync(instance.Id);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Empty(result.Value!.Installed);
        Assert.Equal(["unknown-mod-id"], result.Value.Unresolved);
        Assert.Empty(install.Hits);
        Assert.NotEmpty(result.Value.Warnings);

        var snapshot = Assert.Single(provider.GetRequiredService<IOperationStore>().Snapshots);
        Assert.Equal(OperationState.Completed, snapshot.State);
    }

    [Fact]
    public async Task ReportsFailedInstallSeparatelyFromUnresolved()
    {
        using var environment = new TestEnvironment();
        var mods = new FakeModService(Report("demo.jar", "fabric-api"));
        var install = new RecordingContentInstallService { Succeed = false };
        var modrinth = new FakeModrinthClient();
        modrinth.Versions["fabric-api"] = [CreateModrinthVersion("fabric-api", "fabric-api.jar")];
        await using var provider = CreateProvider(environment, mods, install, modrinth);
        var instance = await CreateInstanceAsync(provider);

        var result = await provider.GetRequiredService<IModDependencyInstaller>()
            .InstallMissingAsync(instance.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(["fabric-api"], result.Value!.Failed);
        Assert.Empty(result.Value.Installed);
        Assert.Empty(result.Value.Unresolved);
    }

    [Fact]
    public async Task DoesNothingWithoutMissingDependencies()
    {
        using var environment = new TestEnvironment();
        var mods = new FakeModService(new ModScanReport("instance", "mods", [], []));
        var install = new RecordingContentInstallService();
        await using var provider = CreateProvider(environment, mods, install, new FakeModrinthClient());
        var instance = await CreateInstanceAsync(provider);

        var result = await provider.GetRequiredService<IModDependencyInstaller>()
            .InstallMissingAsync(instance.Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(install.Hits);
        Assert.Empty(result.Value!.Installed);
        Assert.Single(provider.GetRequiredService<IOperationStore>().Snapshots);
    }

    private static ServiceProvider CreateProvider(
        TestEnvironment environment,
        IModService mods,
        IContentInstallService install,
        IModrinthClient modrinth,
        ICurseForgeClient? curseForge = null) =>
        environment.CreateServices(services =>
        {
            services.AddSingleton(mods);
            services.AddSingleton(install);
            services.AddSingleton(modrinth);
            services.AddSingleton(curseForge ?? new FakeCurseForgeClient());
        });

    private static async Task<GameInstance> CreateInstanceAsync(IServiceProvider provider)
    {
        var result = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Test", "1.21.1", "fabric", "0.16.0");
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!;
    }

    private static ModScanReport Report(string fileName, string missingDependency) =>
        new(
            "instance",
            "mods",
            [],
            [new ModIssue(ModIssueKind.MissingDependency, fileName, "demo", missingDependency, ">=1.0")]);

    private static ModrinthVersion CreateModrinthVersion(string projectId, string fileName) => new()
    {
        Id = $"version-{projectId}",
        ProjectId = projectId,
        VersionNumber = "1.0.0",
        VersionType = "release",
        GameVersions = ["1.21.1"],
        Loaders = ["fabric"],
        DatePublished = DateTimeOffset.UtcNow,
        Files =
        [
            new ModrinthFile
            {
                Url = $"https://cdn.modrinth.com/data/{projectId}/versions/1/{fileName}",
                Filename = fileName,
                Primary = true,
                Size = 4,
            },
        ],
    };

    private sealed class FakeModService(ModScanReport report) : IModService
    {
        public Task<Result<ModScanReport>> ScanAsync(
            string instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ModScanReport>.Ok(report));

        public Task<Result> SetEnabledAsync(
            string instanceId,
            string fileName,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<ModBatchResult>> SetAllEnabledAsync(
            string instanceId,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<ModBatchResult>> ImportAsync(
            string instanceId,
            IReadOnlyList<string> sourcePaths,
            bool overwrite = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> DeleteAsync(
            string instanceId,
            string fileName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingContentInstallService : IContentInstallService
    {
        public List<ContentHit> Hits { get; } = [];

        public bool Succeed { get; init; } = true;

        public Task<Result<ContentInstallReport>> InstallAsync(
            string instanceId,
            ContentHit hit,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Hits.Add(hit);
            return Task.FromResult(Succeed
                ? Result<ContentInstallReport>.Ok(new ContentInstallReport(
                    hit.Name,
                    hit.Source,
                    hit.Kind,
                    instanceId,
                    $"{hit.Name}.jar",
                    [$"{hit.Name}.jar"],
                    []))
                : Result<ContentInstallReport>.Fail("content.install_failed", "模拟安装失败。"));
        }

        public Task<Result<ContentDownloadReport>> DownloadAsync(
            ContentHit hit,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<ModpackImportResult>> ImportModpackAsync(
            string instanceId,
            ContentHit hit,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<ModpackImportResult>> CreateInstanceAndImportModpackAsync(
            ContentHit hit,
            IProgress<ContentProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
