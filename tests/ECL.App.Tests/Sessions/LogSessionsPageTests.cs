// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Dialogs;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.Core;
using Ecl.Core.Diagnostics;
using Ecl.Core.Localization;
using Ecl.Operations;
using Ecl.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Sessions;

/// <summary>
/// 日志工作区（logs.md）的页面行为：会话身份、目标会话不可用、实例筛选、
/// 来源分离、搜索/过滤与暂停跟随的新行提示。文件读取使用真实会话服务与临时目录。
/// </summary>
public sealed class LogSessionsPageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ecl-log-page-tests",
        Guid.NewGuid().ToString("N"));

    private readonly AppPaths _paths;
    private readonly GameSessionService _sessions;
    private readonly OperationService _operations = new();

    public LogSessionsPageTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _sessions = new GameSessionService(_paths, NullLogger<GameSessionService>.Instance);
    }

    [Fact]
    public async Task ActivateLoadsLatestSessionAndSplitsSources()
    {
        using var ui = new UiThread();
        var older = CreateSession("older", "1.20.1");
        File.WriteAllText(Path.Combine(older.DirectoryPath, "game.log"), "[10:00:00] [main/INFO]: old\n");
        var newer = CreateSession("newer", "1.21.1");
        File.WriteAllText(Path.Combine(newer.DirectoryPath, "game.log"), "[11:00:00] [main/INFO]: new\n");
        File.WriteAllText(newer.StdoutPath, "stdout-only\n");

        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        Assert.Equal(2, vm.Sessions.Count);
        Assert.Equal(
            vm.Sessions.OrderByDescending(item => item.CreatedAtUtc).First().Id,
            vm.SelectedSession!.Id);
        Assert.Equal(4, vm.Sources.Count);
        Assert.Contains(vm.Lines, line => line.Message.Contains("new", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.Lines, line => line.Message.Contains("stdout-only", StringComparison.Ordinal));

        vm.SelectedSource = vm.Sources.Single(source => source.Kind == SessionLogSourceKind.Stdout);
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();
        Assert.Contains(vm.Lines, line => line.Message.Contains("stdout-only", StringComparison.Ordinal));

        ui.Invoke(() => vm.Dispose());
    }

    [Fact]
    public async Task FocusUnknownSessionKeepsBannerAndDoesNotSelectAnother()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        File.WriteAllText(Path.Combine(session.DirectoryPath, "game.log"), "hello\n");
        var vm = await ui.InvokeAsync(() => CreateViewModel());

        ui.Invoke(() => vm.FocusSession("missing-session"));
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        Assert.Null(vm.SelectedSession);
        Assert.True(vm.HasTargetUnavailable);

        ui.Invoke(() => vm.SelectedSession = vm.Sessions.Single());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();
        Assert.False(vm.HasTargetUnavailable);

        ui.Invoke(() => vm.Dispose());
    }

    [Fact]
    public async Task FocusInstanceFiltersSessionsAndClearRestoresAll()
    {
        using var ui = new UiThread();
        CreateSession("alpha", "1.21.1");
        CreateSession("beta", "1.20.1");
        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());

        var filteredCount = 0;
        var restoredCount = 0;
        var focusedInstance = string.Empty;
        var hadFocus = false;
        var focusCleared = false;
        ui.Invoke(() =>
        {
            vm.FocusInstance("alpha", "Alpha");
            hadFocus = vm.HasInstanceFocus;
            filteredCount = vm.SessionsView.Cast<object>().Count();
            focusedInstance = vm.SelectedSession!.Manifest.InstanceId ?? string.Empty;

            vm.ClearInstanceFocus();
            focusCleared = !vm.HasInstanceFocus;
            restoredCount = vm.SessionsView.Cast<object>().Count();
        });

        Assert.True(hadFocus);
        Assert.Equal(1, filteredCount);
        Assert.Equal("alpha", focusedInstance);
        Assert.True(focusCleared);
        Assert.Equal(2, restoredCount);

        ui.Invoke(() => vm.Dispose());
    }

    [Fact]
    public async Task SearchAndLevelFilterAreViewOnlyAndHonest()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        File.WriteAllText(
            Path.Combine(session.DirectoryPath, "game.log"),
            "[10:00:00] [main/INFO]: alpha needle\n" +
            "[10:00:01] [main/WARN]: beta needle\n" +
            "[10:00:02] [main/ERROR]: gamma needle\n" +
            "unparsed needle line\n");
        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        ui.Invoke(() =>
        {
            vm.SearchText = "needle";
            vm.SearchCommand.Execute(null);
        });

        Assert.Contains("4", vm.SearchStatus);
        Assert.Equal(4, vm.Lines.Count(line => line.IsMatch));

        ui.Invoke(() => vm.FilterInfo = false);
        Assert.DoesNotContain(vm.Lines, line => line.Message.Contains("alpha", StringComparison.Ordinal));
        // 无法解析级别的行永远不会被级别过滤丢弃。
        Assert.Contains(vm.Lines, line => !line.IsParsed);

        ui.Invoke(() => vm.Dispose());
    }

    [Fact]
    public async Task PausedFollowReportsNewLinesAndResumeDoesNotSkipThem()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        var path = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllText(path, "one\n");
        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() =>
        {
            vm.Activate();
            vm.Follow = false;
        });
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        File.AppendAllText(path, "two\n");
        await ui.InvokeAsync(() => vm.PollAsync()).Unwrap();

        Assert.True(vm.HasNewLines);
        Assert.Contains(vm.Lines, line => line.Message == "two");

        ui.Invoke(() => vm.ToggleFollowCommand.Execute(null));
        Assert.False(vm.HasNewLines);
        Assert.True(vm.Follow);

        ui.Invoke(() => vm.Dispose());
    }

    [Fact]
    public async Task EvidenceOwnershipIsLocalizedAndNotClaimedAsDirect()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        var report = Path.Combine(session.DirectoryPath, "artifacts", "crash.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, "crash");
        _sessions.RegisterArtifact(session.SessionId, new SessionArtifact
        {
            Id = "client_crash_report:crash.txt",
            Kind = SessionArtifactKinds.ClientCrashReport,
            SourcePath = report,
            CapturedPath = report,
            Ownership = ArtifactOwnership.SharedOrAmbiguous,
        });

        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        var evidence = Assert.Single(vm.Evidence);
        Assert.Equal("共享或歧义", evidence.OwnershipText);
        Assert.Equal(ArtifactOwnership.SharedOrAmbiguous, evidence.Artifact.Ownership);

        ui.Invoke(() => vm.Dispose());
    }

    private GameLogSession CreateSession(string instanceId, string version) =>
        _sessions.Create(new SessionCreateRequest
        {
            InstanceId = instanceId,
            InstanceName = char.ToUpperInvariant(instanceId[0]) + instanceId[1..],
            GameDirectory = Path.Combine(_root, "games", instanceId),
            MinecraftVersion = version,
            Loader = "fabric",
            LogIsolation = "direct",
        });

    private LogSessionsViewModel CreateViewModel() =>
        new(
            _sessions,
            new SessionLogReader(),
            new SessionExportService(_sessions, _paths),
            new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")),
            new TestMemoryPressureMonitor(),
            _operations,
            new UnavailableDialogEngine(),
            new UnavailableDialogService(),
            _paths,
            NullLogger<LogSessionsViewModel>.Instance);

    public void Dispose()
    {
        _operations.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响断言。
        }
    }

    private sealed class TestMemoryPressureMonitor : IMemoryPressureMonitor
    {
        public MemoryPressureLevel Level => MemoryPressureLevel.Normal;

        public event EventHandler<MemoryPressureLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public MemoryPressureSample? ReadSample() => null;
    }

    private sealed class UnavailableDialogEngine : IDialogEngine
    {
        public bool IsAvailable => false;

        public DialogResult Show(DialogRequest request) => DialogResult.Cancel;

        public Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(DialogResult.Cancel);

        public bool CloseTop(DialogOutcome outcome) => false;
    }

    private sealed class UnavailableDialogService : IDialogService
    {
        public bool Confirm(string title, string message, string? acknowledgementToken = null) => false;

        public InstanceDeletionChoice ChooseInstanceDeletion(string title, string message, bool canDeleteManagedData) =>
            InstanceDeletionChoice.Cancel;

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
