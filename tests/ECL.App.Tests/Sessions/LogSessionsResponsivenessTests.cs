// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
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
/// 日志工作区响应性回归（性能交接 §4、§6、§9.3）：
/// 会话清单/来源清单读取不得占用 UI Dispatcher；大日志首屏必须有界；长列表必须启用虚拟化。
/// </summary>
public sealed class LogSessionsResponsivenessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ecl-log-responsiveness-tests",
        Guid.NewGuid().ToString("N"));

    private readonly AppPaths _paths;
    private readonly GameSessionService _sessions;
    private readonly OperationService _operations = new();

    public LogSessionsResponsivenessTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _sessions = new GameSessionService(_paths, NullLogger<GameSessionService>.Instance);
    }

    [Fact]
    public async Task SessionListAndSourceDescriptionDoNotRunOnTheDispatcherThread()
    {
        using var ui = new UiThread();
        var session = CreateSession("a", "1.21.1");
        File.WriteAllText(Path.Combine(session.DirectoryPath, "game.log"), "[10:00:00] [main/INFO]: hello\n");

        var sessions = new ThreadTrackingSessionService(_sessions);
        var reader = new ThreadTrackingLogReader(new SessionLogReader());
        var vm = await ui.InvokeAsync(() => CreateViewModel(sessions, reader));

        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        Assert.NotEqual(ui.Dispatcher.Thread.ManagedThreadId, sessions.ListThreadId);
        Assert.NotEqual(ui.Dispatcher.Thread.ManagedThreadId, reader.DescribeSourcesThreadId);
        Assert.NotEqual(ui.Dispatcher.Thread.ManagedThreadId, reader.DescribeEvidenceThreadId);

        ui.Invoke(() => vm.Dispose());
    }

    [Fact]
    public async Task LargeInitialLogIsBoundedToTheVisibleWindow()
    {
        using var ui = new UiThread();
        var session = CreateSession("big", "1.21.1");

        // 约 2 万行、25 字节左右，单个 512 KB 首屏块即可读入；页面必须只保留有界窗口。
        var builder = new StringBuilder(20_000 * 26);
        for (var index = 0; index < 20_000; index++)
        {
            builder.Append("[10:00:00] [main/INFO]: line ").Append(index).Append('\n');
        }

        var logPath = Path.Combine(session.DirectoryPath, "game.log");
        File.WriteAllText(logPath, builder.ToString());

        var vm = await ui.InvokeAsync(() => CreateViewModel());
        ui.Invoke(() => vm.Activate());
        await ui.InvokeAsync(() => vm.WaitForSourceLoadAsync()).Unwrap();

        // 窗口上限 2000：首屏不得把整份历史日志留在 UI 集合里。
        Assert.InRange(vm.Lines.Count, 1, 2000);

        ui.Invoke(() => vm.Dispose());
    }

    [Fact]
    public void TheLogListUsesRealVirtualization()
    {
        var xaml = ReadSource("src/ECL.App/Views/LogSessionsView.xaml");

        Assert.Contains("VirtualizingPanel.IsVirtualizing\" Value=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.VirtualizationMode\" Value=\"Recycling\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<VirtualizingStackPanel />", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualizingPanel.IsVirtualizing\" Value=\"False\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<StackPanel />\n                                </ItemsPanelTemplate>", xaml, StringComparison.Ordinal);
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

    private LogSessionsViewModel CreateViewModel(
        IGameSessionService? sessions = null,
        ISessionLogReader? reader = null) =>
        new(
            sessions ?? _sessions,
            reader ?? new SessionLogReader(),
            new SessionExportService(_sessions, _paths),
            new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")),
            new TestMemoryPressureMonitor(),
            _operations,
            new UnavailableDialogEngine(),
            new UnavailableDialogService(),
            _paths,
            new FakeSettingsService(),
            NullLogger<LogSessionsViewModel>.Instance);

    private static string ReadSource(string relativePath)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ECL.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"找不到源文件：{path}");
        return File.ReadAllText(path);
    }

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

    /// <summary>记录 <see cref="IGameSessionService.List"/> 实际执行线程；其余方法直接转发。</summary>
    private sealed class ThreadTrackingSessionService(IGameSessionService inner) : IGameSessionService
    {
        public int ListThreadId { get; private set; }

        public GameLogSession? Get(string sessionId) => inner.Get(sessionId);

        public IReadOnlyList<GameLogSession> List()
        {
            ListThreadId = Environment.CurrentManagedThreadId;
            return inner.List();
        }

        public GameLogSession Create(SessionCreateRequest request) => inner.Create(request);

        public void MarkState(string sessionId, GameSessionState state, string? detail = null) =>
            inner.MarkState(sessionId, state, detail);

        public void AttachProcess(
            string sessionId,
            int processId,
            DateTimeOffset processStartedAtUtc,
            string executablePath,
            string workingDirectory,
            string? captureHost = null,
            int? captureHostProcessId = null) =>
            inner.AttachProcess(
                sessionId,
                processId,
                processStartedAtUtc,
                executablePath,
                workingDirectory,
                captureHost,
                captureHostProcessId);

        public void RegisterArtifact(string sessionId, SessionArtifact artifact) =>
            inner.RegisterArtifact(sessionId, artifact);

        public void SetCaptureFault(string sessionId, string fault) => inner.SetCaptureFault(sessionId, fault);

        public void MarkCaptureDegraded(string sessionId, string reason) =>
            inner.MarkCaptureDegraded(sessionId, reason);

        public void SetLogIsolation(string sessionId, string mode, string? fingerprint, string? warning) =>
            inner.SetLogIsolation(sessionId, mode, fingerprint, warning);

        public void MarkCaptureHostLost(string sessionId, string reason) =>
            inner.MarkCaptureHostLost(sessionId, reason);

        public void SetCaptureAccounting(
            string sessionId,
            string mode,
            long writtenBytes,
            long droppedBytes,
            string? warning = null) =>
            inner.SetCaptureAccounting(sessionId, mode, writtenBytes, droppedBytes, warning);

        public void SetExitCode(string sessionId, int exitCode) => inner.SetExitCode(sessionId, exitCode);

        public void AppendLaunchStage(string sessionId, LaunchStageEvent stage) =>
            inner.AppendLaunchStage(sessionId, stage);

        public void SetLaunchJava(string sessionId, string? javaPath, string? javaVersion) =>
            inner.SetLaunchJava(sessionId, javaPath, javaVersion);

        public IReadOnlyList<SessionEvent> ReadEvents(string sessionId) => inner.ReadEvents(sessionId);

        public SessionRecoveryReport Recover() => inner.Recover();
    }

    /// <summary>记录来源/证据清单读取线程；正文读取转发给真实阅读器。</summary>
    private sealed class ThreadTrackingLogReader(ISessionLogReader inner) : ISessionLogReader
    {
        public int DescribeSourcesThreadId { get; private set; }

        public int DescribeEvidenceThreadId { get; private set; }

        public IReadOnlyList<SessionLogSourceInfo> DescribeSources(GameLogSession session)
        {
            DescribeSourcesThreadId = Environment.CurrentManagedThreadId;
            return inner.DescribeSources(session);
        }

        public IReadOnlyList<SessionEvidenceItem> DescribeEvidence(GameLogSession session)
        {
            DescribeEvidenceThreadId = Environment.CurrentManagedThreadId;
            return inner.DescribeEvidence(session);
        }

        public Task<SessionLogChunk> ReadLatestAsync(
            GameLogSession session,
            SessionLogSourceKind kind,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default) =>
            inner.ReadLatestAsync(session, kind, maxBytes, cancellationToken);

        public Task<SessionLogChunk> ReadFromAsync(
            GameLogSession session,
            SessionLogSourceKind kind,
            long offset,
            int maxBytes = SessionLogReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default) =>
            inner.ReadFromAsync(session, kind, offset, maxBytes, cancellationToken);

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
