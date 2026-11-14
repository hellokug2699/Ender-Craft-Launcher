// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Shell.Output;
using Ecl.Core.Localization;
using Ecl.Minecraft.Launch;

namespace Ecl.App.ViewModels.Home;

/// <summary>
/// 主页游戏启动卡（home.md §3）：左侧实例信息、启动操作与总启动进度；
/// 右侧「普通 / 高级」两种模式展示同一次启动。
///
/// 状态归属（§3.4）：启动状态按实例保存在 <see cref="ILaunchStatusStore"/> 中，
/// 因此 ECL 关闭后再次打开仍能看到该实例上一次启动的结果；该实例下一次启动开始时重置。
///
/// 错误日志入口（§3.3）：只在最近一次启动失败且存在游戏日志会话时可用；
/// 新一次启动开始后立即隐藏。点击由 Shell 打开同一个 Logs Workspace 并传入准确 SessionId，
/// 本类不自行打开页面、也不猜测“最新日志”。
/// </summary>
public sealed partial class HomeLaunchCardViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly IHomeLaunchHost _host;
    private readonly ILaunchService _launchService;
    private readonly ILaunchStatusStore _statuses;
    private readonly HomeLaunchLogCapture _logCapture;
    private readonly ILocalizer _localizer;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, InstanceOperationState> _previews = new(StringComparer.Ordinal);
    private readonly Stopwatch _launchClock = new();

    private InstanceItemViewModel? _instance;
    private string? _launchInstanceId;
    private LaunchStatusSnapshot? _snapshot;
    private HomeLaunchStepViewModel? _runningStep;
    private TimeSpan _runningStepStartedAt;
    private DateTimeOffset? _lastOutputAtUtc;
    private bool _acceptProgress;
    private bool _launchInFlight;
    private bool _disposed;

    public HomeLaunchCardViewModel(
        IHomeLaunchHost host,
        ILaunchService launchService,
        ILaunchStatusStore statuses,
        AppLogBuffer logBuffer,
        ILocalizer localizer,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(launchService);
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(logBuffer);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _host = host;
        _launchService = launchService;
        _statuses = statuses;
        _localizer = localizer;
        _dispatcher = dispatcher;
        _logCapture = new HomeLaunchLogCapture(logBuffer);

        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TickInterval };
        _timer.Tick += OnTick;

        _host.PropertyChanged += OnLauncherPropertyChanged;
        UpdateLaunchAvailability();
    }

    /// <summary>由 Shell 注入：打开 Logs Workspace 并定位到指定会话（logs.md §7.1）。</summary>
    public Action<string>? OpenLogSession { get; set; }

    public ObservableCollection<HomeLaunchStepViewModel> Steps { get; } = [];

    /// <summary>高级模式：本次启动期间的轻量实时应用输出（不替代完整日志阅读器）。</summary>
    public ObservableCollection<AppLogEntry> LogLines { get; } = [];

    [ObservableProperty]
    private bool _hasInstance;

    [ObservableProperty]
    private string _instanceName = string.Empty;

    /// <summary>Minecraft 版本、加载器，以及存在时的 OptiFine（2号层级）。</summary>
    [ObservableProperty]
    private string _instanceMeta = string.Empty;

    [ObservableProperty]
    private string _instancePath = string.Empty;

    [ObservableProperty]
    private string _lastPlayedText = string.Empty;

    [ObservableProperty]
    private bool _isLaunching;

    /// <summary>右侧模式：true = 高级（实时输出），false = 普通（结构化步骤）。</summary>
    [ObservableProperty]
    private bool _isAdvanced;

    [ObservableProperty]
    private double _totalProgressValue;

    /// <summary>normal（白色，未开始或进行中）/ success（绿色）/ failed（红色，停在失败位置）。</summary>
    [ObservableProperty]
    private string _totalProgressKind = "normal";

    [ObservableProperty]
    private string _launchStatusText = string.Empty;

    [ObservableProperty]
    private string _launchStatusDetail = string.Empty;

    /// <summary>失败且存在游戏日志会话时才显示“查看错误日志”。</summary>
    [ObservableProperty]
    private bool _showErrorLogEntry;

    /// <summary>失败但没有游戏日志会话时说明原因，不显示不可点击的按钮。</summary>
    [ObservableProperty]
    private bool _showNoSessionNotice;

    [ObservableProperty]
    private bool _canLaunch;

    [ObservableProperty]
    private string _launchBlockedReason = string.Empty;

    [ObservableProperty]
    private bool _showPreview;

    [ObservableProperty]
    private string _previewText = string.Empty;

    /// <summary>预览失败时的真实原因（例如缺少版本文件或 Java）；不能静默什么都不显示。</summary>
    [ObservableProperty]
    private string _previewNote = string.Empty;

    [ObservableProperty]
    private bool _hasLogLines;

    [ObservableProperty]
    private string _logTruncatedText = string.Empty;

    public string LogEmptyHint => _localizer.Get("home.launch.advancedEmpty");

    public string NormalModeText => _localizer.Get("home.launch.mode.normal");

    public string AdvancedModeText => _localizer.Get("home.launch.mode.advanced");

    public string PreviewButtonText => _localizer.Get("home.launch.preview");

    public string LaunchButtonText => _localizer.Get("home.launch.play");

    public string ErrorLogButtonText => _localizer.Get("home.launch.viewErrorLog");

    public string NoSessionNoticeText => _localizer.Get("home.launch.noSession");

    /// <summary>绑定当前主页实例：读取该实例最近一次启动状态并刷新启动卡。</summary>
    public void Bind(InstanceItemViewModel? instance)
    {
        // 启动进行中不允许把启动卡切到另一个实例：这两件事在主页同时呈现会互相矛盾（§3.4）。
        if (_launchInFlight && instance?.Model.Id != _launchInstanceId)
        {
            return;
        }

        _instance = instance;
        _snapshot = null;
        _runningStep = null;

        if (instance is null)
        {
            HasInstance = false;
            InstanceName = string.Empty;
            InstanceMeta = string.Empty;
            InstancePath = string.Empty;
            LastPlayedText = string.Empty;
            PreviewText = string.Empty;
            ShowPreview = false;
            Steps.Clear();
            ApplySnapshot(null);
            UpdateLaunchAvailability();
            return;
        }

        HasInstance = true;
        InstanceName = instance.Name;
        InstanceMeta = BuildMeta(instance);
        InstancePath = instance.PathText;
        LastPlayedText = instance.LastPlayedText;

        var preview = GetOperation(instance.Model.Id);
        PreviewText = preview.LaunchCommandPreview;
        ShowPreview = !string.IsNullOrWhiteSpace(PreviewText);
        PreviewNote = string.Empty;

        ApplySnapshot(_statuses.Get(instance.Model.Id));
        UpdateLaunchAvailability();
    }

    /// <summary>重新进入主页时重新读取状态（启动可能已从其它入口发起）。</summary>
    public void Activate() => Bind(_instance);

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (_instance is not { } item)
        {
            return;
        }

        if (_launchInFlight || _host.IsBusy)
        {
            UpdateLaunchAvailability();
            return;
        }

        var before = _statuses.Get(item.Model.Id);
        var operation = GetOperation(item.Model.Id);
        string? unexpectedFailure = null;

        _launchInFlight = true;
        _launchInstanceId = item.Model.Id;
        _acceptProgress = true;
        _runningStep = null;
        _snapshot = null;
        TotalProgressKind = "normal";
        TotalProgressValue = 0;
        ShowErrorLogEntry = false;
        ShowNoSessionNotice = false;
        LaunchStatusDetail = string.Empty;
        LaunchStatusText = _localizer.Get("home.launch.statusRunning");
        IsLaunching = true;
        UpdateLaunchAvailability();

        ResetLog();
        _launchClock.Restart();
        _lastOutputAtUtc = DateTimeOffset.UtcNow;
        BuildPendingRows();
        _logCapture.Start();
        _timer.Start();

        try
        {
            await _host
                .LaunchAsync(item, operation, dryRun: false, new LaunchStepReporter(this))
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // LaunchForAsync 已自行处理已知失败；这里兜底未预期异常，并把失败原因呈现在卡片上。
            unexpectedFailure = exception.Message;
        }
        finally
        {
            // 先停止接收进度，再以持久化记录为唯一权威收尾：迟到的进度事件不得覆盖最终结果。
            _acceptProgress = false;
            _timer.Stop();
            _launchClock.Stop();
            _logCapture.Stop();
            FlushLog();
            _launchInFlight = false;
            _launchInstanceId = null;
            IsLaunching = false;

            var after = _statuses.Get(item.Model.Id);
            var started = after is not null && (before is null || after.StartedAtUtc > before.StartedAtUtc);
            ApplySnapshot(started ? after : before);

            if (unexpectedFailure is not null)
            {
                LaunchStatusText = _localizer.Get("home.launch.statusFailed");
                LaunchStatusDetail = unexpectedFailure;
                TotalProgressKind = "failed";
                ShowErrorLogEntry = false;
                ShowNoSessionNotice = false;
            }

            UpdateLaunchAvailability();
        }
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (_instance is not { } item || _launchInFlight || _host.IsBusy)
        {
            UpdateLaunchAvailability();
            return;
        }

        var operation = GetOperation(item.Model.Id);
        await _host.LaunchAsync(item, operation, dryRun: true, progress: null).ConfigureAwait(true);

        PreviewText = operation.LaunchCommandPreview;
        ShowPreview = !string.IsNullOrWhiteSpace(PreviewText);
        // Dry Run 失败时没有命令可显示：必须给出真实原因，不能静默无反应。
        PreviewNote = ShowPreview || string.IsNullOrWhiteSpace(operation.LaunchStatus)
            ? string.Empty
            : _localizer.Get("home.launch.previewUnavailable", operation.LaunchStatus);
    }

    [RelayCommand]
    private void ToggleMode() => IsAdvanced = !IsAdvanced;

    [RelayCommand]
    private void ShowNormalMode() => IsAdvanced = false;

    [RelayCommand]
    private void ShowAdvancedMode() => IsAdvanced = true;

    [RelayCommand]
    private void OpenErrorLog()
    {
        if (_snapshot is { State: LaunchAttemptState.Failed, SessionId: { Length: > 0 } sessionId })
        {
            OpenLogSession?.Invoke(sessionId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _host.PropertyChanged -= OnLauncherPropertyChanged;
        _logCapture.Dispose();
    }

    private void OnLauncherPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IHomeLaunchHost.IsBusy))
        {
            UpdateLaunchAvailability();
        }
    }

    private void UpdateLaunchAvailability()
    {
        if (_launchInFlight)
        {
            CanLaunch = false;
            LaunchBlockedReason = _localizer.Get("home.launch.blocked.launching");
            return;
        }

        if (_instance is null)
        {
            CanLaunch = false;
            LaunchBlockedReason = _localizer.Get("home.launch.blocked.noInstance");
            return;
        }

        if (_host.IsBusy)
        {
            CanLaunch = false;
            LaunchBlockedReason = _localizer.Get("home.launch.blocked.busy");
            return;
        }

        CanLaunch = true;
        LaunchBlockedReason = string.Empty;
    }

    private void ResetLog()
    {
        LogLines.Clear();
        HasLogLines = false;
        LogTruncatedText = string.Empty;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_launchInFlight)
        {
            UpdateRunningStep();
        }

        FlushLog();
    }

    private void UpdateRunningStep()
    {
        if (_runningStep is null)
        {
            return;
        }

        var elapsed = _launchClock.Elapsed - _runningStepStartedAt;
        _runningStep.UpdateElapsed(elapsed);

        var silentFor = _lastOutputAtUtc is { } last
            ? DateTimeOffset.UtcNow - last
            : TimeSpan.Zero;
        if (HomeLaunchPresentation.ShouldReportLongRunning(elapsed, silentFor))
        {
            _runningStep.SetLongRunningNote(elapsed, silentFor);
        }
        else
        {
            _runningStep.ClearNote();
        }

        UpdateTotalProgress();
    }

    private void UpdateTotalProgress()
    {
        var completed = Steps.Count(step =>
            step.State is HomeLaunchStepState.Success
                or HomeLaunchStepState.Warning
                or HomeLaunchStepState.Skipped);
        var running = _runningStep is { State: HomeLaunchStepState.Running }
            ? _runningStep.ProgressValue / 100
            : 0;
        TotalProgressValue = HomeLaunchPresentation.TotalProgress(Steps.Count, completed, running) * 100;
    }

    private void FlushLog()
    {
        var entries = _logCapture.Drain();
        if (entries.Count == 0)
        {
            UpdateLogTruncation();
            return;
        }

        foreach (var entry in entries)
        {
            LogLines.Add(entry);
        }

        while (LogLines.Count > HomeLaunchLogCapture.Capacity)
        {
            LogLines.RemoveAt(0);
        }

        HasLogLines = LogLines.Count > 0;
        _lastOutputAtUtc = DateTimeOffset.UtcNow;
        UpdateLogTruncation();
    }

    private void UpdateLogTruncation()
    {
        var dropped = _logCapture.DroppedCount;
        LogTruncatedText = dropped > 0 ? _localizer.Get("home.launch.logTruncated", dropped) : string.Empty;
    }

    private void ApplyProgress(LaunchStepProgress progress)
    {
        if (!_acceptProgress || _launchInstanceId is null)
        {
            return;
        }

        var row = Steps.FirstOrDefault(step => string.Equals(step.StepId, progress.StepId, StringComparison.Ordinal));
        if (progress.Phase == LaunchStepProgressPhase.Started)
        {
            if (row is null)
            {
                return;
            }

            _runningStep = row;
            _runningStepStartedAt = _launchClock.Elapsed;
            row.MarkRunning();
            row.ClearNote();
            UpdateTotalProgress();
            return;
        }

        if (row is null || progress.Status is not { } status)
        {
            return;
        }

        row.Complete(status, progress.Message ?? string.Empty, progress.Duration);
        if (ReferenceEquals(row, _runningStep))
        {
            _runningStep = null;
        }

        UpdateTotalProgress();
    }

    private void BuildPendingRows()
    {
        Steps.Clear();
        foreach (var stepId in _launchService.Steps)
        {
            Steps.Add(new HomeLaunchStepViewModel(stepId, _localizer));
        }
    }

    private void ApplySnapshot(LaunchStatusSnapshot? snapshot)
    {
        _snapshot = snapshot;
        _runningStep = null;
        ShowErrorLogEntry = false;
        ShowNoSessionNotice = false;
        LaunchStatusDetail = string.Empty;

        if (snapshot is null)
        {
            BuildPendingRows();
            LaunchStatusText = _localizer.Get("home.launch.statusNever");
            TotalProgressValue = 0;
            TotalProgressKind = "normal";
            return;
        }

        if (snapshot.State == LaunchAttemptState.Running)
        {
            // 上一次尝试没有记录结束：不猜测结果，也不伪造步骤完成。
            BuildPendingRows();
            LaunchStatusText = _localizer.Get("home.launch.statusInterrupted");
            LaunchStatusDetail = _localizer.Get("home.launch.interruptedHint");
            TotalProgressValue = 0;
            TotalProgressKind = "normal";
            return;
        }

        Steps.Clear();
        var pipeline = _launchService.Steps;
        var recorded = snapshot.Steps.ToDictionary(step => step.StepId, StringComparer.Ordinal);

        // 启动在进入流水线前就被拒绝时，记录里的步骤（例如账户/实例）不是流水线步骤，
        // 必须原样保留在前面，不能因为“不在链路里”而丢弃失败原因。
        foreach (var step in snapshot.Steps.Where(step =>
                     !pipeline.Contains(step.StepId, StringComparer.Ordinal)))
        {
            var row = new HomeLaunchStepViewModel(step.StepId, _localizer);
            row.Restore(step);
            Steps.Add(row);
        }

        foreach (var stepId in pipeline)
        {
            var row = new HomeLaunchStepViewModel(stepId, _localizer);
            if (recorded.TryGetValue(stepId, out var step))
            {
                row.Restore(step);
            }
            else
            {
                row.MarkNotRun();
            }

            Steps.Add(row);
        }

        var failed = snapshot.State == LaunchAttemptState.Failed;
        LaunchStatusText = _localizer.Get(failed ? "home.launch.statusFailed" : "home.launch.statusSucceeded");
        if (!string.IsNullOrWhiteSpace(snapshot.FailureSummary))
        {
            LaunchStatusDetail = snapshot.FailureSummary;
        }
        else if (snapshot.Duration > TimeSpan.Zero)
        {
            LaunchStatusDetail = _localizer.Get("home.launch.duration", snapshot.Duration.TotalSeconds.ToString("F1"));
        }

        if (failed)
        {
            var position = -1;
            for (var index = 0; index < Steps.Count; index++)
            {
                if (Steps[index].State == HomeLaunchStepState.Failed)
                {
                    position = index + 1;
                    break;
                }
            }

            TotalProgressValue = Steps.Count == 0
                ? 0
                : Math.Clamp(Math.Max(0, position) * 100.0 / Steps.Count, 0, 100);
            TotalProgressKind = "failed";
            ShowNoSessionNotice = string.IsNullOrWhiteSpace(snapshot.SessionId);
            ShowErrorLogEntry = !ShowNoSessionNotice;
            return;
        }

        TotalProgressValue = 100;
        TotalProgressKind = "success";
    }

    private static string BuildMeta(InstanceItemViewModel instance)
    {
        var meta = instance.VersionText;
        if (!string.IsNullOrWhiteSpace(instance.Model.OptiFineVersion))
        {
            meta = $"{meta} · OptiFine {instance.Model.OptiFineVersion}";
        }

        return meta;
    }

    private InstanceOperationState GetOperation(string instanceId)
    {
        if (!_previews.TryGetValue(instanceId, out var operation))
        {
            operation = new InstanceOperationState();
            _previews[instanceId] = operation;
        }

        return operation;
    }

    /// <summary>把启动服务的进度事件封送到 UI Dispatcher，并按“本次启动”过滤过期事件。</summary>
    private sealed class LaunchStepReporter(HomeLaunchCardViewModel owner) : IProgress<LaunchStepProgress>
    {
        public void Report(LaunchStepProgress value)
        {
            if (owner._disposed || !owner._acceptProgress)
            {
                return;
            }

            if (owner._dispatcher.CheckAccess())
            {
                owner.ApplyProgress(value);
                return;
            }

            owner._dispatcher.BeginInvoke(new Action(() =>
            {
                if (!owner._disposed && owner._acceptProgress)
                {
                    owner.ApplyProgress(value);
                }
            }));
        }
    }
}

