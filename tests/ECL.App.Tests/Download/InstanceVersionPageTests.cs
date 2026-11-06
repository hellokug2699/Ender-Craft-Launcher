// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Core.Storage;
using Ecl.Minecraft.Install;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;


namespace Ecl.App.Tests.Download;

/// <summary>
/// 下载中心「实例版本」页（download-instance-versions.md）：
/// 分类顺序、版本/类型选择、加载器与条件组件、同名校验、真实阶段映射、失败重试与重新布置。
/// </summary>
public sealed class InstanceVersionPageTests
{
    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public void DownloadCategoriesKeepTheApprovedOrder()
    {
        var categories = DownloadCategories.Create(Localizer());

        Assert.Equal(
            new[] { "version", "mod", "modpack", "resourcepack", "shader", "datapack" },
            categories.Select(category => category.Key));
        Assert.Null(categories[0].Kind);
        Assert.Equal("实例版本", categories[0].Title);
        Assert.Equal("模组", categories[1].Title);
        Assert.Equal("整合包", categories[2].Title);
        Assert.Equal("资源包", categories[3].Title);
        Assert.Equal("光影包", categories[4].Title);
        Assert.Equal("数据包", categories[5].Title);
    }

    [Fact]
    public async Task LoadsNewestReleaseAndLeavesLoaderUnselectedForVanilla()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        Assert.Equal("1.21.1", vm.SelectedGameVersion!.Value);
        Assert.Equal(new[] { "1.21.1", "1.20.1" }, vm.GameVersions.Select(version => version.Value));
        Assert.Equal("vanilla", vm.SelectedLoader!.Id);
        Assert.Equal(DeploymentItemStatus.Idle, vm.GameItem.Status);
        Assert.Equal(DeploymentItemStatus.NotSelected, vm.LoaderItem.Status);
        Assert.False(vm.ShowFabricApi);
        Assert.False(vm.ShowOptiFine);
        Assert.True(vm.CanStartDownload);
    }

    [Fact]
    public async Task VersionTypeFiltersTheListAndKeepsGameVersionSelectionOnly()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        vm.SelectedVersionType = vm.VersionTypes.First(type => type.Kind == MinecraftVersionKind.Snapshot);

        Assert.Equal(new[] { "24w14a" }, vm.GameVersions.Select(version => version.Value));
        Assert.Equal("24w14a", vm.SelectedGameVersion!.Value);
        Assert.Equal(1, harness.Versions.Calls);

        vm.SelectedVersionType = vm.VersionTypes.First(type => type.Kind == MinecraftVersionKind.AprilFools);

        Assert.Empty(vm.GameVersions);
        Assert.Null(vm.SelectedGameVersion);
        Assert.Equal("该类型下没有可用版本。", vm.VersionStatus);
        Assert.Equal(1, harness.Versions.Calls);
    }

    [Fact]
    public async Task FabricTargetExposesTheRequiredFabricApiRowWithSize()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        vm.SelectedLoader = vm.LoaderChoices.First(choice => choice.Id == LoaderKinds.Fabric);
        await vm.PendingComponentRefresh;

        Assert.True(vm.ShowFabricApi);
        Assert.False(vm.ShowOptiFine);
        Assert.Equal("0.16.14", vm.SelectedLoaderVersion!.Value);
        Assert.Equal("0.16.14", vm.SelectedLoader.VersionText);
        Assert.Equal("0.92.2", vm.SelectedFabricApiVersion!.Value);
        Assert.Equal("2 MB", vm.FabricApiItem.SizeText);
        Assert.Equal(DeploymentItemStatus.Idle, vm.FabricApiItem.Status);
        Assert.Equal(
            "安装Fabric必须同时安装Fabric api才能正常加载模组",
            vm.FabricApiNoticeText);
    }

    [Fact]
    public async Task ForgeTargetShowsOptiFineButNeverInstallsItByDefault()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        vm.SelectedLoader = vm.LoaderChoices.First(choice => choice.Id == LoaderKinds.Forge);
        await vm.PendingComponentRefresh;

        Assert.False(vm.ShowFabricApi);
        Assert.True(vm.ShowOptiFine);
        Assert.Null(vm.SelectedOptiFineVersion!.Value);
        Assert.Equal("不安装", vm.SelectedOptiFineVersion.DisplayName);
        Assert.Equal(DeploymentItemStatus.NotSelected, vm.OptiFineItem.Status);

        vm.SelectedOptiFineVersion = vm.OptiFineVersions.Last();

        Assert.Equal("HD_U_I7:F1", vm.SelectedOptiFineVersion.Value);
        Assert.Equal(DeploymentItemStatus.Idle, vm.OptiFineItem.Status);
        Assert.Equal("HD_U_I7:F1", vm.OptiFineItem.VersionText);
    }

    [Fact]
    public async Task NeoForgeDoesNotOfferOptiFine()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        vm.SelectedLoader = vm.LoaderChoices.First(choice => choice.Id == LoaderKinds.NeoForge);
        await vm.PendingComponentRefresh;

        Assert.False(vm.ShowOptiFine);
        Assert.Empty(vm.OptiFineVersions);
        Assert.Equal(DeploymentItemStatus.NotSelected, vm.OptiFineItem.Status);
    }

    [Fact]
    public async Task CleanroomIsRejectedOutsideMinecraft1122()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        vm.SelectedLoader = vm.LoaderChoices.First(choice => choice.Id == LoaderKinds.Cleanroom);
        await vm.PendingComponentRefresh;

        Assert.Equal("Cleanroom 仅支持 Minecraft 1.12.2。", vm.ComponentStatus);
        Assert.Empty(vm.LoaderVersions);

        await vm.StartDownloadCommand.ExecuteAsync(null);

        Assert.Equal("Cleanroom 仅支持 Minecraft 1.12.2。", vm.StatusMessage);
        Assert.Empty(harness.Instances.CreateCalls);
        Assert.Empty(harness.Install.Calls);
    }

    [Fact]
    public async Task DuplicateInstanceNameBlocksSubmissionWithTheApprovedMessage()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        harness.Instances.Existing.Add(new GameInstance
        {
            Id = "existing",
            Name = "生存存档",
            GameVersion = "1.20.1",
            VersionId = "1.20.1",
        });
        await vm.EnsureLoadedAsync();

        vm.InstanceName = "  生存存档 ";

        Assert.True(vm.IsNameDuplicate);
        Assert.False(vm.CanStartDownload);

        await vm.StartDownloadCommand.ExecuteAsync(null);

        Assert.Equal("实例已存在，请换一个名字或换一个目录", vm.StatusMessage);
        Assert.Empty(harness.Instances.CreateCalls);

        vm.InstanceName = "另一个存档";
        Assert.False(vm.IsNameDuplicate);
        Assert.True(vm.CanStartDownload);
    }

    [Fact]
    public async Task StartDownloadCreatesTheInstanceThenInstallsAndReportsCompletion()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.InstanceName = "测试实例";

        await vm.StartDownloadCommand.ExecuteAsync(null);

        var create = Assert.Single(harness.Instances.CreateCalls);
        Assert.Equal("测试实例", create.Name);
        Assert.Equal("1.21.1", create.GameVersion);
        Assert.Equal("vanilla", create.Loader);
        Assert.Null(create.LoaderVersion);
        Assert.Null(create.FabricApiVersion);
        Assert.Null(create.OptiFineVersion);

        var install = Assert.Single(harness.Install.Calls);
        Assert.Equal("inst-1", install);

        Assert.Equal(InstanceDeploymentState.Succeeded, vm.DeploymentState);
        Assert.Equal(DeploymentItemStatus.Completed, vm.GameItem.Status);
        Assert.Equal("已完成", vm.GameItem.StatusText);
        Assert.Equal(DeploymentItemStatus.NotSelected, vm.LoaderItem.Status);
        Assert.False(vm.ShowStartButton);
        Assert.True(vm.ShowResetButton);
        Assert.False(vm.IsConfigEnabled);
    }

    [Fact]
    public async Task ItemStatusLinesFollowTheRealInstallStages()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedLoader = vm.LoaderChoices.First(choice => choice.Id == LoaderKinds.Fabric);
        await vm.PendingComponentRefresh;

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Install.Gate = gate;
        harness.Install.BeforeGate =
        [
            new InstallProgress(InstallStage.Loader, "正在下载加载器", 1, 2, 0, 0, null),
        ];
        harness.Install.AfterGate =
        [
            new InstallProgress(InstallStage.Client, "正在下载客户端", 1, 4, 0, 0, null),
            new InstallProgress(InstallStage.FabricApi, "正在下载 Fabric API", 1, 1, 0, 0, null),
        ];

        var deploying = vm.StartDownloadCommand.ExecuteAsync(null);

        Assert.Equal(InstanceDeploymentState.Deploying, vm.DeploymentState);
        Assert.Equal(DeploymentItemStatus.Queued, vm.GameItem.Status);
        Assert.Equal(DeploymentItemStatus.Downloading, vm.LoaderItem.Status);
        Assert.Equal(DeploymentItemStatus.Queued, vm.FabricApiItem.Status);
        Assert.True(vm.ShowProgress);
        Assert.False(vm.IsConfigEnabled);
        Assert.False(vm.CanStartDownload);

        gate.SetResult(true);
        await deploying;

        Assert.Equal(InstanceDeploymentState.Succeeded, vm.DeploymentState);
        Assert.Equal("已完成", vm.GameItem.StatusText);
        Assert.Equal("已完成", vm.LoaderItem.StatusText);
        Assert.Equal("已完成", vm.FabricApiItem.StatusText);
        Assert.Equal(DeploymentItemStatus.NotSelected, vm.OptiFineItem.Status);
    }

    [Fact]
    public async Task InstallFailureKeepsTheCreatedInstanceAndRetriesWithoutRecreating()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        harness.Install.InstallResult = Result<InstallReport>.Fail("download.failed", "下载失败");

        await vm.StartDownloadCommand.ExecuteAsync(null);

        Assert.Equal(InstanceDeploymentState.Failed, vm.DeploymentState);
        Assert.True(vm.CanRetry);
        Assert.True(vm.ShowErrorInfo);
        Assert.Contains("下载失败", vm.ErrorText);
        Assert.Single(harness.Instances.CreateCalls);
        Assert.Single(harness.Install.Calls);

        harness.Install.InstallResult = Result<InstallReport>.Ok(new InstallReport("1.21.1", 3, 1, 1024, TimeSpan.FromSeconds(1)));

        await vm.RetryCommand.ExecuteAsync(null);

        Assert.Equal(InstanceDeploymentState.Succeeded, vm.DeploymentState);
        Assert.Single(harness.Instances.CreateCalls);
        Assert.Equal(2, harness.Install.Calls.Count);
        Assert.False(vm.CanRetry);
    }

    [Fact]
    public async Task CreateFailureDoesNotStartInstallAndKeepsTheFormUsable()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        harness.Instances.CreateResult = Result<GameInstance>.Fail("instance.duplicate_name", "已存在同名实例");

        await vm.StartDownloadCommand.ExecuteAsync(null);

        Assert.Empty(harness.Install.Calls);
        Assert.Equal(InstanceDeploymentState.Failed, vm.DeploymentState);
        Assert.True(vm.ShowErrorInfo);
        Assert.False(vm.CanRetry);
        Assert.True(vm.CanStartDownload);
        Assert.Equal(DeploymentItemStatus.Idle, vm.GameItem.Status);
    }

    [Fact]
    public async Task SuccessRequiresResetBeforeTheNextInstanceAndResetClearsEverythingButTheLocation()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.InstanceName = "第一个实例";
        var location = vm.InstallLocationText;

        await vm.StartDownloadCommand.ExecuteAsync(null);
        Assert.True(vm.ShowResetButton);
        Assert.False(vm.CanStartDownload);
        Assert.False(vm.IsConfigEnabled);

        vm.ResetDeploymentCommand.Execute(null);

        Assert.Equal(InstanceDeploymentState.Configuring, vm.DeploymentState);
        Assert.True(vm.ShowStartButton);
        Assert.False(vm.ShowResetButton);
        Assert.True(vm.IsConfigEnabled);
        Assert.Equal(string.Empty, vm.InstanceName);
        Assert.Null(vm.SelectedGameVersion);
        Assert.Equal("vanilla", vm.SelectedLoader!.Id);
        Assert.Empty(vm.LoaderVersions);
        Assert.Empty(vm.FabricApiVersions);
        Assert.Equal(DeploymentItemStatus.NotSelected, vm.GameItem.Status);
        Assert.Equal(DeploymentItemStatus.NotSelected, vm.LoaderItem.Status);
        Assert.Equal(location, vm.InstallLocationText);
        Assert.False(vm.ShowProgress);
    }

    [Fact]
    public async Task FabricDeploymentPassesTheSelectedApiVersionAndMarksTheApiItem()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedLoader = vm.LoaderChoices.First(choice => choice.Id == LoaderKinds.Fabric);
        await vm.PendingComponentRefresh;
        vm.SelectedFabricApiVersion = vm.FabricApiVersions.Last();

        await vm.StartDownloadCommand.ExecuteAsync(null);

        var create = Assert.Single(harness.Instances.CreateCalls);
        Assert.Equal("fabric", create.Loader);
        Assert.Equal("0.16.14", create.LoaderVersion);
        Assert.Equal("0.92.1", create.FabricApiVersion);
        Assert.Equal("已完成", vm.FabricApiItem.StatusText);
    }

    [Fact]
    public async Task VersionCatalogFailureIsReportedWithoutPretendingToBeReady()
    {
        using var harness = new Harness();
        harness.Versions.Result = Result<IReadOnlyList<MinecraftGameVersion>>.Fail("network.down", "网络不可用");
        var vm = harness.ViewModel;

        await vm.EnsureLoadedAsync();

        Assert.Empty(vm.GameVersions);
        Assert.Null(vm.SelectedGameVersion);
        Assert.Contains("网络不可用", vm.VersionStatus);
        Assert.False(vm.CanStartDownload);

        await vm.StartDownloadCommand.ExecuteAsync(null);

        Assert.Empty(harness.Instances.CreateCalls);
        Assert.Equal("请选择游戏版本。", vm.StatusMessage);
    }

    [Fact]
    public async Task InstallLocationsShowThePrimaryWorkspaceAndExternalSourcesAsReadOnly()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        Assert.Equal(2, vm.InstallLocations.Count);
        var primary = vm.InstallLocations.First(row => row.IsPrimary);
        Assert.False(primary.IsExternal);
        Assert.Equal("当前安装位置", primary.PrimaryHint);
        var external = vm.InstallLocations.First(row => !row.IsPrimary);
        Assert.True(external.IsExternal);
        Assert.Equal("只读来源", external.SourceHint);
        Assert.Contains(harness.Root, vm.InstallLocationText);
    }

    private sealed class Harness : IDisposable
    {
        private readonly SynchronizationContext? _previous;

        public Harness()
        {
            Root = Directory.CreateTempSubdirectory("ecl-download-").FullName;
            // 让 Progress<T> 同步回调：安装阶段映射在测试中可确定地观察。
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new ImmediateSynchronizationContext());

            Versions.Versions.AddRange(
            [
                new MinecraftGameVersion("1.21.1", MinecraftVersionKind.Release, new DateTimeOffset(2024, 8, 8, 0, 0, 0, TimeSpan.Zero)),
                new MinecraftGameVersion("1.20.1", MinecraftVersionKind.Release, new DateTimeOffset(2023, 6, 12, 0, 0, 0, TimeSpan.Zero)),
                new MinecraftGameVersion("24w14a", MinecraftVersionKind.Snapshot, new DateTimeOffset(2024, 4, 3, 0, 0, 0, TimeSpan.Zero)),
            ]);

            Workspace.RootList.Add(new WorkspaceRoot(
                "primary",
                "主工作区",
                Path.Combine(Root, "minecraft"),
                WorkspaceKind.Managed,
                WorkspaceStatus.Ready));
            Workspace.RootList.Add(new WorkspaceRoot(
                "external",
                "PCL",
                Path.Combine(Root, "PCL", ".minecraft"),
                WorkspaceKind.External,
                WorkspaceStatus.Ready));

            var localizer = Localizer();
            ViewModel = new InstanceVersionPageViewModel(
                Instances,
                Install,
                Versions,
                Loaders,
                Workspace,
                new InstanceSourceService(Workspace, NullLogger<InstanceSourceService>.Instance),
                Dialogs,
                FileDialogs,
                localizer,
                new AppPaths(Root, Path.Combine(Root, "minecraft")));
        }

        public string Root { get; }

        public FakeInstanceService Instances { get; } = new();

        public FakeInstallService Install { get; } = new();

        public FakeVersionCatalog Versions { get; } = new();

        public FakeLoaderCatalog Loaders { get; } = new();

        public FakeWorkspaceService Workspace { get; } = new();

        public FakeDialogEngine Dialogs { get; } = new();

        public FakeDialogService FileDialogs { get; } = new();

        public InstanceVersionPageViewModel ViewModel { get; }

        public void Dispose()
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 临时目录清理失败不影响测试结论。
            }
        }
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class FakeInstanceService : IInstanceService
    {
        public List<GameInstance> Existing { get; } = [];

        public List<CreateCall> CreateCalls { get; } = [];

        public Result<GameInstance>? CreateResult { get; set; }

        public Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameInstance>>(Existing.ToArray());

        public Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameInstance?>(Existing.FirstOrDefault(instance => instance.Id == instanceId));

        public Task<Result<GameInstance>> CreateAsync(
            string name,
            string gameVersion,
            string loader = "vanilla",
            string? loaderVersion = null,
            string? fabricApiVersion = null,
            string? optiFineVersion = null,
            CancellationToken cancellationToken = default)
        {
            CreateCalls.Add(new CreateCall(name, gameVersion, loader, loaderVersion, fabricApiVersion, optiFineVersion));
            if (CreateResult is { } failed)
            {
                return Task.FromResult(failed);
            }

            var instance = new GameInstance
            {
                Id = "inst-1",
                Name = name,
                GameVersion = gameVersion,
                VersionId = gameVersion,
                Loader = loader,
                LoaderVersion = loaderVersion,
                FabricApiVersion = fabricApiVersion,
                OptiFineVersion = optiFineVersion,
            };
            Existing.Add(instance);
            return Task.FromResult(Result<GameInstance>.Ok(instance));
        }

        public Task<Result<GameInstance>> UpdateConfigurationAsync(
            string instanceId,
            string gameVersion,
            string loader,
            string? loaderVersion = null,
            string? fabricApiVersion = null,
            string? optiFineVersion = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<GameInstance>> RestoreConfigurationAsync(
            GameInstance restorePoint,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> DeleteAsync(string instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstanceDeletionPlan>> GetDeletionPlanAsync(
            string instanceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> DeleteAsync(
            string instanceId,
            InstanceDeleteOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<DetectedVersion>> ScanVersionsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> ImportAsync(
            string versionId,
            string? name = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task TouchLastPlayedAsync(string instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<GameInstance>> RelocateAsync(
            string instanceId,
            string newGameDirectory,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<GameInstance>> CloneAsync(
            string sourceInstanceId,
            string name,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    internal sealed record CreateCall(
        string Name,
        string GameVersion,
        string Loader,
        string? LoaderVersion,
        string? FabricApiVersion,
        string? OptiFineVersion);

    private sealed class FakeInstallService : IVersionInstallService
    {
        public List<string> Calls { get; } = [];

        public Result<InstallReport>? InstallResult { get; set; }

        public TaskCompletionSource<bool>? Gate { get; set; }

        public IReadOnlyList<InstallProgress> BeforeGate { get; set; } = [];

        public IReadOnlyList<InstallProgress> AfterGate { get; set; } = [];

        public async Task<Result<InstallReport>> InstallAsync(
            string instanceId,
            IProgress<InstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(instanceId);
            foreach (var stage in BeforeGate)
            {
                progress?.Report(stage);
            }

            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            foreach (var stage in AfterGate)
            {
                progress?.Report(stage);
            }

            return InstallResult ?? Result<InstallReport>.Ok(new InstallReport("1.21.1", 2, 0, 2048, TimeSpan.FromSeconds(1)));
        }

        public Task<InstallOutcome> ReconfigureAsync(
            string instanceId,
            string gameVersion,
            string loader,
            string? loaderVersion = null,
            string? fabricApiVersion = null,
            string? optiFineVersion = null,
            IProgress<InstallProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeVersionCatalog : IVersionCatalog
    {
        public List<MinecraftGameVersion> Versions { get; } = [];

        public int Calls { get; private set; }

        public Result<IReadOnlyList<MinecraftGameVersion>>? Result { get; set; }

        public Task<Result<IReadOnlyList<MinecraftGameVersion>>> GetVersionsAsync(
            bool refresh = false,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result ?? Result<IReadOnlyList<MinecraftGameVersion>>.Ok(Versions.ToArray()));
        }
    }

    private sealed class FakeLoaderCatalog : ILoaderCatalog
    {
        public List<LoaderVersionInfo> FabricLoaders { get; } =
        [
            Loader("0.16.14"),
            Loader("0.16.10"),
        ];

        public List<LoaderVersionInfo> ForgeVersions { get; } = [Loader("47.2.0")];

        public List<LoaderVersionInfo> NeoForgeVersions { get; } = [Loader("21.1.0")];

        public List<LoaderVersionInfo> CleanroomVersions { get; } = [Loader("0.3.4")];

        public List<FabricApiVersionInfo> FabricApi { get; } =
        [
            new("0.92.2", "https://example.invalid/api-0.92.2.jar", "fabric-api-0.92.2.jar", null, 2 * 1024 * 1024, new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            new("0.92.1", "https://example.invalid/api-0.92.1.jar", "fabric-api-0.92.1.jar", null, null, new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero)),
        ];

        public List<OptiFineVersionInfo> OptiFine { get; } =
        [
            new("HD_U_I7", "F1", "OptiFine_1.20.1_HD_U_I7.jar", new DateTimeOffset(2025, 10, 15, 0, 0, 0, TimeSpan.Zero)),
        ];

        public Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetFabricLoadersAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<LoaderVersionInfo>>.Ok(FabricLoaders.ToArray()));

        public Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetForgeVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<LoaderVersionInfo>>.Ok(ForgeVersions.ToArray()));

        public Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetNeoForgeVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<LoaderVersionInfo>>.Ok(NeoForgeVersions.ToArray()));

        public Task<Result<IReadOnlyList<LoaderVersionInfo>>> GetCleanroomVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<LoaderVersionInfo>>.Ok(CleanroomVersions.ToArray()));

        public Task<Result<IReadOnlyList<FabricApiVersionInfo>>> GetFabricApiVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<FabricApiVersionInfo>>.Ok(FabricApi.ToArray()));

        public Task<Result<IReadOnlyList<OptiFineVersionInfo>>> GetOptiFineVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<OptiFineVersionInfo>>.Ok(OptiFine.ToArray()));

        public Task<Result<string>> GetFabricProfileAsync(
            string gameVersion,
            string loaderVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static LoaderVersionInfo Loader(string version) =>
            new(version, true, new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero));
    }

    private sealed class FakeWorkspaceService : IWorkspaceService
    {
        public WorkspaceFile State { get; } = new();

        public WorkspaceRoot? Primary => Roots.FirstOrDefault();

        public IReadOnlyList<WorkspaceRoot> Roots => RootList;

        public List<WorkspaceRoot> RootList { get; } = [];

        public bool IsConfigured => Roots.Count > 0;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<WorkspaceSuggestion> Suggest() => [];

        public WorkspacePathCheck Check(string path, bool createIfMissing) =>
            new(path, true, WorkspaceStatus.Ready, null, null, null, []);

        public Task<Result<WorkspaceRoot>> InitializeAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<WorkspaceRoot>> RegisterExternalAsync(
            string path,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<WorkspacePathCheck>> ValidatePrimaryAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SkipOnboardingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateRootAsync(WorkspaceRoot root, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<bool>> RemoveRootAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public WorkspaceLegacyInfo DetectLegacy() => new(false, string.Empty, 0, 0);

        public Task<Result<WorkspaceRoot>> AdoptLegacyAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public StorageLayout GetStorageLayout() => new(string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private sealed class FakeDialogEngine : IDialogEngine
    {
        public bool IsAvailable => false;

        public List<DialogRequest> Requests { get; } = [];

        public DialogResult Show(DialogRequest request) => DialogResult.Cancel;

        public Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(DialogResult.Cancel);
        }

        public bool CloseTop(DialogOutcome outcome) => false;
    }

    private sealed class FakeDialogService : IDialogService
    {
        public List<(string Title, string Message)> Errors { get; } = [];

        public bool Confirm(string title, string message, string? acknowledgementToken = null) => false;

        public InstanceDeletionChoice ChooseInstanceDeletion(
            string title,
            string message,
            bool canDeleteManagedData) => InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowError(string title, string message) => Errors.Add((title, message));

        public string? PickOpenFile(string title, string filter) => null;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}








