// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using System.Text.Json;
using Ecl.App.Services;
using Ecl.App.ViewModels.Settings;
using Ecl.Core;
using Ecl.Core.Storage;
using Ecl.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Settings;

/// <summary>
/// 设置中心「存储与路径」页（settings-storage-paths.md，B3 步骤 6）：
/// 只展示真实路径、工作区迁移的预览/确认/真实进度/失败恢复、活动任务阻断、
/// 外部与未配置工作区的真实不可用状态，以及“管理存储空间”入口的接线。
/// </summary>
public sealed class StoragePathsPageTests
{
    [Fact]
    public void PageListsOnlyRealPathsAndTheWorkspaceIsTheOnlyMigratableOne()
    {
        using var host = new StorageHost();

        Assert.Equal(
            [
                "settings.storage.path.workspace",
                "settings.storage.path.data",
                "settings.storage.path.cache",
                "settings.storage.path.downloads",
                "settings.storage.path.logs",
                "settings.storage.path.runtimes",
                "settings.storage.path.program",
            ],
            host.Page.Rows.Select(row => row.Key));
        Assert.Equal(host.GameRoot, host.Page.WorkspaceRow.Path);
        Assert.Equal(host.DataRoot, host.Page.Rows[1].Path);
        Assert.True(host.Page.CanChangeWorkspace);
        Assert.True(host.Page.ShowChangeCard);
        Assert.Contains("可用", host.Page.WorkspaceStatusText, StringComparison.Ordinal);
        Assert.True(host.Page.WorkspaceRow.CanOpen);
        Assert.All(host.Page.Rows, row => Assert.False(row.IsHighlighted));

        // 未生成计划前不显示迁移预览，也不允许开始。
        Assert.False(host.Page.HasPlan);
        Assert.False(host.Page.CanStart);
        Assert.False(host.Page.HasRecovery);
    }

    [Fact]
    public void UnconfiguredWorkspaceIsReportedAsUnavailableWithoutFakeEditors()
    {
        using var host = new StorageHost(workspaceConfigured: false);

        Assert.False(host.Page.CanChangeWorkspace);
        Assert.False(host.Page.ShowChangeCard);
        Assert.Contains("尚未设置", host.Page.WorkspaceStatusText, StringComparison.Ordinal);
        Assert.False(host.Page.CanStart);
    }

    [Fact]
    public async Task PlanShowsRealImpactAndBlocksAnOccupiedTarget()
    {
        using var host = new StorageHost();
        host.Page.TargetPath = host.TargetRoot;

        await host.Page.CheckCommand.ExecuteAsync(null);

        Assert.True(host.Page.HasPlan);
        Assert.True(host.Page.PlanCanProceed);
        Assert.True(host.Page.CanStart);
        Assert.Equal(host.GameRoot, host.Page.PlanSourceText);
        Assert.Equal(host.TargetRoot, host.Page.PlanTargetText);
        Assert.Equal("1", host.Page.PlanInstanceText);
        Assert.True(int.Parse(host.Page.PlanFileText) >= 4);
        Assert.NotEqual("未知", host.Page.PlanSizeText);
        Assert.Empty(host.Page.PlanProblems);
        Assert.False(host.Page.HasPlanProblems);

        // 目标被占用后重新检查：显示真实阻塞原因，开始按钮不可用。
        Directory.CreateDirectory(host.TargetRoot);
        await File.WriteAllTextAsync(Path.Combine(host.TargetRoot, "keep.txt"), "keep");
        await host.Page.CheckCommand.ExecuteAsync(null);

        Assert.True(host.Page.HasPlan);
        Assert.False(host.Page.PlanCanProceed);
        Assert.False(host.Page.CanStart);
        Assert.Contains("不为空", Assert.Single(host.Page.PlanProblems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingTheTargetInvalidatesThePlanAndRequiresANewCheck()
    {
        using var host = new StorageHost();
        host.Page.TargetPath = host.TargetRoot;
        await host.Page.CheckCommand.ExecuteAsync(null);
        Assert.True(host.Page.CanStart);

        host.Page.TargetPath = Path.Combine(host.Root, "another-target");

        Assert.False(host.Page.HasPlan);
        Assert.False(host.Page.CanStart);
    }

    [Fact]
    public async Task EmptyTargetIsRejectedWithoutPlan()
    {
        using var host = new StorageHost();

        await host.Page.CheckCommand.ExecuteAsync(null);

        Assert.True(host.Page.HasError);
        Assert.False(host.Page.HasPlan);
        Assert.Contains("目标位置", host.Page.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsksForConfirmationThenMigratesAndKeepsTheSource()
    {
        using var host = new StorageHost();
        host.Page.TargetPath = host.TargetRoot;
        await host.Page.CheckCommand.ExecuteAsync(null);

        await host.Page.StartCommand.ExecuteAsync(null);

        Assert.Single(host.Dialogs.ConfirmMessages);
        Assert.Contains(host.TargetRoot, host.Dialogs.ConfirmMessages[0], StringComparison.Ordinal);
        Assert.True(host.Page.HasResult);
        Assert.True(host.Page.ResultIsSuccess);
        Assert.Contains(host.TargetRoot, host.Page.ResultText, StringComparison.Ordinal);
        Assert.Equal(host.TargetRoot, host.Workspace.Primary!.Path);
        Assert.Equal(WorkspaceMigrationState.Completed, host.Migration.Current!.State);
        Assert.True(Directory.Exists(host.GameRoot));
        Assert.True(File.Exists(Path.Combine(host.TargetRoot, "instances", "demo-abc123", "minecraft", "options.txt")));
        Assert.Equal(host.TargetRoot, host.Page.WorkspaceRow.Path);
        Assert.False(host.Page.HasRecovery);
        Assert.Empty(host.Page.TargetPath);
        Assert.False(host.Page.IsMigrating);
    }

    [Fact]
    public async Task CancellingTheConfirmationWritesNothing()
    {
        using var host = new StorageHost();
        host.Dialogs.ConfirmResult = false;
        host.Page.TargetPath = host.TargetRoot;
        await host.Page.CheckCommand.ExecuteAsync(null);

        await host.Page.StartCommand.ExecuteAsync(null);

        Assert.Equal(host.GameRoot, host.Workspace.Primary!.Path);
        Assert.Null(host.Migration.Current);
        Assert.False(Directory.Exists(host.TargetRoot));
        Assert.False(host.Page.HasResult);
    }

    [Fact]
    public async Task StartRechecksConditionsAndReportsFailureInsteadOfPretendingSuccess()
    {
        using var host = new StorageHost();
        host.Page.TargetPath = host.TargetRoot;
        await host.Page.CheckCommand.ExecuteAsync(null);
        Assert.True(host.Page.CanStart);

        // 计划生成后目标被占用：领域重新校验后拒绝，页面必须显示真实失败。
        Directory.CreateDirectory(host.TargetRoot);
        await File.WriteAllTextAsync(Path.Combine(host.TargetRoot, "keep.txt"), "keep");

        await host.Page.StartCommand.ExecuteAsync(null);

        Assert.True(host.Page.HasResult);
        Assert.False(host.Page.ResultIsSuccess);
        Assert.Contains("迁移条件不满足", host.Page.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("已完成", host.Page.ResultText, StringComparison.Ordinal);
        Assert.Equal(host.GameRoot, host.Workspace.Primary!.Path);
        Assert.True(File.Exists(Path.Combine(host.TargetRoot, "keep.txt")));
    }

    [Fact]
    public async Task FailureAfterACompletedMigrationDoesNotReuseTheOldStage()
    {
        using var host = new StorageHost();
        host.Page.TargetPath = host.TargetRoot;
        await host.Page.CheckCommand.ExecuteAsync(null);
        await host.Page.StartCommand.ExecuteAsync(null);
        Assert.True(host.Page.ResultIsSuccess);

        // 第二次迁移：计划仍可执行，但开始前目标被占用，领域拒绝且不建立新记录。
        var second = Path.Combine(host.Root, "games-moved-2");
        host.Page.TargetPath = second;
        await host.Page.CheckCommand.ExecuteAsync(null);
        Assert.True(host.Page.CanStart);

        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(second, "keep.txt"), "keep");
        await host.Page.StartCommand.ExecuteAsync(null);

        Assert.False(host.Page.ResultIsSuccess);
        Assert.Contains("正在准备迁移", host.Page.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("已完成", host.Page.ResultText, StringComparison.Ordinal);
        Assert.Equal(host.TargetRoot, host.Workspace.Primary!.Path);
    }

    [Fact]
    public async Task InterruptedMigrationOffersResumeAndCompletesIt()
    {
        using var host = new StorageHost();
        host.WriteRecord(WorkspaceMigrationState.Interrupted, staging: true);

        // 重新构造页面，让恢复卡从持久化记录读取。
        host.RecreatePage();

        Assert.True(host.Page.HasRecovery);
        Assert.True(host.Page.CanResume);
        Assert.True(host.Page.CanDiscard);
        Assert.False(host.Page.CanCompleteSwitch);
        Assert.Equal("已中断", host.Page.RecoveryStageText);

        await host.Page.ResumeCommand.ExecuteAsync(null);

        Assert.Single(host.Dialogs.ConfirmMessages);
        Assert.Equal(WorkspaceMigrationState.Completed, host.Migration.Current!.State);
        Assert.Equal(host.TargetRoot, host.Workspace.Primary!.Path);
        Assert.True(host.Page.HasResult);
        Assert.True(host.Page.ResultIsSuccess);
        Assert.False(host.Page.HasRecovery);
        Assert.True(Directory.Exists(host.GameRoot));
    }

    [Fact]
    public async Task InterruptedMigrationCanBeDiscardedWithoutTouchingTheSource()
    {
        using var host = new StorageHost();
        host.WriteRecord(WorkspaceMigrationState.Copying, staging: true);
        host.RecreatePage();

        Assert.True(host.Page.CanDiscard);

        await host.Page.DiscardCommand.ExecuteAsync(null);

        Assert.Equal(WorkspaceMigrationState.Discarded, host.Migration.Current!.State);
        Assert.False(Directory.Exists(host.TargetRoot + ".ecl-staging"));
        Assert.True(Directory.Exists(host.GameRoot));
        Assert.Equal(host.GameRoot, host.Workspace.Primary!.Path);
        Assert.False(host.Page.HasRecovery);
        Assert.Contains("已放弃", host.Page.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchFailedRecordOffersOnlySwitchingAndCompletesIt()
    {
        using var host = new StorageHost();
        host.WriteRecord(WorkspaceMigrationState.RollbackRequired, copyTarget: true);
        host.RecreatePage();

        Assert.True(host.Page.HasRecovery);
        Assert.True(host.Page.CanCompleteSwitch);
        Assert.False(host.Page.CanResume);
        Assert.Contains("切换工作区失败", host.Page.RecoveryStageText, StringComparison.Ordinal);

        // 未完成的迁移必须先处理；不能悄悄开始一次新的迁移。
        Assert.True(host.Page.HasUnresolvedMigration);
        Assert.False(host.Page.CanCheck);
        host.Page.TargetPath = Path.Combine(host.Root, "another-target");
        await host.Page.CheckCommand.ExecuteAsync(null);
        Assert.False(host.Page.HasPlan);

        await host.Page.CompleteSwitchCommand.ExecuteAsync(null);

        Assert.Single(host.Dialogs.ConfirmMessages);
        Assert.Equal(WorkspaceMigrationState.Completed, host.Migration.Current!.State);
        Assert.Equal(host.TargetRoot, host.Workspace.Primary!.Path);
        Assert.True(host.Page.ResultIsSuccess);
        Assert.False(host.Page.HasRecovery);
        Assert.True(Directory.Exists(host.GameRoot));
    }

    [Fact]
    public async Task FailedRecordIsRecoverableOnlyByDiscarding()
    {
        using var host = new StorageHost();
        host.WriteRecord(WorkspaceMigrationState.Failed, staging: true);
        host.RecreatePage();

        Assert.True(host.Page.HasRecovery);
        Assert.False(host.Page.CanResume);
        Assert.False(host.Page.CanCompleteSwitch);
        Assert.True(host.Page.CanDiscard);
        Assert.Contains("失败", host.Page.RecoveryStageText, StringComparison.Ordinal);

        await host.Page.DiscardCommand.ExecuteAsync(null);

        Assert.Equal(WorkspaceMigrationState.Discarded, host.Migration.Current!.State);
    }

    [Fact]
    public async Task ActiveOperationsBlockPlanningAndStarting()
    {
        using var host = new StorageHost();
        host.Operations.AddRunning("正在下载 Sodium");

        host.Page.TargetPath = host.TargetRoot;
        await host.Page.CheckCommand.ExecuteAsync(null);

        Assert.True(host.Page.HasActiveOperations);
        Assert.Contains("正在下载 Sodium", host.Page.ActiveOperationsText, StringComparison.Ordinal);
        Assert.False(host.Page.HasPlan);
        Assert.False(host.Page.CanStart);
    }

    [Fact]
    public void OpenStorageEntryIsWiredByTheHostAndDisabledWithoutACallback()
    {
        using var host = new StorageHost();
        var opened = 0;

        Assert.False(host.Page.CanOpenStorage);
        host.Page.OpenStorageCenterCommand.Execute(null);
        Assert.Equal(0, opened);

        host.Page.OpenStorage = () => opened++;
        Assert.True(host.Page.CanOpenStorage);

        host.Page.OpenStorageCenterCommand.Execute(null);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void SearchLocatesRowsTheChangeCardAndTheStorageEntry()
    {
        using var host = new StorageHost();

        Assert.Contains(
            host.Page.SearchEntries,
            entry => entry.OptionKey == "settings.storage.change");
        Assert.Contains(
            host.Page.SearchEntries,
            entry => entry.OptionKey == "settings.storage.manage");

        Assert.True(host.Page.Highlight("settings.storage.path.workspace"));
        Assert.True(host.Page.WorkspaceRow.IsHighlighted);
        Assert.True(host.Page.IsWorkspaceHighlighted);
        Assert.Contains("已定位到", host.Page.StatusMessage, StringComparison.Ordinal);

        Assert.True(host.Page.Highlight("settings.storage.change"));
        Assert.True(host.Page.IsChangeHighlighted);
        Assert.False(host.Page.WorkspaceRow.IsHighlighted);

        Assert.False(host.Page.Highlight("settings.storage.unknown"));
        Assert.False(host.Page.IsChangeHighlighted);
    }

    [Fact]
    public void OpenRowUsesTheInjectedOpenerAndReportsFailure()
    {
        using var host = new StorageHost();
        var opened = new List<string>();
        host.Page = host.CreatePage(opened.Add);

        host.Page.OpenRowCommand.Execute(host.Page.WorkspaceRow);
        Assert.Equal([host.GameRoot], opened);

        host.Page = host.CreatePage(_ => throw new IOException("模拟打开失败。"));
        host.Page.OpenRowCommand.Execute(host.Page.WorkspaceRow);
        Assert.Contains("无法打开该位置", host.Page.StatusMessage, StringComparison.Ordinal);
    }

    private sealed class StorageHost : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly SynchronizationContext? _previous;

        public StorageHost(bool workspaceConfigured = true)
        {
            // 让 Progress<T> 同步回调：迁移阶段在测试中可确定地观察。
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new ImmediateSynchronizationContext());

            Root = Path.Combine(Path.GetTempPath(), "ecl-storage-tests", Guid.NewGuid().ToString("N"));
            DataRoot = Path.Combine(Root, "data");
            GameRoot = Path.Combine(Root, "games");
            TargetRoot = Path.Combine(Root, "games-moved");

            Paths = new AppPaths(DataRoot);
            Paths.EnsureCreated();

            Workspace = new WorkspaceService(Paths, NullLogger<WorkspaceService>.Instance);
            if (workspaceConfigured)
            {
                Assert.True(Workspace.InitializeAsync(GameRoot).GetAwaiter().GetResult().IsSuccess);
                SeedWorkspace();
            }

            Migration = new WorkspaceMigrationService(
                Paths,
                Workspace,
                NullLogger<WorkspaceMigrationService>.Instance);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEclCore(Paths, CultureInfo.GetCultureInfo("zh-CN"));
            _provider = services.BuildServiceProvider();
            Localizer = _provider.GetRequiredService<Ecl.Core.Localization.ILocalizer>();
            Dialogs = new FakeDialogService();
            Operations = new FakeOperationStore();
            Page = CreatePage();
        }

        public string Root { get; }

        public string DataRoot { get; }

        public string GameRoot { get; }

        public string TargetRoot { get; }

        public AppPaths Paths { get; }

        public WorkspaceService Workspace { get; }

        public WorkspaceMigrationService Migration { get; private set; }

        public Ecl.Core.Localization.ILocalizer Localizer { get; }

        public FakeDialogService Dialogs { get; }

        public FakeOperationStore Operations { get; }

        public SettingsStoragePageViewModel Page { get; set; }

        public SettingsStoragePageViewModel CreatePage(Action<string>? openFolder = null) => new(
            Paths,
            Workspace,
            Migration,
            Dialogs,
            Localizer,
            Operations,
            openFolder ?? (_ => { }));

        public void RecreatePage() => Page = CreatePage();

        public void SeedWorkspace()
        {
            var game = Path.Combine(GameRoot, "instances", "demo-abc123", "minecraft");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "options.txt"), "version:1\n");
            Directory.CreateDirectory(Path.Combine(game, "saves", "world"));
            File.WriteAllText(Path.Combine(game, "saves", "world", "level.dat"), "level");
            Directory.CreateDirectory(Path.Combine(GameRoot, "versions", "1.21.1"));
            File.WriteAllText(Path.Combine(GameRoot, "versions", "1.21.1", "1.21.1.json"), "{}");
            Directory.CreateDirectory(Path.Combine(GameRoot, "libraries", "org", "example"));
            File.WriteAllBytes(Path.Combine(GameRoot, "libraries", "org", "example", "a.jar"), new byte[2048]);
        }

        public void WriteRecord(
            WorkspaceMigrationState state,
            bool staging = false,
            bool copyTarget = false)
        {
            var stagingPath = TargetRoot + ".ecl-staging";
            if (staging)
            {
                Directory.CreateDirectory(stagingPath);
                File.WriteAllText(Path.Combine(stagingPath, "partial.txt"), "partial");
            }

            if (copyTarget)
            {
                CopyTree(GameRoot, TargetRoot);
            }

            var record = new WorkspaceMigrationRecord
            {
                Id = "test-record",
                SourcePath = GameRoot,
                TargetPath = TargetRoot,
                StagingPath = stagingPath,
                State = state,
                TotalFiles = 5,
                CopiedFiles = state == WorkspaceMigrationState.Interrupted ? 2 : 5,
                ErrorCode = state switch
                {
                    WorkspaceMigrationState.Failed => "workspace.migration.io",
                    WorkspaceMigrationState.RollbackRequired => "workspace.path.notWritable",
                    _ => null,
                },
                ErrorMessage = state switch
                {
                    WorkspaceMigrationState.Failed => "校验失败。",
                    WorkspaceMigrationState.RollbackRequired => "无法写回工作区配置。",
                    _ => null,
                },
            };
            File.WriteAllText(
                Path.Combine(Paths.ConfigDirectory, "workspace-migration.json"),
                JsonSerializer.Serialize(record));

            // 让新构造的迁移服务读取记录（模拟上次运行留下的状态）。
            Migration = new WorkspaceMigrationService(
                Paths,
                Workspace,
                NullLogger<WorkspaceMigrationService>.Instance);
        }

        private static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }

        public void Dispose()
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
            _provider.Dispose();
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 测试清理失败不影响测试结论。
            }
        }
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class FakeOperationStore : IOperationStore
    {
        private readonly List<OperationSnapshot> _items = [];

        public IReadOnlyList<OperationSnapshot> Snapshots => _items;

        public event EventHandler<OperationSnapshot>? Changed
        {
            add { }
            remove { }
        }

        public OperationSnapshot? TryGet(Guid operationId) =>
            _items.FirstOrDefault(item => item.Id == operationId);

        public void AddRunning(string title) => _items.Add(new OperationSnapshot
        {
            Id = Guid.NewGuid(),
            Kind = OperationKind.Download,
            Title = title,
            State = OperationState.Running,
        });
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public List<string> ConfirmMessages { get; } = [];

        public bool Confirm(string title, string message, string? acknowledgementToken = null)
        {
            ConfirmMessages.Add(message);
            return ConfirmResult;
        }

        public InstanceDeletionChoice ChooseInstanceDeletion(
            string title,
            string message,
            bool canDeleteManagedData) => InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowError(string title, string message)
        {
        }

        public string? PickOpenFile(string title, string filter) => null;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}
