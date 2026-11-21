// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Dialogs;
using Ecl.App.Services;
using Ecl.App.Tests.Support;
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
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        var filteredCount = 0;
        var restoredCount = 0;
        var focusedInstance = string.Empty;
        var hadFocus = false;
        var focusCleared = false;
        ui.Invoke(() => vm.FocusInstance("alpha", "Alpha"));
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();
        ui.Invoke(() =>
        {
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
    public async Task PollingWaitsForInitialSourceLoadToFinish()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        File.WriteAllText(
            Path.Combine(session.DirectoryPath, "game.log"),
            "[10:00:00] [main/INFO]: alpha needle\n" +
            "[10:00:01] [main/WARN]: beta needle\n" +
            "[10:00:02] [main/ERROR]: gamma needle\n" +
            "unparsed needle line\n");
        var reader = new BlockingSessionLogReader(new SessionLogReader());
        var vm = await ui.InvokeAsync(() => CreateViewModel(reader));

        ui.Invoke(() => vm.Activate());
        await reader.WaitUntilInitialReadStartsAsync();

        // The timer can fire while ReadLatestAsync is still pending. It must not
        // read from offset zero and append a duplicate copy of the same file.
        await ui.InvokeAsync(() => vm.PollAsync()).Unwrap();
        Assert.Equal(0, reader.ReadFromCallCount);

        reader.ReleaseInitialRead();
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();
        Assert.Equal(4, vm.Lines.Count);

        ui.Invoke(() =>
        {
            vm.SearchText = "needle";
            vm.SearchCommand.Execute(null);
        });
        Assert.Equal("4 条命中", vm.SearchStatus);

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

        Assert.Equal("4 条命中", vm.SearchStatus);
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

    /// <summary>
    /// F-09 S3/S9：Inspector 显示采集方式与写入/丢弃字节；控制台模式不能显示成完整采集。
    /// </summary>
    [Fact]
    public async Task InspectorShowsCaptureAccountingAndConsoleGap()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        File.WriteAllText(Path.Combine(session.DirectoryPath, "game.log"), "line\n");
        _sessions.SetCaptureAccounting(
            session.SessionId,
            SessionCaptureModes.Console,
            0,
            0,
            SessionWarnings.CaptureConsole);

        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        var fields = vm.BuildInspectorContext()!.Fields;
        Assert.Contains(fields, field => field.Label == "采集方式" && field.Value.Contains("控制台"));
        Assert.Contains(fields, field => field.Label == "采集写入" && field.Value.Contains("0"));
        Assert.Contains(fields, field => field.Label == "警告" && field.Value.Contains("未被重定向"));

        ui.Invoke(() => vm.Dispose());
    }

    /// <summary>
    /// F-09 S8：多选与可见范围复制只写入剪贴板；多选结果按显示顺序，不修改原始日志。
    /// </summary>
    [Fact]
    public async Task MultiSelectionCopyUsesSelectedAndVisibleLines()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        File.WriteAllText(
            Path.Combine(session.DirectoryPath, "game.log"),
            "first\nsecond\nthird\n");
        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        await ui.InvokeAsync(async () =>
        {
            // 选择顺序故意倒序：复制必须按显示顺序输出。
            vm.UpdateLineSelection([vm.Lines[2], vm.Lines[0]]);
            Assert.Equal(2, vm.SelectedLineCount);
            Assert.Equal($"first{Environment.NewLine}third", vm.SelectionCopyText());
            Assert.Equal(
                $"first{Environment.NewLine}second{Environment.NewLine}third",
                vm.VisibleCopyText());

            // 命令本身只写剪贴板；无桌面剪贴板的测试环境即使失败也不能抛异常。
            vm.CopyLinesCommand.Execute(null);
            vm.CopyVisibleLinesCommand.Execute(null);
            Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
            await Task.CompletedTask;
        }).Unwrap();

        // 复制是视图行为：原始文件不变。
        Assert.Equal(
            "first\nsecond\nthird\n",
            await File.ReadAllTextAsync(Path.Combine(session.DirectoryPath, "game.log")));

        ui.Invoke(() => vm.Dispose());
    }

    /// <summary>
    /// F-09 S4/S8：显示偏好持久化到设置服务；重新打开页面恢复，且“从未设置”不会被解释成 false。
    /// </summary>
    [Fact]
    public async Task DisplayPreferencesPersistAndReload()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        File.WriteAllText(Path.Combine(session.DirectoryPath, "game.log"), "line\n");
        var settings = new FakeSettingsService();

        var first = await ui.InvokeAsync(() => CreateViewModel(settings: settings));
        Assert.True(first.Follow);
        Assert.False(first.AutoWrap);
        Assert.False(first.AutoOpenOnFailure);

        ui.Invoke(() =>
        {
            first.AutoWrap = true;
            first.Follow = false;
            first.FilterDebug = false;
            first.ToggleAutoOpenOnFailureCommand.Execute(null);
        });

        Assert.Equal("true", settings.Values["logs.autoWrap"]);
        Assert.Equal("false", settings.Values["logs.follow"]);
        Assert.Equal("Debug", settings.Values["logs.hiddenLevels"]);
        Assert.Equal("true", settings.Values["logs.autoOpenOnFailure"]);

        var second = await ui.InvokeAsync(() => CreateViewModel(settings: settings));
        Assert.True(second.AutoWrap);
        Assert.False(second.Follow);
        Assert.False(second.FilterDebug);
        Assert.True(second.AutoOpenOnFailure);

        ui.Invoke(() =>
        {
            first.Dispose();
            second.Dispose();
        });
    }

    /// <summary>
    /// F-09 S5：运行中会话在一段时间没有新输出时给出不确定提示；控制台模式说明静默不代表无输出。
    /// </summary>
    [Fact]
    public async Task StallHintIsUncertainAndExplicitAboutCaptureGaps()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        var path = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllText(path, "line\n");
        // 证据是“来源最后一次写入时间”而不是 UI 计时器：把写入时间推回，静默时长才可复现。
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        _sessions.AttachProcess(
            session.SessionId,
            Environment.ProcessId,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            @"C:\Java\bin\javaw.exe",
            @"C:\Games");
        _sessions.SetCaptureAccounting(session.SessionId, SessionCaptureModes.Redirect, 5, 0);

        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();
        await ui.InvokeAsync(() => vm.PollAsync()).Unwrap();

        Assert.True(vm.HasStallHint);
        Assert.Contains("不能据此判定卡死", vm.StallHint);

        ui.Invoke(() =>
        {
            _sessions.SetCaptureAccounting(
                session.SessionId,
                SessionCaptureModes.Console,
                0,
                0,
                SessionWarnings.CaptureConsole);
            // 重新读取会话列表，让页面看到新的采集方式（不刷新 UI 状态、不猜测）。
            vm.RefreshSessionsCommand.Execute(null);
        });
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();
        await ui.InvokeAsync(() => vm.PollAsync()).Unwrap();
        Assert.Contains("没有 stdout/stderr 采集", vm.StallHint);

        ui.Invoke(() => vm.Dispose());
    }

    /// <summary>
    /// logs.md §3.3：来源按实际可用性逐步出现。会话启动时会话独立日志可能还不存在，
    /// 页面不能停在“共享 latest.log”快照上，必须在轮询时切到本次会话自己的 game.log。
    /// </summary>
    [Fact]
    public async Task PollingSwitchesFromSharedLatestLogToTheLiveSessionLog()
    {
        using var ui = new UiThread();
        var session = CreateSession("live", "1.12.2", SessionLogIsolationModes.Pending);
        var gameDirectory = session.Manifest.GameDirectory!;
        Directory.CreateDirectory(Path.Combine(gameDirectory, "logs"));
        // 会话创建后由上一次启动残留的共享日志仍在写入窗口内：此时只能按歧义来源呈现。
        File.WriteAllText(Path.Combine(gameDirectory, "logs", "latest.log"), "old-shared-line\n");

        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        Assert.False(string.IsNullOrWhiteSpace(vm.SelectedSource!.Warning));
        Assert.Contains(vm.Lines, line => line.Message.Contains("old-shared-line", StringComparison.Ordinal));

        // 游戏进程开始写会话独立日志。
        File.WriteAllText(
            Path.Combine(session.DirectoryPath, "game.log"),
            "[10:00:00] [main/INFO]: session-line\n");
        await ui.InvokeAsync(() => vm.PollAsync()).Unwrap();
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        Assert.True(vm.SelectedSource!.IsGameLog);
        Assert.Null(vm.SelectedSource.Warning);
        Assert.Contains(vm.Lines, line => line.Message.Contains("session-line", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.Lines, line => line.Message.Contains("old-shared-line", StringComparison.Ordinal));

        ui.Invoke(() => vm.Dispose());
    }

    [Fact]
    public async Task PollingKeepsTheLoadedWindowWhenSourcesAreUnchanged()
    {
        using var ui = new UiThread();
        var session = CreateSession("stable", "1.21.1");
        var path = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();
        Assert.Equal(3, vm.Lines.Count);

        // 文件继续增长但来源身份没变：轮询只追加新行，不重建来源栏、不重读整份日志。
        File.AppendAllText(path, "four\n");
        await ui.InvokeAsync(() => vm.PollAsync()).Unwrap();
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        Assert.Equal(["one", "two", "three", "four"], vm.Lines.Select(line => line.Message).ToArray());
        Assert.Equal(4, vm.Sources.Count);

        ui.Invoke(() => vm.Dispose());
    }

    /// <summary>
    /// 崩溃报告与 hs_err 的结论在开头：证据来源默认从开头显示，长调用栈不会再让错因不可见；
    /// “跳到结尾”仍能一次到达尾部，“跳到开头”再回来。
    /// </summary>
    [Fact]
    public async Task CrashReportEvidenceOpensAtTheHeadAndCanJumpToTheEnd()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        var lines = new List<string>
        {
            "Description: Rendering overlay crashed",
            "java.lang.NullPointerException: Cannot invoke registry lookup",
        };
        lines.AddRange(Enumerable.Range(1, 12000).Select(index =>
            $"#  at example.package.Class{index:0000}.method(Class{index:0000}.java:{index})"));
        RegisterEvidence(session, "crash-2026-11-21.txt", SessionArtifactKinds.ClientCrashReport, lines);

        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();
        ui.Invoke(() => vm.SelectedSource = vm.Sources.Single(source => source.Kind == SessionLogSourceKind.Evidence));
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        Assert.True(vm.IsEvidenceHead);
        Assert.False(vm.FollowsTail);
        Assert.Equal("Description: Rendering overlay crashed", vm.Lines[0].Message);
        Assert.Contains("开头", vm.StatusMessage);
        // 大文件仍然受显示上限保护，但裁剪必须保留开头（错因所在）。
        Assert.True(vm.Lines.Count <= 2000);
        Assert.Equal("Description: Rendering overlay crashed", vm.Lines[0].Message);

        await ui.InvokeAsync(() => vm.JumpEvidenceToTailCommand.ExecuteAsync(null)).Unwrap();
        Assert.False(vm.IsEvidenceHead);
        Assert.Contains(vm.Lines, line => line.Message.Contains("Class12000", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.Lines, line => line.Message.StartsWith("Description:", StringComparison.Ordinal));

        await ui.InvokeAsync(() => vm.JumpEvidenceToHeadCommand.ExecuteAsync(null)).Unwrap();        Assert.True(vm.IsEvidenceHead);
        Assert.Equal("Description: Rendering overlay crashed", vm.Lines[0].Message);

        ui.Invoke(() => vm.Dispose());
    }

    /// <summary>游戏日志等证据仍以最新内容为准：不能因为崩溃报告改了锚点就把所有证据都从头读。</summary>
    [Fact]
    public async Task GameLogEvidenceStillOpensAtTheEnd()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        var lines = new List<string> { "first line" };
        lines.AddRange(Enumerable.Range(1, 9000).Select(index => $"[10:00:00] [main/INFO]: line-{index:0000}"));
        RegisterEvidence(session, "rotated.log", SessionArtifactKinds.RotatedLog, lines);

        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();
        ui.Invoke(() => vm.SelectedSource = vm.Sources.Single(source => source.Kind == SessionLogSourceKind.Evidence));
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        Assert.False(vm.IsEvidenceHead);
        Assert.True(vm.FollowsTail);
        Assert.Equal(lines[^1], vm.Lines[^1].Raw);
        Assert.DoesNotContain(vm.Lines, line => line.Raw == "first line");

        ui.Invoke(() => vm.Dispose());
    }

    /// <summary>
    /// 证据向更早分页必须按真实字节偏移读取：重新读取末尾会把同一段内容重复追加一次。
    /// </summary>
    [Fact]
    public async Task EvidenceLoadOlderPagesBackwardsInsteadOfRereadingTheTail()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        // 每行较长，使 512KB 尾窗只覆盖一部分行（行数远低于显示上限）。
        var lines = Enumerable.Range(1, 1000)
            .Select(index => $"# evidence line {index:0000} " + new string('x', 600))
            .ToList();
        RegisterEvidence(session, "rotated.log", SessionArtifactKinds.RotatedLog, lines);

        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();
        ui.Invoke(() => vm.SelectedSource = vm.Sources.Single(source => source.Kind == SessionLogSourceKind.Evidence));
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        Assert.True(vm.CanLoadOlder);
        var loadedBefore = vm.Lines.Count;

        await ui.InvokeAsync(() => vm.LoadOlderCommand.ExecuteAsync(null)).Unwrap();

        Assert.True(vm.Lines.Count > loadedBefore);
        // 分页读取的是更早的内容，不是把同一段末尾重复追加。
        Assert.Equal(lines[0], vm.Lines[0].Message);
        var duplicates = vm.Lines
            .GroupBy(line => line.Message, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.Empty(duplicates);

        ui.Invoke(() => vm.Dispose());
    }

    /// <summary>从开头读到一半时可以继续向更新读，且不会丢掉已加载的开头。</summary>
    [Fact]
    public async Task EvidenceLoadNewerContinuesAfterTheHeadWindow()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        var lines = new List<string> { "Description: start of the crash report" };
        lines.AddRange(Enumerable.Range(1, 1100).Select(index =>
            $"#  at example.Class{index:0000}.frame(Frame{index:0000}.java:{index}) " + new string('y', 600)));
        RegisterEvidence(session, "crash-big.txt", SessionArtifactKinds.ClientCrashReport, lines);

        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();
        ui.Invoke(() => vm.SelectedSource = vm.Sources.Single(source => source.Kind == SessionLogSourceKind.Evidence));
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        Assert.True(vm.IsEvidenceHead);
        Assert.True(vm.CanLoadNewer);
        Assert.Equal(lines[0], vm.Lines[0].Message);

        var loadedBefore = vm.Lines.Count;
        await ui.InvokeAsync(() => vm.LoadNewerCommand.ExecuteAsync(null)).Unwrap();

        Assert.True(vm.Lines.Count > loadedBefore);
        Assert.Equal(lines[0], vm.Lines[0].Message);
        Assert.Equal(lines[^1], vm.Lines[^1].Message);

        ui.Invoke(() => vm.Dispose());
    }

    private void RegisterEvidence(
        GameLogSession session,
        string fileName,
        string kind,
        IReadOnlyList<string> lines)
    {
        var report = Path.Combine(session.DirectoryPath, "artifacts", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, string.Join('\n', lines) + "\n");
        _sessions.RegisterArtifact(session.SessionId, new SessionArtifact
        {
            Id = $"{kind}:{fileName}",
            Kind = kind,
            SourcePath = report,
            CapturedPath = report,
            Ownership = ArtifactOwnership.Direct,
        });
    }

    private GameLogSession CreateSession(
        string instanceId,
        string version,
        string logIsolation = SessionLogIsolationModes.Direct) =>
        _sessions.Create(new SessionCreateRequest
        {
            InstanceId = instanceId,
            InstanceName = char.ToUpperInvariant(instanceId[0]) + instanceId[1..],
            GameDirectory = Path.Combine(_root, "games", instanceId),
            MinecraftVersion = version,
            Loader = "fabric",
            LogIsolation = logIsolation,
        });

    private LogSessionsViewModel CreateViewModel(
        ISessionLogReader? reader = null,
        FakeSettingsService? settings = null) =>
        new(
            _sessions,
            reader ?? new SessionLogReader(),
            new SessionExportService(_sessions, _paths),
            new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")),
            new TestMemoryPressureMonitor(),
            _operations,
            new UnavailableDialogEngine(),
            new UnavailableDialogService(),
            _paths,
            settings ?? new FakeSettingsService(),
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

    private sealed class BlockingSessionLogReader(ISessionLogReader inner) : ISessionLogReader
    {
        private readonly TaskCompletionSource<bool> _initialReadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseInitialRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadFromCallCount { get; private set; }

        public Task WaitUntilInitialReadStartsAsync() => _initialReadStarted.Task;

        public void ReleaseInitialRead() => _releaseInitialRead.TrySetResult(true);

        public IReadOnlyList<SessionLogSourceInfo> DescribeSources(GameLogSession session) =>
            inner.DescribeSources(session);

        public IReadOnlyList<SessionEvidenceItem> DescribeEvidence(GameLogSession session) =>
            inner.DescribeEvidence(session);

        public async Task<SessionLogChunk> ReadLatestAsync(
            GameLogSession session,
            SessionLogSourceKind kind,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default)
        {
            _initialReadStarted.TrySetResult(true);
            await _releaseInitialRead.Task.WaitAsync(cancellationToken);
            return await inner.ReadLatestAsync(session, kind, maxBytes, cancellationToken);
        }

        public Task<SessionLogChunk> ReadFromAsync(
            GameLogSession session,
            SessionLogSourceKind kind,
            long offset,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default)
        {
            ReadFromCallCount++;
            return inner.ReadFromAsync(session, kind, offset, maxBytes, cancellationToken);
        }

        public Task<SessionLogChunk> ReadBeforeAsync(
            GameLogSession session,
            SessionLogSourceKind kind,
            long beforeOffset,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default) =>
            inner.ReadBeforeAsync(session, kind, beforeOffset, maxBytes, cancellationToken);

        public Task<SessionLogChunk> ReadEvidenceAsync(
            GameLogSession session,
            SessionArtifact artifact,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            SessionEvidenceAnchor anchor = SessionEvidenceAnchor.Tail,
            CancellationToken cancellationToken = default) =>
            inner.ReadEvidenceAsync(session, artifact, maxBytes, anchor, cancellationToken);

        public Task<SessionLogChunk> ReadEvidenceBeforeAsync(
            GameLogSession session,
            SessionArtifact artifact,
            long beforeOffset,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default) =>
            inner.ReadEvidenceBeforeAsync(session, artifact, beforeOffset, maxBytes, cancellationToken);

        public Task<SessionLogChunk> ReadEvidenceAfterAsync(
            GameLogSession session,
            SessionArtifact artifact,
            long afterOffset,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default) =>
            inner.ReadEvidenceAfterAsync(session, artifact, afterOffset, maxBytes, cancellationToken);
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
        public bool Confirm(
            string title,
            string message,
            string? acknowledgementToken = null,
            ConfirmationKind kind = ConfirmationKind.Mandatory) => false;

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
