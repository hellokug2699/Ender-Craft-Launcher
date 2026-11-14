// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Core.Storage;
using Ecl.Operations;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 设置中心「存储与路径」页（settings-storage-paths.md §2–§10，B3 步骤 6）。
///
/// - 只展示真实存在、由 ECL 消费的路径；不可迁移的路径只读显示，不提供伪编辑控件；
/// - Minecraft 工作区的更改使用 <see cref="IWorkspaceMigrationService"/> 的正式流程：
///   校验目标 → 展示迁移影响 → 明确确认 → 复制/校验/切换（真实阶段与进度）→ 失败恢复；
/// - 源目录永远保留；外部工作区只登记引用，不由本页迁移；
/// - 页内不清理任何文件；存储清理跳转到存储中心（ADR 0022）。
///
/// ECL 数据目录、缓存、日志与 Java 运行时目录都由启动方式或安装位置决定，
/// 当前架构没有独立迁移能力，因此只读展示并在“固定边界”中说明。
/// </summary>
public sealed partial class SettingsStoragePageViewModel : ObservableObject, ISettingsPage
{
    private const string WorkspaceRowKey = "settings.storage.path.workspace";
    private const string DataRowKey = "settings.storage.path.data";
    private const string CacheRowKey = "settings.storage.path.cache";
    private const string DownloadsRowKey = "settings.storage.path.downloads";
    private const string LogsRowKey = "settings.storage.path.logs";
    private const string RuntimesRowKey = "settings.storage.path.runtimes";
    private const string ProgramRowKey = "settings.storage.path.program";
    private const string ChangeKey = "settings.storage.change";
    private const string ManageKey = "settings.storage.manage";

    private readonly IAppPaths _paths;
    private readonly IWorkspaceService _workspace;
    private readonly IWorkspaceMigrationService _migration;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly IOperationStore? _operations;
    private readonly Action<string> _openFolder;

    private string? _plannedTarget;
    private bool _isMigrating;

    public SettingsStoragePageViewModel(
        IAppPaths paths,
        IWorkspaceService workspace,
        IWorkspaceMigrationService migration,
        IDialogService dialogs,
        ILocalizer localizer,
        IOperationStore? operations = null,
        Action<string>? openFolder = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(localizer);

        _paths = paths;
        _workspace = workspace;
        _migration = migration;
        _dialogs = dialogs;
        _localizer = localizer;
        _operations = operations;
        _openFolder = openFolder ?? OpenInFileExplorer;

        Title = localizer.Get("settings.nav.storage");
        Subtitle = localizer.Get("settings.page.storage.subtitle");

        Rows =
        [
            new(WorkspaceRowKey,
                localizer.Get("settings.page.storage.row.workspace.title"),
                localizer.Get("settings.page.storage.row.workspace.description"),
                paths.GameRoot),
            new(DataRowKey,
                localizer.Get("settings.page.storage.row.data.title"),
                localizer.Get("settings.page.storage.row.data.description"),
                paths.Root),
            new(CacheRowKey,
                localizer.Get("settings.page.storage.row.cache.title"),
                localizer.Get("settings.page.storage.row.cache.description"),
                paths.CacheDirectory),
            new(DownloadsRowKey,
                localizer.Get("settings.page.storage.row.downloads.title"),
                localizer.Get("settings.page.storage.row.downloads.description"),
                paths.DownloadsDirectory),
            new(LogsRowKey,
                localizer.Get("settings.page.storage.row.logs.title"),
                localizer.Get("settings.page.storage.row.logs.description"),
                paths.LogsDirectory),
            new(RuntimesRowKey,
                localizer.Get("settings.page.storage.row.runtimes.title"),
                localizer.Get("settings.page.storage.row.runtimes.description"),
                paths.RuntimesDirectory),
            new(ProgramRowKey,
                localizer.Get("settings.page.storage.row.program.title"),
                localizer.Get("settings.page.storage.row.program.description"),
                AppContext.BaseDirectory),
        ];
        WorkspaceRow = Rows[0];

        SearchEntries =
        [
            Entry(WorkspaceRowKey),
            Entry(DataRowKey),
            Entry(CacheRowKey),
            Entry(DownloadsRowKey),
            Entry(LogsRowKey),
            Entry(RuntimesRowKey),
            Entry(ProgramRowKey),
            new SettingsSearchEntry(
                Key,
                Title,
                ChangeKey,
                localizer.Get("settings.page.storage.change.title"),
                localizer.Get("settings.page.storage.change.description")),
            new SettingsSearchEntry(
                Key,
                Title,
                ManageKey,
                localizer.Get("settings.page.storage.manage.title"),
                localizer.Get("settings.page.storage.manage.description")),
        ];

        Boundaries = new SettingsUnavailableSectionViewModel(
            localizer.Get("settings.page.storage.boundaries.title"),
            [
                Boundary("dataDir"),
                Boundary("cache"),
                Boundary("program"),
                Boundary("cleanup"),
                Boundary("external"),
            ]);

        BrowseText = localizer.Get("settings.page.storage.browse");
        CheckText = localizer.Get("settings.page.storage.check");
        StartText = localizer.Get("settings.page.storage.start");
        ResumeText = localizer.Get("settings.page.storage.resume");
        CompleteSwitchText = localizer.Get("settings.page.storage.completeSwitch");
        DiscardText = localizer.Get("settings.page.storage.discard");
        ManageText = localizer.Get("settings.page.storage.manage.command");

        Refresh();
    }

    public string Key => SettingsPageKeys.Storage;

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>路径总览；第一行是 Minecraft 工作区（唯一可迁移项）。</summary>
    public IReadOnlyList<SettingsStoragePathRowViewModel> Rows { get; }

    public SettingsStoragePathRowViewModel WorkspaceRow { get; }

    public IReadOnlyList<SettingsSearchEntry> SearchEntries { get; }

    public SettingsUnavailableSectionViewModel Boundaries { get; }

    /// <summary>本页没有元数据设置项，路径与迁移状态不是可恢复的默认值。</summary>
    public bool CanResetDefaults => false;

    public string BrowseText { get; }

    public string CheckText { get; }

    public string StartText { get; }

    public string ResumeText { get; }

    public string CompleteSwitchText { get; }

    public string DiscardText { get; }

    public string ManageText { get; }

    /// <summary>由 Shell 注入：进入既有存储中心（本页不实现清理）。</summary>
    public Action? OpenStorage { get; set; }

    public bool CanOpenStorage => OpenStorage is not null;

    // ---- 工作区状态 -------------------------------------------------------

    [ObservableProperty]
    private string _workspaceStatusText = string.Empty;

    [ObservableProperty]
    private string _workspaceChangeHint = string.Empty;

    [ObservableProperty]
    private bool _canChangeWorkspace;

    [ObservableProperty]
    private bool _isWorkspaceHighlighted;

    public bool ShowChangeCard => CanChangeWorkspace;

    // ---- 目标与迁移计划 ---------------------------------------------------

    [ObservableProperty]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    private string _planStatusText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasPlan;

    [ObservableProperty]
    private bool _planCanProceed;

    [ObservableProperty]
    private string _planSourceText = string.Empty;

    [ObservableProperty]
    private string _planTargetText = string.Empty;

    [ObservableProperty]
    private string _planInstanceText = string.Empty;

    [ObservableProperty]
    private string _planFileText = string.Empty;

    [ObservableProperty]
    private string _planSizeText = string.Empty;

    [ObservableProperty]
    private string _planFreeSpaceText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _planProblems = [];

    [ObservableProperty]
    private IReadOnlyList<string> _planWarnings = [];

    [ObservableProperty]
    private bool _isChangeHighlighted;

    public bool HasPlanProblems => PlanProblems.Count > 0;

    public bool HasPlanWarnings => PlanWarnings.Count > 0;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>存在可继续/可切换的未完成迁移时，先处理恢复卡，不允许再生成新计划。</summary>
    public bool HasUnresolvedMigration =>
        _migration.Current is { } record && IsResumable(record.State);

    public bool CanCheck => !IsBusy && !HasUnresolvedMigration;

    public bool CanStart => HasPlan
        && PlanCanProceed
        && !HasActiveOperations
        && !IsBusy
        && !HasUnresolvedMigration;

    // ---- 迁移进度与结果 ---------------------------------------------------

    [ObservableProperty]
    private string _migrationStageText = string.Empty;

    [ObservableProperty]
    private double _migrationProgress;

    [ObservableProperty]
    private bool _hasMigrationProgress;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _resultIsSuccess;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsMigrating => _isMigrating;

    // ---- 未完成迁移的恢复 -------------------------------------------------

    [ObservableProperty]
    private bool _hasRecovery;

    [ObservableProperty]
    private string _recoveryStageText = string.Empty;

    [ObservableProperty]
    private string _recoveryDetailsText = string.Empty;

    [ObservableProperty]
    private string _recoveryReasonText = string.Empty;

    [ObservableProperty]
    private bool _canResume;

    [ObservableProperty]
    private bool _canCompleteSwitch;

    [ObservableProperty]
    private bool _canDiscard;

    public bool HasRecoveryReason => !string.IsNullOrEmpty(RecoveryReasonText);

    // ---- 活动任务 ---------------------------------------------------------

    [ObservableProperty]
    private bool _hasActiveOperations;

    [ObservableProperty]
    private string _activeOperationsText = string.Empty;

    public void Activate()
    {
        Refresh();
    }

    public void ResetToDefaults()
    {
        // 路径不是元数据默认值；这里不提供“恢复默认路径”，也不会移动任何文件。
    }

    public bool Highlight(string optionKey)
    {
        ClearHighlights();
        if (string.Equals(optionKey, ChangeKey, StringComparison.Ordinal))
        {
            IsChangeHighlighted = true;
            StatusMessage = _localizer.Get(
                "settings.search.located",
                _localizer.Get("settings.page.storage.change.title"));
            return true;
        }

        if (string.Equals(optionKey, ManageKey, StringComparison.Ordinal))
        {
            StatusMessage = _localizer.Get(
                "settings.search.located",
                _localizer.Get("settings.page.storage.manage.title"));
            return true;
        }

        var row = Rows.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, optionKey, StringComparison.Ordinal));
        if (row is null)
        {
            return false;
        }

        row.IsHighlighted = true;
        if (ReferenceEquals(row, WorkspaceRow))
        {
            IsWorkspaceHighlighted = true;
        }

        StatusMessage = _localizer.Get("settings.search.located", row.Title);
        return true;
    }

    // ---- 命令 -------------------------------------------------------------

    [RelayCommand]
    private void Browse()
    {
        if (!CanChangeWorkspace || IsBusy)
        {
            return;
        }

        var picked = _dialogs.PickFolder(_localizer.Get("settings.page.storage.change.title"));
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        TargetPath = picked;
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsBusy || !CanChangeWorkspace || HasUnresolvedMigration)
        {
            return;
        }

        HasResult = false;
        ErrorText = string.Empty;
        RefreshActiveOperations();
        if (HasActiveOperations)
        {
            PlanStatusText = string.Empty;
            HasPlan = false;
            PlanProblems = [];
            PlanWarnings = [];
            PlanCanProceed = false;
            NotifyPlanState();
            return;
        }

        var target = TargetPath.Trim();
        if (target.Length == 0)
        {
            ErrorText = _localizer.Get("settings.page.storage.error.targetRequired");
            return;
        }

        IsBusy = true;
        PlanStatusText = _localizer.Get("settings.page.storage.checking");
        try
        {
            var planned = await _migration.PlanAsync(target);
            if (!planned.IsSuccess)
            {
                ClearPlan();
                ErrorText = DescribeError(planned.Error);
                return;
            }

            ApplyPlan(planned.Value!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ClearPlan();
            ErrorText = _localizer.Get("settings.page.storage.error.checkFailed", exception.Message);
        }
        finally
        {
            IsBusy = false;
            PlanStatusText = HasPlan
                ? _localizer.Get("settings.page.storage.checkDone")
                : string.Empty;
            NotifyPlanState();
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart)
        {
            return;
        }

        var target = TargetPath.Trim();
        if (string.IsNullOrEmpty(target) || !HasPlan)
        {
            return;
        }

        // 生成计划后可能有新的下载/安装任务开始；复制进行中的文件会得到不一致的结果。
        RefreshActiveOperations();
        if (HasActiveOperations)
        {
            return;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("settings.page.storage.confirm.title"),
                _localizer.Get(
                    "settings.page.storage.confirm.message",
                    PlanTargetText,
                    PlanInstanceText,
                    PlanFileText,
                    PlanSizeText),
                acknowledgementToken: null))
        {
            return;
        }

        await RunMigrationAsync(() => _migration.StartAsync(target, CreateProgress()));
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        if (IsBusy || !CanResume)
        {
            return;
        }

        var target = _migration.Current?.TargetPath ?? string.Empty;
        if (!_dialogs.Confirm(
                _localizer.Get("settings.page.storage.resumeConfirm.title"),
                _localizer.Get("settings.page.storage.resumeConfirm.message", target),
                acknowledgementToken: null))
        {
            return;
        }

        await RunMigrationAsync(() => _migration.ResumeAsync(CreateProgress()));
    }

    [RelayCommand]
    private async Task CompleteSwitchAsync()
    {
        if (IsBusy || !CanCompleteSwitch)
        {
            return;
        }

        var target = _migration.Current?.TargetPath ?? string.Empty;
        if (!_dialogs.Confirm(
                _localizer.Get("settings.page.storage.switchConfirm.title"),
                _localizer.Get("settings.page.storage.switchConfirm.message", target),
                acknowledgementToken: null))
        {
            return;
        }

        ErrorText = string.Empty;
        IsBusy = true;
        HasResult = false;
        try
        {
            var result = await _migration.CompleteSwitchAsync();
            if (result.IsSuccess)
            {
                HasResult = true;
                ResultIsSuccess = true;
                ResultText = _localizer.Get(
                    "settings.page.storage.result.completed",
                    _workspace.Primary?.Path ?? target);
                ClearPlan();
            }
            else
            {
                HasResult = true;
                ResultIsSuccess = false;
                ResultText = _localizer.Get(
                    "settings.page.storage.result.failed",
                    _localizer.Get("settings.page.storage.stage.switching"),
                    DescribeError(result.Error));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            HasResult = true;
            ResultIsSuccess = false;
            ResultText = _localizer.Get(
                "settings.page.storage.result.failed",
                _localizer.Get("settings.page.storage.stage.switching"),
                exception.Message);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (IsBusy || !CanDiscard)
        {
            return;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("settings.page.storage.discardConfirm.title"),
                _localizer.Get("settings.page.storage.discardConfirm.message"),
                acknowledgementToken: null))
        {
            return;
        }

        ErrorText = string.Empty;
        IsBusy = true;
        try
        {
            var result = await _migration.DiscardAsync();
            if (result.IsSuccess)
            {
                StatusMessage = _localizer.Get("settings.page.storage.discarded");
            }
            else
            {
                ErrorText = DescribeError(result.Error);
            }
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void OpenStorageCenter()
    {
        OpenStorage?.Invoke();
    }

    [RelayCommand]
    private void OpenRow(SettingsStoragePathRowViewModel? row)
    {
        if (row is null || !row.CanOpen)
        {
            return;
        }

        try
        {
            _openFolder(row.Path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            StatusMessage = _localizer.Get("settings.page.storage.openFailed", exception.Message);
        }
    }

    // ---- 状态刷新 ---------------------------------------------------------

    /// <summary>只读取路径与状态；不做体积测量（分类切换不得触发有成本的后台任务）。</summary>
    public void Refresh()
    {
        var primary = _workspace.Primary;
        var configured = _workspace.IsConfigured;

        WorkspaceRow.Path = primary?.Path ?? _paths.GameRoot;
        if (primary is null || !configured)
        {
            WorkspaceStatusText = _localizer.Get("settings.page.storage.workspace.notConfigured");
            WorkspaceRow.StatusText = WorkspaceStatusText;
            WorkspaceRow.CanOpen = Directory.Exists(WorkspaceRow.Path);
            CanChangeWorkspace = false;
            WorkspaceChangeHint = _localizer.Get("settings.page.storage.workspace.notConfiguredHint");
        }
        else
        {
            WorkspacePathCheck check;
            try
            {
                check = _workspace.Check(primary.Path, createIfMissing: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                check = new WorkspacePathCheck(
                    primary.Path,
                    false,
                    WorkspaceStatus.Unknown,
                    "workspace.path.checkFailed",
                    exception.Message,
                    null,
                    []);
            }

            WorkspaceStatusText = DescribeCheck(check);
            WorkspaceRow.StatusText = WorkspaceStatusText;
            WorkspaceRow.CanOpen = Directory.Exists(primary.Path);
            CanChangeWorkspace = primary.Kind == WorkspaceKind.Managed;
            WorkspaceChangeHint = CanChangeWorkspace
                ? _localizer.Get("settings.page.storage.workspace.managedHint")
                : _localizer.Get("settings.page.storage.workspace.externalHint");
        }

        UpdateRow(DataRowKey, _paths.Root);
        UpdateRow(CacheRowKey, _paths.CacheDirectory);
        UpdateRow(DownloadsRowKey, _paths.DownloadsDirectory);
        UpdateRow(LogsRowKey, _paths.LogsDirectory);
        UpdateRow(RuntimesRowKey, _paths.RuntimesDirectory);
        UpdateRow(ProgramRowKey, AppContext.BaseDirectory);

        OnPropertyChanged(nameof(ShowChangeCard));
        OnPropertyChanged(nameof(CanOpenStorage));
        RefreshActiveOperations();
        RefreshRecovery();
        NotifyPlanState();
    }

    private void UpdateRow(string key, string path)
    {
        var row = Rows.First(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
        row.Path = path;
        var exists = Directory.Exists(path);
        row.CanOpen = exists;
        row.StatusText = exists
            ? _localizer.Get("settings.page.storage.rowStatus.exists")
            : _localizer.Get("settings.page.storage.rowStatus.missing");
    }

    private string DescribeCheck(WorkspacePathCheck check)
    {
        if (check.IsUsable)
        {
            return check.FreeBytes is { } free
                ? _localizer.Get(
                    "settings.page.storage.workspace.ready",
                    StorageSizeFormatter.FormatBytes(free))
                : _localizer.Get("settings.page.storage.workspace.readyNoSpace");
        }

        return check.Status switch
        {
            WorkspaceStatus.Missing => _localizer.Get("settings.page.storage.workspace.missing"),
            WorkspaceStatus.NotWritable => _localizer.Get("settings.page.storage.workspace.notWritable"),
            WorkspaceStatus.Invalid => _localizer.Get("settings.page.storage.workspace.invalid"),
            _ => _localizer.Get("settings.page.storage.workspace.unavailable"),
        };
    }

    private void RefreshActiveOperations()
    {
        IReadOnlyList<OperationSnapshot> active = _operations is null
            ? []
            : _operations.Snapshots.Where(snapshot => !snapshot.IsTerminal).ToArray();

        HasActiveOperations = active.Count > 0;
        ActiveOperationsText = HasActiveOperations
            ? _localizer.Get(
                "settings.page.storage.activeOperations",
                active.Count,
                string.Join(", ", active.Take(3).Select(snapshot => snapshot.Title)))
            : string.Empty;
        NotifyPlanState();
    }

    private void RefreshRecovery()
    {
        var record = _migration.Current;
        CanResume = false;
        CanCompleteSwitch = false;
        CanDiscard = false;
        RecoveryStageText = string.Empty;
        RecoveryDetailsText = string.Empty;
        RecoveryReasonText = string.Empty;
        HasRecovery = false;

        if (record is null ||
            record.State is WorkspaceMigrationState.Completed or WorkspaceMigrationState.Discarded)
        {
            OnPropertyChanged(nameof(HasRecoveryReason));
            return;
        }

        HasRecovery = true;
        RecoveryStageText = DescribeStage(record.State);
        RecoveryDetailsText = _localizer.Get(
            "settings.page.storage.recovery.details",
            record.SourcePath,
            record.TargetPath,
            record.CopiedFiles,
            record.TotalFiles);
        RecoveryReasonText = DescribeRecordError(record);

        switch (record.State)
        {
            case WorkspaceMigrationState.PendingValidation:
            case WorkspaceMigrationState.Validating:
            case WorkspaceMigrationState.ReadyToCopy:
            case WorkspaceMigrationState.Copying:
            case WorkspaceMigrationState.Verifying:
            case WorkspaceMigrationState.Switching:
            case WorkspaceMigrationState.Interrupted:
                CanResume = true;
                CanDiscard = true;
                break;
            case WorkspaceMigrationState.RollbackRequired:
                CanCompleteSwitch = true;
                CanDiscard = true;
                break;
            case WorkspaceMigrationState.Failed:
                CanDiscard = true;
                break;
        }

        OnPropertyChanged(nameof(HasRecoveryReason));
    }

    // ---- 迁移执行 ---------------------------------------------------------

    private IProgress<WorkspaceMigrationRecord> CreateProgress() =>
        new Progress<WorkspaceMigrationRecord>(OnMigrationProgress);

    private void OnMigrationProgress(WorkspaceMigrationRecord record)
    {
        if (!_isMigrating)
        {
            return;
        }

        MigrationStageText = FormatStage(record);
        if (record.TotalFiles > 0)
        {
            MigrationProgress = Math.Clamp(
                (double)record.CopiedFiles / record.TotalFiles,
                0,
                1);
        }
        else
        {
            MigrationProgress = 0;
        }
    }

    private async Task RunMigrationAsync(Func<Task<Result<WorkspaceMigrationRecord>>> start)
    {
        ErrorText = string.Empty;
        StatusMessage = string.Empty;
        HasResult = false;
        HasPlan = false;
        PlanProblems = [];
        PlanWarnings = [];
        _isMigrating = true;
        OnPropertyChanged(nameof(IsMigrating));
        IsBusy = true;
        IsChangeHighlighted = false;
        HasMigrationProgress = true;
        MigrationProgress = 0;
        MigrationStageText = _localizer.Get("settings.page.storage.stage.starting");

        try
        {
            var outcome = await start();
            var record = _migration.Current;
            if (outcome.IsSuccess)
            {
                HasResult = true;
                ResultIsSuccess = true;
                ResultText = _localizer.Get(
                    "settings.page.storage.result.completed",
                    record?.TargetPath ?? _workspace.Primary?.Path ?? string.Empty);
                TargetPath = string.Empty;
                _plannedTarget = null;
            }
            else
            {
                HasResult = true;
                ResultIsSuccess = false;
                // 迁移可能在建立新记录前就被拒绝（目标被占用等）；此时 Current 仍是上一次的
                // 已完成记录，不能把“已完成”当成这次失败发生的阶段。
                var stage = record is null || record.State is
                    WorkspaceMigrationState.Completed or WorkspaceMigrationState.Discarded
                    ? _localizer.Get("settings.page.storage.stage.starting")
                    : DescribeStage(record.State);
                ResultText = _localizer.Get(
                    "settings.page.storage.result.failed",
                    stage,
                    DescribeError(outcome.Error));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            HasResult = true;
            ResultIsSuccess = false;
            ResultText = _localizer.Get(
                "settings.page.storage.result.failed",
                _localizer.Get("settings.page.storage.stage.copying"),
                exception.Message);
        }
        finally
        {
            _isMigrating = false;
            OnPropertyChanged(nameof(IsMigrating));
            IsBusy = false;
            HasMigrationProgress = false;
            MigrationStageText = string.Empty;
            Refresh();
        }
    }

    // ---- 计划呈现 ---------------------------------------------------------

    private void ApplyPlan(WorkspaceMigrationPlan plan)
    {
        _plannedTarget = plan.TargetPath;
        HasPlan = true;
        PlanCanProceed = plan.CanProceed;
        PlanSourceText = plan.SourcePath;
        PlanTargetText = string.IsNullOrEmpty(plan.TargetPath)
            ? _localizer.Get("settings.page.storage.plan.targetInvalid")
            : plan.TargetPath;
        PlanInstanceText = plan.InstanceCount.ToString();
        PlanFileText = plan.TotalFiles.ToString();
        PlanSizeText = StorageSizeFormatter.FormatBytes(plan.TotalBytes);
        PlanFreeSpaceText = plan.FreeBytes is { } free
            ? StorageSizeFormatter.FormatBytes(free)
            : _localizer.Get("settings.page.storage.plan.freeUnknown");
        PlanProblems = plan.ProblemDetails.Count > 0
            ? plan.ProblemDetails.Select(DescribeProblem).ToArray()
            : plan.Problems.ToArray();
        PlanWarnings = plan.Warnings.Select(DescribeWarningCode).ToArray();
        ErrorText = string.Empty;
        OnPropertyChanged(nameof(HasPlanProblems));
        OnPropertyChanged(nameof(HasPlanWarnings));
        NotifyPlanState();
    }

    private void ClearPlan()
    {
        _plannedTarget = null;
        HasPlan = false;
        PlanCanProceed = false;
        PlanSourceText = string.Empty;
        PlanTargetText = string.Empty;
        PlanInstanceText = string.Empty;
        PlanFileText = string.Empty;
        PlanSizeText = string.Empty;
        PlanFreeSpaceText = string.Empty;
        PlanProblems = [];
        PlanWarnings = [];
        OnPropertyChanged(nameof(HasPlanProblems));
        OnPropertyChanged(nameof(HasPlanWarnings));
        NotifyPlanState();
    }

    private void NotifyPlanState()
    {
        OnPropertyChanged(nameof(HasUnresolvedMigration));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanResume));
    }

    private SettingsSearchEntry Entry(string key)
    {
        var row = Rows.First(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
        return new SettingsSearchEntry(Key, Title, row.Key, row.Title, row.Description);
    }

    private SettingsUnavailableItemViewModel Boundary(string name) => new(
        _localizer.Get($"settings.page.storage.boundary.{name}.title"),
        _localizer.Get($"settings.page.storage.boundary.{name}.reason"));

    // ---- 文案映射 ---------------------------------------------------------

    private static bool IsResumable(WorkspaceMigrationState state) => state is
        WorkspaceMigrationState.PendingValidation or
        WorkspaceMigrationState.Validating or
        WorkspaceMigrationState.ReadyToCopy or
        WorkspaceMigrationState.Copying or
        WorkspaceMigrationState.Verifying or
        WorkspaceMigrationState.Switching or
        WorkspaceMigrationState.Interrupted or
        WorkspaceMigrationState.RollbackRequired;

    private string DescribeStage(WorkspaceMigrationState state) => _localizer.Get(state switch
    {
        WorkspaceMigrationState.PendingValidation => "settings.page.storage.stage.validating",
        WorkspaceMigrationState.Validating => "settings.page.storage.stage.validating",
        WorkspaceMigrationState.ReadyToCopy => "settings.page.storage.stage.ready",
        WorkspaceMigrationState.Copying => "settings.page.storage.stage.copying",
        WorkspaceMigrationState.Verifying => "settings.page.storage.stage.verifying",
        WorkspaceMigrationState.Switching => "settings.page.storage.stage.switching",
        WorkspaceMigrationState.Completed => "settings.page.storage.stage.completed",
        WorkspaceMigrationState.Interrupted => "settings.page.storage.stage.interrupted",
        WorkspaceMigrationState.Failed => "settings.page.storage.stage.failed",
        WorkspaceMigrationState.RollbackRequired => "settings.page.storage.stage.switchFailed",
        _ => "settings.page.storage.stage.discarded",
    });

    private string FormatStage(WorkspaceMigrationRecord record) => record.State switch
    {
        WorkspaceMigrationState.Copying => _localizer.Get(
            "settings.page.storage.stage.copyingCount",
            record.CopiedFiles,
            record.TotalFiles),
        WorkspaceMigrationState.Verifying => _localizer.Get(
            "settings.page.storage.stage.verifyingCount",
            record.CopiedFiles,
            record.TotalFiles),
        _ => DescribeStage(record.State),
    };

    private string DescribeProblem(WorkspaceMigrationProblem problem) => problem.Code switch
    {
        "workspace.migration.source_unavailable" => _localizer.Get("settings.page.storage.problem.sourceUnavailable"),
        "workspace.migration.external_workspace" => _localizer.Get("settings.page.storage.problem.external"),
        "workspace.migration.target_invalid" => _localizer.Get("settings.page.storage.problem.targetInvalid"),
        "workspace.migration.overlap" => _localizer.Get("settings.page.storage.problem.overlap"),
        "workspace.migration.target_not_empty" => _localizer.Get("settings.page.storage.problem.targetNotEmpty"),
        "workspace.migration.target_unavailable" => _localizer.Get("settings.page.storage.problem.targetUnavailable"),
        "workspace.migration.game_running" => _localizer.Get("settings.page.storage.problem.gameRunning"),
        "workspace.migration.source_unreadable" => _localizer.Get("settings.page.storage.problem.sourceUnreadable"),
        "workspace.migration.insufficient_space" => _localizer.Get(
            "settings.page.storage.problem.insufficientSpace",
            problem.Arguments.Count > 0 ? problem.Arguments[0] : string.Empty,
            problem.Arguments.Count > 1 ? problem.Arguments[1] : string.Empty),
        _ => problem.Message,
    };

    private string DescribeWarningCode(string code)
    {
        var localized = _localizer.Get(code);
        return string.Equals(localized, code, StringComparison.Ordinal) ? code : localized;
    }

    private string DescribeError(Ecl.Core.EclError? error)
    {
        if (error is null)
        {
            return _localizer.Get("settings.page.storage.error.unknown");
        }

        var key = error.Code switch
        {
            "workspace.migration.not_configured" => "settings.page.storage.error.notConfigured",
            "workspace.migration.none" => "settings.page.storage.error.noRecord",
            "workspace.migration.unresumable" => "settings.page.storage.error.unresumable",
            "workspace.migration.not_switchable" => "settings.page.storage.error.notSwitchable",
            "workspace.migration.target_missing" => "settings.page.storage.error.targetMissing",
            "workspace.migration.blocked" => "settings.page.storage.error.blocked",
            "workspace.migration.io" => "settings.page.storage.error.io",
            "workspace.migration.source_unavailable" => "settings.page.storage.problem.sourceUnavailable",
            "workspace.migration.external_workspace" => "settings.page.storage.problem.external",
            "workspace.migration.target_invalid" => "settings.page.storage.problem.targetInvalid",
            "workspace.migration.overlap" => "settings.page.storage.problem.overlap",
            "workspace.migration.target_not_empty" => "settings.page.storage.problem.targetNotEmpty",
            "workspace.migration.target_unavailable" => "settings.page.storage.problem.targetUnavailable",
            "workspace.migration.game_running" => "settings.page.storage.problem.gameRunning",
            "workspace.migration.source_unreadable" => "settings.page.storage.problem.sourceUnreadable",
            "workspace.path.empty" => "settings.page.storage.problem.targetInvalid",
            "workspace.path.relative" => "settings.page.storage.problem.targetInvalid",
            "workspace.path.invalid" => "settings.page.storage.problem.targetInvalid",
            "workspace.path.protected" => "settings.page.storage.problem.targetProtected",
            "workspace.path.driveMissing" => "settings.page.storage.problem.targetUnavailable",
            "workspace.path.notDirectory" => "settings.page.storage.problem.targetNotDirectory",
            "workspace.path.notWritable" => "settings.page.storage.problem.targetUnavailable",
            "workspace.path.createFailed" => "settings.page.storage.problem.targetUnavailable",
            _ => null,
        };

        return key is null
            ? error.Message
            : _localizer.Get(key);
    }

    private string DescribeRecordError(WorkspaceMigrationRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.ErrorCode))
        {
            var localized = DescribeError(new Ecl.Core.EclError(
                record.ErrorCode,
                record.ErrorMessage ?? string.Empty));
            if (!string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }
        }

        return record.ErrorMessage ?? string.Empty;
    }

    private void ClearHighlights()
    {
        foreach (var row in Rows)
        {
            row.IsHighlighted = false;
        }

        IsChangeHighlighted = false;
        IsWorkspaceHighlighted = false;
    }

    private static void OpenInFileExplorer(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    partial void OnTargetPathChanged(string value)
    {
        if (!HasPlan)
        {
            return;
        }

        if (!string.Equals(value.Trim(), _plannedTarget, StringComparison.OrdinalIgnoreCase))
        {
            ClearPlan();
        }
    }

    partial void OnHasPlanChanged(bool value) => NotifyPlanState();

    partial void OnPlanCanProceedChanged(bool value) => NotifyPlanState();

    partial void OnHasActiveOperationsChanged(bool value) => NotifyPlanState();

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanStart));
    }

    partial void OnPlanProblemsChanged(IReadOnlyList<string> value) =>
        OnPropertyChanged(nameof(HasPlanProblems));

    partial void OnPlanWarningsChanged(IReadOnlyList<string> value) =>
        OnPropertyChanged(nameof(HasPlanWarnings));

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnRecoveryReasonTextChanged(string value) =>
        OnPropertyChanged(nameof(HasRecoveryReason));
}
