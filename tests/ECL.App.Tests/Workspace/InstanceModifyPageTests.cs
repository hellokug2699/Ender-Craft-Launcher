// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels;
using Ecl.App.Workspace;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Versions;
using Ecl.Operations;
using Ecl.Sessions;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 实例工作区「修改」页（B2 步骤 3b-4a）：只读变更计划、影响三档、Java/内容/任务/快照检查、
/// 状态机与生命周期。本步骤不得产生任何写调用，也不得把未知影响伪装成兼容。
/// </summary>
public sealed class InstanceModifyPageTests
{
    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    [Fact]
    public void ShowsTheWorkspaceInstanceAndCurrentConfiguration()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;

        Assert.Equal("1-20-1-790872", vm.InstanceId);
        Assert.Equal("Test Instance", vm.InstanceName);
        Assert.Contains("1.20.1", vm.CurrentVersionText);
        Assert.Contains("Fabric 0.16.10", vm.CurrentLoaderText);
        Assert.Equal("无", vm.CurrentComponentsText);
        Assert.Equal(InstanceModifyPlanState.NotChecked, vm.PlanState);
        Assert.False(vm.HasPlan);
        Assert.Empty(vm.PlanItems);
        Assert.Equal(harness.Model.GameDirectory, vm.CurrentPath);
    }

    [Fact]
    public async Task LoadsCatalogAndFiltersLoadersByTargetVersion()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;

        await vm.EnsureLoadedAsync();

        Assert.Equal(new[] { "1.21", "1.20.1", "1.12.2" }, vm.TargetVersions.Select(choice => choice.Value));
        Assert.Equal("1.20.1", vm.SelectedTargetVersion!.Value);
        Assert.Equal(new[] { "vanilla", "fabric", "forge", "neoforge" }, vm.LoaderChoices.Select(choice => choice.Id));
        Assert.Contains("1.12.2", vm.LoaderFilterNote);
        Assert.Equal("fabric", vm.SelectedTargetLoader!.Id);
        Assert.Equal("0.16.10", vm.SelectedTargetLoaderVersion!.Value);
        Assert.Contains("0.92.2", vm.FabricApiText);

        // 切到 1.12.2 后 Cleanroom 才出现在加载器选项中。
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.12.2");
        await vm.PendingTargetRefresh;

        Assert.Contains("cleanroom", vm.LoaderChoices.Select(choice => choice.Id));
        Assert.Equal(string.Empty, vm.LoaderFilterNote);
    }

    [Fact]
    public async Task CleanroomIsNotOfferedForOtherVersions()
    {
        using var harness = new Harness(gameVersion: "1.12.2", loader: "cleanroom", loaderVersion: "0.3.4");
        var vm = harness.ViewModel;

        await vm.EnsureLoadedAsync();
        Assert.Contains("cleanroom", vm.LoaderChoices.Select(choice => choice.Id));
        Assert.Equal("cleanroom", vm.SelectedTargetLoader!.Id);

        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        Assert.DoesNotContain("cleanroom", vm.LoaderChoices.Select(choice => choice.Id));
        Assert.NotEqual("cleanroom", vm.SelectedTargetLoader!.Id);
        Assert.Contains("1.12.2", vm.LoaderFilterNote);
    }

    [Fact]
    public async Task UnchangedTargetRequiresActionAndSkipsHeavyChecks()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(InstanceModifyPlanState.NeedsAction, vm.PlanState);
        Assert.True(vm.HasActionItems);
        Assert.Contains(vm.ActionItems, text => text.Contains("没有可执行的变更"));
        Assert.Contains(vm.PlanItems, item => item.Detail.Contains("保持 1.20.1"));
        Assert.Equal(0, harness.Mods.ScanCalls);
        Assert.DoesNotContain(vm.PlanItems, item => item.Kind == ModifyPlanItemKind.Downloads);
        Assert.Empty(vm.Blockers);
    }

    [Fact]
    public async Task RunningGameProcessMustBeClosedBeforeExecution()
    {
        using var harness = new Harness(configure: it => it.Sessions.Sessions.Add(
            Session("session-running", it.Model.Id, GameSessionState.Running, Environment.ProcessId)));
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(InstanceModifyPlanState.NeedsAction, vm.PlanState);
        Assert.Contains(vm.ActionItems, text => text.Contains("session-running"));
        var processItem = Assert.Single(vm.PlanItems, item => item.Kind == ModifyPlanItemKind.RunningProcess);
        Assert.Equal(ModifyPlanImpact.Warning, processItem.Impact);
        Assert.Contains("session-running", processItem.Detail);
    }

    [Fact]
    public async Task SessionWithoutProcessIdCannotBeProvenStopped()
    {
        using var harness = new Harness(configure: it => it.Sessions.Sessions.Add(
            Session("session-unverified", it.Model.Id, GameSessionState.Running, processId: null)));
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(InstanceModifyPlanState.NeedsAction, vm.PlanState);
        Assert.Contains(vm.ActionItems, text => text.Contains("session-unverified"));
    }

    [Fact]
    public async Task ExpiredSessionDoesNotBlockWhenTheProcessIsGone()
    {
        using var harness = new Harness(configure: it => it.Sessions.Sessions.Add(
            Session("session-stale", it.Model.Id, GameSessionState.Running, processId: 999_999)));
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(InstanceModifyPlanState.Executable, vm.PlanState);
        var processItem = Assert.Single(vm.PlanItems, item => item.Kind == ModifyPlanItemKind.RunningProcess);
        Assert.Equal(ModifyPlanImpact.Info, processItem.Impact);
    }

    [Fact]
    public async Task ActiveWriteTaskIsReportedAsUnresolvableConflict()
    {
        using var harness = new Harness(configure: it => it.Operations = new StubOperationService(
        [
            new OperationSnapshot
            {
                Id = Guid.NewGuid(),
                Kind = OperationKind.Install,
                Title = "安装",
                State = OperationState.Running,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        ]));
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(InstanceModifyPlanState.NeedsAction, vm.PlanState);
        Assert.Contains(vm.ActionItems, text => text.Contains("写入类任务"));
        var tasks = Assert.Single(vm.PlanItems, item => item.Kind == ModifyPlanItemKind.WriteTasks);
        Assert.Equal(ModifyPlanImpact.Warning, tasks.Impact);
        Assert.Contains("无法证明属于哪个实例", tasks.Detail);
    }

    [Fact]
    public async Task MissingGameDirectoryCannotExecute()
    {
        using var harness = new Harness(createGameDirectory: false);
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(InstanceModifyPlanState.NotExecutable, vm.PlanState);
        Assert.Contains(vm.Blockers, text => text.Contains("游戏目录不存在"));
    }

    [Fact]
    public async Task MissingLoaderVersionsCannotExecute()
    {
        using var harness = new Harness(configure: it => it.Loaders.ForgeVersions.Clear());
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        vm.SelectedTargetLoader = vm.LoaderChoices.First(choice => choice.Id == "forge");
        await vm.PendingTargetRefresh;

        Assert.Null(vm.SelectedTargetLoaderVersion);
        Assert.NotEmpty(vm.TargetLoadError);

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(InstanceModifyPlanState.NotExecutable, vm.PlanState);
        Assert.Contains(vm.Blockers, text => text.Contains("加载器版本"));
    }

    [Fact]
    public async Task MissingFabricApiVersionCannotExecute()
    {
        using var harness = new Harness(configure: it => it.Loaders.FabricApiByVersion.Clear());
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(InstanceModifyPlanState.NotExecutable, vm.PlanState);
        Assert.Contains(vm.Blockers, text => text.Contains("Fabric API"));
    }

    [Fact]
    public async Task ModImpactSeparatesCertainUnknownAndCompatible()
    {
        using var harness = new Harness(configure: it =>
        {
            it.Mods.Entries.AddRange(
            [
                Mod("old-only.jar", metadata: Metadata("old-only", "fabric", "[1.20,1.21)")),
                Mod("new-only.jar", metadata: Metadata("new-only", "fabric", "[1.21,1.22)")),
                Mod("unpinned.jar", metadata: Metadata("unpinned", "fabric", range: null)),
                Mod("no-meta.jar", metadata: null),
                Mod("old-disabled.jar", enabled: false, metadata: Metadata("old-disabled", "fabric", "[1.20,1.21)")),
            ]);
        });
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(InstanceModifyPlanState.Executable, vm.PlanState);

        var modsItems = vm.PlanItems.Where(item => item.Kind == ModifyPlanItemKind.Mods).ToArray();
        var certain = Assert.Single(modsItems, item => item.Impact == ModifyPlanImpact.Certain);
        Assert.Contains("old-only.jar", certain.Detail);
        Assert.DoesNotContain("new-only.jar", certain.Detail);
        Assert.DoesNotContain("old-disabled.jar", certain.Detail);

        var unknown = Assert.Single(modsItems, item => item.Impact == ModifyPlanImpact.Unknown);
        Assert.Contains("unpinned.jar", unknown.Detail);
        Assert.Contains("no-meta.jar", unknown.Detail);

        var compatible = Assert.Single(modsItems, item => item.Impact == ModifyPlanImpact.Info);
        Assert.Contains("1 个", compatible.Detail);

        // 确定不可用的模组必须给出具体后续处理项，而不是只显示一行汇总。
        Assert.Contains(vm.PlanItems, item =>
            item.Kind == ModifyPlanItemKind.Manual && item.Detail.Contains("old-only.jar"));
    }

    [Fact]
    public async Task UnknownModMetadataIsNeverReportedAsCompatible()
    {
        using var harness = new Harness(configure: it =>
        {
            it.Mods.Entries.Add(Mod("no-meta.jar", metadata: null));
        });
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        var modsItems = vm.PlanItems.Where(item => item.Kind == ModifyPlanItemKind.Mods).ToArray();
        Assert.DoesNotContain(modsItems, item => item.Impact == ModifyPlanImpact.Info && item.Detail.Contains("声明支持"));
        Assert.Contains(modsItems, item => item.Impact == ModifyPlanImpact.Unknown && item.Detail.Contains("no-meta.jar"));
    }

    [Fact]
    public async Task TargetJavaUsesInferenceAndMarksIncompatibility()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        var current = Assert.Single(vm.PlanItems, item => item.Kind == ModifyPlanItemKind.JavaCurrent);
        Assert.Equal(ModifyPlanImpact.Info, current.Impact);

        var target = Assert.Single(vm.PlanItems, item => item.Kind == ModifyPlanItemKind.JavaTarget);
        Assert.Equal(ModifyPlanImpact.Warning, target.Impact);
        Assert.Contains("Java 21+", target.Detail);
        Assert.Contains("推断", target.Detail);
        Assert.Contains("版本过低", target.Detail);
    }

    [Fact]
    public async Task JavaRequirementStaysUnknownWhenNoRuntimeIsAvailable()
    {
        using var harness = new Harness(configure: it => it.Java.Runtime = null);
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        var target = Assert.Single(vm.PlanItems, item => item.Kind == ModifyPlanItemKind.JavaTarget);
        Assert.Equal(ModifyPlanImpact.Unknown, target.Impact);
        Assert.Contains("Java 21+", target.Detail);
        Assert.DoesNotContain("满足推断要求", target.Detail);
    }

    [Fact]
    public async Task ForgeTargetWithoutJavaCannotRunProcessors()
    {
        using var harness = new Harness(configure: it => it.Java.Runtime = null);
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetLoader = vm.LoaderChoices.First(choice => choice.Id == "forge");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(InstanceModifyPlanState.NotExecutable, vm.PlanState);
        Assert.Contains(vm.Blockers, text => text.Contains("processors"));
    }

    [Fact]
    public async Task OptiFineIsOnlyPreservedWhenItExistsForTheTarget()
    {
        using var harness = new Harness(
            gameVersion: "1.20.1",
            loader: "forge",
            loaderVersion: "47.2.0",
            optiFineVersion: "HD_U_I6:E7");
        var vm = harness.ViewModel;

        await vm.EnsureLoadedAsync();

        // 当前 OptiFine 版本在目标版本目录中不存在：不得自动换成别的版本。
        Assert.Null(vm.SelectedTargetOptiFine!.Value);
        Assert.Contains("HD_U_I6:E7", vm.OptiFineNote);

        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;
        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Contains(vm.PlanItems, item =>
            item.Kind == ModifyPlanItemKind.Manual && item.Detail.Contains("HD_U_I6:E7"));
        Assert.DoesNotContain(vm.PlanItems, item =>
            item.Kind == ModifyPlanItemKind.Downloads && item.Detail.Contains("OptiFine"));
    }

    [Fact]
    public async Task DownloadsListRealArtifactsButNeverClaimsSizes()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        var downloads = vm.PlanItems.Where(item => item.Kind == ModifyPlanItemKind.Downloads).ToArray();
        Assert.Contains(downloads, item => item.Detail.Contains("客户端与依赖库"));
        Assert.Contains(downloads, item => item.Detail.Contains("资源文件"));
        Assert.Contains(downloads, item => item.Detail.Contains("Fabric 安装器"));
        Assert.Contains(downloads, item => item.Detail.Contains("Fabric API 0.100.0"));
        Assert.All(downloads, item => Assert.True(
            item.Detail.Contains("大小未知") || item.Detail.Contains("不下载任何文件")));
        Assert.DoesNotContain(downloads, item => item.Detail.Contains(" MB"));
    }

    [Fact]
    public async Task PlanAlsoReportsSnapshotAndDirectoryScope()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        var snapshot = Assert.Single(vm.PlanItems, item => item.Kind == ModifyPlanItemKind.Snapshot);
        Assert.Contains("完整实例快照", snapshot.Detail);
        Assert.Contains("游戏目录", snapshot.Detail);

        var identity = Assert.Single(vm.PlanItems, item => item.Kind == ModifyPlanItemKind.Instance);
        Assert.Contains("InstanceId", identity.Detail);
        Assert.Contains(harness.Model.Id, identity.Detail);

        var directory = Assert.Single(vm.PlanItems, item => item.Kind == ModifyPlanItemKind.Directory);
        Assert.Contains(harness.Model.GameDirectory!, directory.Detail);
    }

    [Fact]
    public async Task LoaderVersionOnlyChangeDoesNotReinterpretExistingModIssues()
    {
        using var harness = new Harness(configure: it => it.Mods.Entries.Add(
            Mod("already-broken.jar", metadata: Metadata("already-broken", "fabric", "[1.21,1.22)"))));
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();

        vm.SelectedTargetLoaderVersion = vm.LoaderVersions.Last();
        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(InstanceModifyPlanState.Executable, vm.PlanState);
        var mods = Assert.Single(vm.PlanItems, item => item.Kind == ModifyPlanItemKind.Mods);
        Assert.Equal(ModifyPlanImpact.Info, mods.Impact);
        Assert.Contains("不受本次变更影响", mods.Detail);
        Assert.Equal(0, harness.Mods.ScanCalls);
        Assert.DoesNotContain(vm.PlanItems, item =>
            item.Kind == ModifyPlanItemKind.Manual && item.Detail.Contains("already-broken.jar"));
    }

    [Fact]
    public async Task GeneratingAPlanCallsNoWriteApi()
    {
        using var harness = new Harness(configure: it => it.Mods.Entries.Add(
            Mod("mod.jar", metadata: Metadata("mod", "fabric", "[1.21,1.22)"))));
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;

        await vm.GeneratePlanCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Mods.ScanCalls);
        Assert.Empty(harness.Mods.ToggleCalls);
        Assert.Empty(harness.Mods.DeleteCalls);
        Assert.Empty(harness.Mods.ImportCalls);
        Assert.Equal(0, harness.Packs.WriteCalls);

        // 结构保证：本页面根本没有执行写入所需的领域依赖，3b-4b 接入前不可能“顺手执行”。
        var dependencies = typeof(InstanceModifyViewModel)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(typeof(Ecl.Minecraft.Install.IVersionInstallService), dependencies);
        Assert.DoesNotContain(typeof(Ecl.Minecraft.Engine.Snapshots.IInstanceSnapshotService), dependencies);
        Assert.DoesNotContain(typeof(IInstanceService), dependencies);
        Assert.DoesNotContain(typeof(Ecl.Minecraft.Engine.Transactions.InstanceTransactionCoordinator), dependencies);
    }

    [Fact]
    public async Task ChangingAnyTargetValueInvalidatesTheExistingPlan()
    {
        using var harness = new Harness();
        var vm = harness.ViewModel;
        await vm.EnsureLoadedAsync();
        vm.SelectedTargetVersion = vm.TargetVersions.First(choice => choice.Value == "1.21");
        await vm.PendingTargetRefresh;
        await vm.GeneratePlanCommand.ExecuteAsync(null);
        Assert.NotEqual(InstanceModifyPlanState.NotChecked, vm.PlanState);

        vm.SelectedTargetLoaderVersion = vm.LoaderVersions.Last();

        Assert.Equal(InstanceModifyPlanState.NotChecked, vm.PlanState);
        Assert.Empty(vm.PlanItems);
        Assert.Empty(vm.ActionItems);
        Assert.Empty(vm.Blockers);
        Assert.False(vm.HasPlan);
    }

    [Fact]
    public async Task DisposeStopsInFlightCatalogLoadFromWritingBack()
    {
        using var harness = new Harness(configure: it => it.Versions.Gate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously));
        var vm = harness.ViewModel;

        var loading = vm.EnsureLoadedAsync();
        vm.Dispose();
        harness.Versions.Gate!.SetResult(true);
        await loading;

        Assert.True(vm.IsDisposed);
        Assert.Empty(vm.TargetVersions);
        Assert.Empty(vm.LoaderVersions);
        Assert.Equal(InstanceModifyPlanState.NotChecked, vm.PlanState);
        Assert.False(vm.GeneratePlanCommand.CanExecute(null));
    }

    [Fact]
    public async Task TwoWorkspacesKeepIndependentTargetState()
    {
        using var first = new Harness();
        using var second = new Harness();
        await first.ViewModel.EnsureLoadedAsync();
        await second.ViewModel.EnsureLoadedAsync();

        first.ViewModel.SelectedTargetVersion = first.ViewModel.TargetVersions.First(choice => choice.Value == "1.12.2");
        await first.ViewModel.PendingTargetRefresh;

        Assert.Equal("1.12.2", first.ViewModel.SelectedTargetVersion!.Value);
        Assert.Equal("1.20.1", second.ViewModel.SelectedTargetVersion!.Value);
        Assert.Equal(InstanceModifyPlanState.NotChecked, second.ViewModel.PlanState);

        first.Dispose();

        Assert.True(first.ViewModel.IsDisposed);
        Assert.False(second.ViewModel.IsDisposed);
        Assert.Equal("1.20.1", second.ViewModel.SelectedTargetVersion!.Value);
    }

    private static GameLogSession Session(
        string sessionId,
        string instanceId,
        GameSessionState state,
        int? processId) =>
        new(
            new SessionManifest
            {
                SessionId = sessionId,
                InstanceId = instanceId,
                State = state,
                ProcessId = processId,
            },
            Path.GetTempPath());

    private static ModEntry Mod(
        string fileName,
        bool enabled = true,
        ModMetadata? metadata = null,
        IReadOnlyList<ModIssue>? issues = null) =>
        new(fileName, Path.Combine(Path.GetTempPath(), fileName), enabled, metadata, null, issues ?? []);

    private static ModMetadata Metadata(string modId, string platform, string? range) =>
        new(
            modId,
            modId,
            "1.0.0",
            null,
            platform switch
            {
                "fabric" => ModPlatform.Fabric,
                "forge" => ModPlatform.Forge,
                "neoforge" => ModPlatform.NeoForge,
                _ => ModPlatform.Unknown,
            },
            [new ModDependency("minecraft", true, range)],
            []);

    private sealed class Harness : IDisposable
    {
        public Harness(
            Action<Harness>? configure = null,
            string gameVersion = "1.20.1",
            string loader = "fabric",
            string? loaderVersion = "0.16.10",
            bool createGameDirectory = true,
            string? optiFineVersion = null)
        {
            Root = Directory.CreateTempSubdirectory("ecl-modify-").FullName;
            var gameDirectory = Path.Combine(Root, "minecraft");
            if (createGameDirectory)
            {
                Directory.CreateDirectory(gameDirectory);
            }

            Model = new GameInstance
            {
                Id = "1-20-1-790872",
                Name = "Test Instance",
                GameVersion = gameVersion,
                VersionId = $"modded-{gameVersion}",
                Loader = loader,
                LoaderVersion = loaderVersion,
                OptiFineVersion = optiFineVersion,
                GameDirectory = gameDirectory,
            };

            Versions.Versions.AddRange(
            [
                new MinecraftGameVersion("1.21", MinecraftVersionKind.Release, new DateTimeOffset(2025, 6, 17, 0, 0, 0, TimeSpan.Zero)),
                new MinecraftGameVersion("1.20.1", MinecraftVersionKind.Release, new DateTimeOffset(2023, 6, 12, 0, 0, 0, TimeSpan.Zero)),
                new MinecraftGameVersion("1.12.2", MinecraftVersionKind.Release, new DateTimeOffset(2017, 9, 18, 0, 0, 0, TimeSpan.Zero)),
            ]);

            configure?.Invoke(this);

            var localizer = Localizer();
            var item = new InstanceItemViewModel(Model, localizer, new AppPaths(Root, Root));
            ViewModel = new InstanceModifyViewModel(
                new InstanceWorkspaceState(item),
                Versions,
                Loaders,
                Resolver,
                Java,
                Mods,
                Packs,
                Sessions,
                Operations,
                localizer);
        }

        public FakeVersionCatalog Versions { get; } = new();

        public FakeLoaderCatalog Loaders { get; } = new();

        public FakeVersionResolver Resolver { get; } = new();

        public FakeJavaSelectionService Java { get; } = new();

        public FakeModService Mods { get; } = new();

        public FakeContentPackService Packs { get; } = new();

        public FakeGameSessionService Sessions { get; } = new();

        public IOperationService Operations { get; set; } = new StubOperationService();

        public string Root { get; }

        public GameInstance Model { get; }

        public InstanceModifyViewModel ViewModel { get; }

        public void Dispose()
        {
            ViewModel.Dispose();
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

    private sealed class FakeVersionCatalog : IVersionCatalog
    {
        public List<MinecraftGameVersion> Versions { get; } = [];

        public TaskCompletionSource<bool>? Gate { get; set; }

        public async Task<Result<IReadOnlyList<MinecraftGameVersion>>> GetVersionsAsync(
            bool refresh = false,
            CancellationToken cancellationToken = default)
        {
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            return Result<IReadOnlyList<MinecraftGameVersion>>.Ok(Versions.ToArray());
        }
    }

    private sealed class FakeLoaderCatalog : ILoaderCatalog
    {
        public List<LoaderVersionInfo> FabricLoaders { get; } =
        [
            Loader("0.16.10"),
            Loader("0.16.9"),
        ];

        public List<LoaderVersionInfo> ForgeVersions { get; } = [Loader("47.2.0")];

        public List<LoaderVersionInfo> NeoForgeVersions { get; } = [Loader("21.1.0")];

        public List<LoaderVersionInfo> CleanroomVersions { get; } = [Loader("0.3.4")];

        public Dictionary<string, List<FabricApiVersionInfo>> FabricApiByVersion { get; } = new(StringComparer.Ordinal)
        {
            ["1.20.1"] = [Api("0.92.2")],
            ["1.21"] = [Api("0.100.0")],
            ["1.12.2"] = [Api("0.4.0")],
        };

        public Dictionary<string, List<OptiFineVersionInfo>> OptiFineByVersion { get; } = new(StringComparer.Ordinal)
        {
            ["1.20.1"] = [OptiFine("HD_U_I7", "F1")],
            ["1.21"] = [OptiFine("HD_U_I7", "F1")],
            ["1.12.2"] = [OptiFine("HD_U_C9", "E2")],
        };

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
            Task.FromResult(Result<IReadOnlyList<FabricApiVersionInfo>>.Ok(
                FabricApiByVersion.TryGetValue(gameVersion, out var versions) ? versions.ToArray() : []));

        public Task<Result<IReadOnlyList<OptiFineVersionInfo>>> GetOptiFineVersionsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<OptiFineVersionInfo>>.Ok(
                OptiFineByVersion.TryGetValue(gameVersion, out var versions) ? versions.ToArray() : []));

        public Task<Result<string>> GetFabricProfileAsync(
            string gameVersion,
            string loaderVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static LoaderVersionInfo Loader(string version) =>
            new(version, true, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        private static FabricApiVersionInfo Api(string version) =>
            new(version, "https://example.invalid/api.jar", $"fabric-api-{version}.jar", null, null, DateTimeOffset.UtcNow);

        private static OptiFineVersionInfo OptiFine(string type, string patch) =>
            new(type, patch, $"OptiFine_{type}_{patch}.jar", DateTimeOffset.UtcNow);
    }

    private sealed class FakeVersionResolver : IVersionResolver
    {
        public Task<Result<MinecraftVersion>> ResolveAsync(
            string versionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<MinecraftVersion>.Ok(new MinecraftVersion
            {
                Id = versionId,
                JavaVersion = new VersionJavaRequirement { MajorVersion = 17 },
            }));
    }

    private sealed class FakeJavaSelectionService : IJavaSelectionService
    {
        public JavaRuntime? Runtime { get; set; } = Java(17, "jdk-17.0.1");

        public Task<JavaSelectionResult> ResolveAsync(
            GameInstance instance,
            MinecraftVersion? version,
            CancellationToken cancellationToken = default)
        {
            var requirement = new JavaRequirement(17);
            return Task.FromResult(new JavaSelectionResult(
                Runtime,
                requirement,
                CompatibilityCheckEnabled: true,
                Compatibility: JavaCompatibility.Evaluate(requirement, Runtime),
                HasAnyDetectedJava: Runtime is not null,
                ExplicitPath: null,
                IsExplicitOverride: false,
                Warnings: [],
                MemoryMb: 4096));
        }

        public Task<EffectiveJavaRequirements> ResolveRequirementsAsync(
            GameInstance instance,
            MinecraftVersion? version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EffectiveJavaRequirements.FromRequirement(new JavaRequirement(17)));

        private static JavaRuntime Java(int major, string raw) =>
            new(
                $@"C:\Java\{raw}",
                $@"C:\Java\{raw}\bin\java.exe",
                "Eclipse Adoptium",
                "x64",
                new JavaVersion(major, 0, 1, raw));
    }

    private sealed class FakeModService : IModService
    {
        public List<ModEntry> Entries { get; } = [];

        public int ScanCalls { get; private set; }

        public List<(string FileName, bool Enabled)> ToggleCalls { get; } = [];

        public List<string> DeleteCalls { get; } = [];

        public List<IReadOnlyList<string>> ImportCalls { get; } = [];

        public Task<Result<ModScanReport>> ScanAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            ScanCalls++;
            var entries = Entries.ToArray();
            return Task.FromResult(Result<ModScanReport>.Ok(new ModScanReport(
                instanceId,
                "mods",
                entries,
                entries.SelectMany(entry => entry.Issues).ToArray())));
        }

        public Task<Result> SetEnabledAsync(
            string instanceId,
            string fileName,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            ToggleCalls.Add((fileName, enabled));
            return Task.FromResult(Result.Ok());
        }

        public Task<Result<ModBatchResult>> SetAllEnabledAsync(
            string instanceId,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ModBatchResult>.Ok(new ModBatchResult(0, [])));

        public Task<Result<ModBatchResult>> ImportAsync(
            string instanceId,
            IReadOnlyList<string> sourcePaths,
            bool overwrite = false,
            CancellationToken cancellationToken = default)
        {
            ImportCalls.Add(sourcePaths);
            return Task.FromResult(Result<ModBatchResult>.Ok(new ModBatchResult(0, [])));
        }

        public Task<Result> DeleteAsync(
            string instanceId,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add(fileName);
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class FakeContentPackService : IContentPackService
    {
        public int WriteCalls { get; private set; }

        public Task<Result<ContentPackList>> ListAsync(
            string instanceId,
            ContentPackKind kind,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ContentPackList>.Ok(new ContentPackList(
                [],
                [],
                [],
                [],
                PackOptionsStatus.Valid,
                null)));

        public Task<Result<ContentPackEntry>> ImportAsync(
            string instanceId,
            ContentPackKind kind,
            string sourcePath,
            bool overwrite = false,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            throw new NotSupportedException();
        }

        public Task<Result> DeleteAsync(
            string instanceId,
            ContentPackKind kind,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            throw new NotSupportedException();
        }

        public Task<Result> SetEnabledAsync(
            string instanceId,
            ContentPackKind kind,
            string fileName,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            throw new NotSupportedException();
        }
    }

    private sealed class FakeGameSessionService : IGameSessionService
    {
        public List<GameLogSession> Sessions { get; } = [];

        public GameLogSession? Get(string sessionId) =>
            Sessions.FirstOrDefault(session => session.SessionId == sessionId);

        public IReadOnlyList<GameLogSession> List() => Sessions.ToArray();

        public GameLogSession Create(SessionCreateRequest request) => throw new NotSupportedException();

        public void MarkState(string sessionId, GameSessionState state, string? detail = null)
        {
        }

        public void AttachProcess(
            string sessionId,
            int processId,
            DateTimeOffset processStartedAtUtc,
            string executablePath,
            string workingDirectory,
            string? captureHost = null,
            int? captureHostProcessId = null)
        {
        }

        public void RegisterArtifact(string sessionId, SessionArtifact artifact)
        {
        }

        public void SetCaptureFault(string sessionId, string fault)
        {
        }

        public void SetLogIsolation(string sessionId, string mode, string? fingerprint, string? warning)
        {
        }

        public void MarkCaptureHostLost(string sessionId, string reason)
        {
        }

        public void SetExitCode(string sessionId, int exitCode)
        {
        }

        public SessionRecoveryReport Recover() => new(0, 0, 0, 0, []);
    }
}
