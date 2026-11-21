// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.App.Shell.Inspector;
using Ecl.App.ViewModels.Home;
using Ecl.App.ViewModels.LogSessions;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Diagnostics;
using Ecl.Core.Localization;
using Ecl.Operations;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Ecl.App.ViewModels;

public sealed record LogFilterOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record LogTimeRangeOption(string Id, string Label, TimeSpan? Range)
{
    public override string ToString() => Label;
}

/// <summary>
/// 日志与诊断工作区（logs.md）。中央阅读器是主体，左侧是按 SessionId 组织的会话栏；
/// 会话详情与证据摘要通过 Shell 的 Inspector 选择层呈现，不建立第二个右侧栏。
/// 页面只通过 <see cref="IGameSessionService"/> / <see cref="ISessionLogReader"/> /
/// <see cref="ISessionExportService"/> 读取与导出，不自行扫描实例目录。
/// </summary>
public sealed partial class LogSessionsViewModel : ObservableObject, IDisposable
{
    private const int MaxLines = 2000;
    private const int InitialChunkBytes = 512 * 1024;
    private const int PollChunkBytes = 512 * 1024;

    private readonly IGameSessionService _sessions;
    private readonly ISessionLogReader _reader;
    private readonly ISessionExportService _exporter;
    private readonly ILocalizer _localizer;
    private readonly IMemoryPressureMonitor _pressure;
    private readonly IOperationService _operations;
    private readonly IDialogEngine _dialogs;
    private readonly IDialogService _dialogService;
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ILogger<LogSessionsViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private readonly List<LogLineViewModel> _allLines = [];
    private readonly List<LogLineViewModel> _matches = [];
    private readonly List<LogSessionRowViewModel> _rows = [];
    private readonly List<LogLineViewModel> _selectedLines = [];

    private int _maxLines = MaxLines;
    private bool _disposed;
    private bool _polling;
    private bool _loadingDisplayPreferences;
    private long _sourceOffset;
    private long _sourceWindowStart;
    private SessionArtifact? _evidenceArtifact;
    private string? _targetSessionId;
    private string? _instanceFocusId;
    private string? _instanceFocusName;
    private bool _suppressFilterRefresh;
    private int _currentMatchIndex = -1;
    private Task _sessionsLoad = Task.CompletedTask;
    private Task _sessionSourcesLoad = Task.CompletedTask;
    private Task _sourceLoad = Task.CompletedTask;
    private CancellationTokenSource? _searchCancellation;
    private int _searchVersion;
    /// <summary>来源/会话切换代次：异步读取返回时若代次已变，丢弃过期结果，避免旧来源覆盖新视图。</summary>
    private int _loadGeneration;
    private CancellationTokenSource? _filterDebounce;
    // 小集合直接同步搜索（测试与低延迟），大集合才移出 UI 线程，避免每次键入都等待全量扫描。
    private const int SynchronousSearchThreshold = 4000;

    public LogSessionsViewModel(
        IGameSessionService sessions,
        ISessionLogReader reader,
        ISessionExportService exporter,
        ILocalizer localizer,
        IMemoryPressureMonitor pressure,
        IOperationService operations,
        IDialogEngine dialogs,
        IDialogService dialogService,
        IAppPaths paths,
        ISettingsService settings,
        ILogger<LogSessionsViewModel> logger)
    {
        _sessions = sessions;
        _reader = reader;
        _exporter = exporter;
        _localizer = localizer;
        _pressure = pressure;
        _operations = operations;
        _dialogs = dialogs;
        _dialogService = dialogService;
        _paths = paths;
        _settings = settings;
        _logger = logger;

        SessionsView = CollectionViewSource.GetDefaultView(Sessions);
        SessionsView.Filter = FilterSession;
        SessionsView.SortDescriptions.Add(new SortDescription(
            nameof(LogSessionRowViewModel.GroupOrder),
            ListSortDirection.Ascending));
        SessionsView.SortDescriptions.Add(new SortDescription(
            nameof(LogSessionRowViewModel.CreatedAtUtc),
            ListSortDirection.Descending));
        SessionsView.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(LogSessionRowViewModel.GroupTitle)));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _pressure.LevelChanged += OnPressureChanged;

        InstanceFilterOptions.Add(new LogFilterOption(string.Empty, _localizer.Get("logs.filter.allInstances")));
        StateFilterOptions =
        [
            new("all", _localizer.Get("logs.filter.allStates")),
            new("running", _localizer.Get("logs.filter.running")),
            new("exited", _localizer.Get("logs.filter.exited")),
            new("interrupted", _localizer.Get("logs.filter.interrupted")),
            new("unknown", _localizer.Get("logs.filter.unknown")),
        ];
        TimeFilterOptions =
        [
            new("all", _localizer.Get("logs.filter.allTime"), null),
            new("5m", _localizer.Get("logs.filter.last5Minutes"), TimeSpan.FromMinutes(5)),
            new("1h", _localizer.Get("logs.filter.lastHour"), TimeSpan.FromHours(1)),
            new("24h", _localizer.Get("logs.filter.lastDay"), TimeSpan.FromDays(1)),
        ];
        _selectedStateFilter = StateFilterOptions[0];
        _selectedTimeFilter = TimeFilterOptions[0];
        _selectedInstanceFilter = InstanceFilterOptions[0];
        LoadDisplayPreferences();
    }

    public ObservableCollection<LogSessionRowViewModel> Sessions { get; } = [];

    public ICollectionView SessionsView { get; }

    public ObservableCollection<LogSourceViewModel> Sources { get; } = [];

    public ObservableCollection<LogEvidenceRowViewModel> Evidence { get; } = [];

    public ObservableCollection<LogLineViewModel> Lines { get; } = [];

    public ObservableCollection<LogFilterOption> InstanceFilterOptions { get; } = [];

    public IReadOnlyList<LogFilterOption> StateFilterOptions { get; }

    public IReadOnlyList<LogTimeRangeOption> TimeFilterOptions { get; }

    public string Title => _localizer.Get("logs.workspace.title");

    public string Subtitle => _localizer.Get("logs.workspace.subtitle");

    /// <summary>窄窗口隐藏页面副标题，把高度留给中央日志正文。</summary>
    public string PageSubtitle => IsNarrow ? string.Empty : Subtitle;

    /// <summary>Inspector 选择层内容变化；Shell 只在日志工作区处于活动状态时应用。</summary>
    public event EventHandler<InspectorSelectionContext?>? InspectorSelectionChanged;



    [ObservableProperty]
    private LogSessionRowViewModel? _selectedSession;

    [ObservableProperty]
    private LogSourceViewModel? _selectedSource;

    [ObservableProperty]
    private LogEvidenceRowViewModel? _selectedEvidence;

    [ObservableProperty]
    private LogLineViewModel? _selectedLine;

    [ObservableProperty]
    private bool _follow = true;

    [ObservableProperty]
    private bool _autoWrap;

    [ObservableProperty]
    private bool _isSessionListOpen = true;

    [ObservableProperty]
    private bool _hasNewLines;

    [ObservableProperty]
    private bool _hasOlderLines;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isNarrow;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _sessionSummary = string.Empty;

    [ObservableProperty]
    private string _sourceWarning = string.Empty;

    [ObservableProperty]
    private bool _hasSourceWarning;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _searchStatus = string.Empty;

    [ObservableProperty]
    private bool _searchCaseSensitive;

    [ObservableProperty]
    private bool _searchUseRegex;

    [ObservableProperty]
    private string _threadFilter = string.Empty;

    [ObservableProperty]
    private bool _onlyWarningsAndErrors;

    [ObservableProperty]
    private bool _filterTrace = true;

    [ObservableProperty]
    private bool _filterDebug = true;

    [ObservableProperty]
    private bool _filterInfo = true;

    [ObservableProperty]
    private bool _filterWarn = true;

    [ObservableProperty]
    private bool _filterError = true;

    [ObservableProperty]
    private bool _filterFatal = true;

    [ObservableProperty]
    private string _metadataSearch = string.Empty;

    [ObservableProperty]
    private LogFilterOption _selectedInstanceFilter;

    [ObservableProperty]
    private LogFilterOption _selectedStateFilter;

    [ObservableProperty]
    private LogTimeRangeOption _selectedTimeFilter;

    [ObservableProperty]
    private string _targetUnavailableMessage = string.Empty;

    [ObservableProperty]
    private string _instanceFocusMessage = string.Empty;

    [ObservableProperty]
    private string _loadedRangeText = string.Empty;

    /// <summary>
    /// F-09 S5：运行期停滞提示。它是**不确定**提示：只有“已运行较久 + 一段时间的来源没有增长”
    /// 同时成立才出现，并且永远不表述为“卡死”；控制台模式等采集空洞会说明“静默不代表无输出”。
    /// </summary>
    [ObservableProperty]
    private string _stallHint = string.Empty;

    /// <summary>F-09 S4：启动失败后自动打开日志工作区（只影响导航，不影响采集）。</summary>
    [ObservableProperty]
    private bool _autoOpenOnFailure;

    /// <summary>F-09 S8：当前多选的行数；0 表示只有单选行或没有选择。</summary>
    [ObservableProperty]
    private int _selectedLineCount;

    public bool HasStallHint => !string.IsNullOrWhiteSpace(StallHint);

    public bool HasSelection => SelectedLine is not null || _selectedLines.Count > 0;

    public string CopySelectedText => SelectedLineCount > 1
        ? _localizer.Get("logs.copySelectedCount", SelectedLineCount)
        : _localizer.Get("logs.copySelected");

    public string AutoOpenText => _localizer.Get(
        "logs.autoOpen.toggle",
        _localizer.Get(AutoOpenOnFailure ? "logs.autoOpen.on" : "logs.autoOpen.off"));

    public bool HasTargetUnavailable => !string.IsNullOrWhiteSpace(TargetUnavailableMessage);

    public bool HasInstanceFocus => !string.IsNullOrWhiteSpace(InstanceFocusMessage);

    public bool CanLoadOlder => HasOlderLines &&
                                SelectedSource is { IsAvailable: true, IsEvidence: false } &&
                                _allLines.Count < _maxLines;

    public bool IsEvidenceSource => SelectedSource?.IsEvidence == true;

    public bool ShowSessionSplitter => IsSessionListOpen && !IsNarrow;

    public string FollowText => Follow
        ? _localizer.Get("logs.follow.pause")
        : _localizer.Get("logs.follow.resume");

    public string WrapText => AutoWrap
        ? _localizer.Get("logs.wrap.off")
        : _localizer.Get("logs.wrap.on");

    public string SessionListText => IsSessionListOpen
        ? _localizer.Get("logs.sessionList.hide")
        : _localizer.Get("logs.sessionList.show");

    public string EmptySessionsText => _instanceFocusId is not null
        ? _localizer.Get("logs.empty.instance", _instanceFocusName ?? _instanceFocusId)
        : _localizer.Get("logs.empty.none");

    public string EmptyLinesText => SelectedSession is null
        ? _localizer.Get("logs.reader.noSession")
        : SelectedSource is not { IsAvailable: true }
            ? SelectedSource?.Warning ?? _localizer.Get("logs.reader.sourceUnavailable")
            : _localizer.Get("logs.reader.empty");

    public string ExportText => _localizer.Get("logs.export");

    public void Activate()
    {
        // 会话清单来自磁盘（每个会话一个 manifest），不能在 UI 线程同步枚举；
        // 首屏先呈现加载状态，清单在后台读取完成后统一回填（logs.md §9 生命周期）。
        _sessionsLoad = RefreshSessionsAsync(_targetSessionId is null);
        _timer.Start();
    }

    public void Deactivate() => _timer.Stop();

    /// <summary>
    /// 由 Home 错误入口携带失败启动的 SessionId 打开（logs.md §7.1）。目标不存在时保留说明，
    /// 不静默切换其它会话；用户手动选择后才清除说明。
    /// </summary>
    public void FocusSession(string sessionId)
    {
        var target = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
        _targetSessionId = target;
        SetTargetUnavailable(string.Empty);
        _sessionsLoad = FocusSessionAsync(target);
    }

    private async Task FocusSessionAsync(string? target)
    {
        await RefreshSessionsAsync(selectFallback: target is null).ConfigureAwait(true);
        if (target is null || _disposed)
        {
            return;
        }

        var row = _rows.FirstOrDefault(item => string.Equals(item.Id, target, StringComparison.Ordinal));
        if (row is not null)
        {
            SelectedSession = row;
            return;
        }

        SelectedSession = null;
        SetTargetUnavailable(_localizer.Get("logs.targetUnavailable", target));
    }

    /// <summary>
    /// 实例工作区快捷入口（logs.md §7.2）：打开同一个 Logs Workspace 并默认按实例筛选。
    /// 不创建实例私有的第二套日志列表。
    /// </summary>
    public void FocusInstance(string instanceId, string? instanceName = null)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        _instanceFocusId = instanceId;
        _instanceFocusName = instanceName;
        SetInstanceFocus(_localizer.Get("logs.instanceFocus", instanceName ?? instanceId));
        _targetSessionId = null;
        SetTargetUnavailable(string.Empty);
        _sessionsLoad = FocusInstanceAsync();
    }

    private async Task FocusInstanceAsync()
    {
        await RefreshSessionsAsync(selectFallback: false).ConfigureAwait(true);
        if (_disposed)
        {
            return;
        }

        // 实例快捷入口的默认选中项必须来自筛选后的可见会话；
        // 没有该实例的会话时保持空选择，由空状态说明实例上下文。
        SelectedSession = SessionsView.Cast<LogSessionRowViewModel>().FirstOrDefault();
    }

    public void ClearInstanceFocus()
    {
        if (_instanceFocusId is null)
        {
            return;
        }

        _instanceFocusId = null;
        _instanceFocusName = null;
        SetInstanceFocus(string.Empty);
        _suppressFilterRefresh = true;
        try
        {
            SelectedInstanceFilter = InstanceFilterOptions[0];
        }
        finally
        {
            _suppressFilterRefresh = false;
        }

        ApplySessionFilter();
        OnPropertyChanged(nameof(EmptySessionsText));
    }

    [RelayCommand]
    private void RefreshSessions()
    {
        _sessionsLoad = RefreshSessionsAsync(selectFallback: _targetSessionId is null);
    }

    private async Task RefreshSessionsAsync(bool selectFallback)
    {
        var selectedId = SelectedSession?.Id;
        IReadOnlyList<GameLogSession> sessions;
        try
        {
            // 目录枚举与 manifest 反序列化在后台完成；UI 线程只做集合回填。
            sessions = await Task.Run(() => _sessions.List()).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "读取游戏日志会话失败。");
            return;
        }

        if (_disposed)
        {
            return;
        }

        _rows.Clear();
        foreach (var session in sessions)
        {
            _rows.Add(new LogSessionRowViewModel(session, _localizer));
        }

        RebuildInstanceFilterOptions();

        Sessions.Clear();
        foreach (var row in _rows)
        {
            Sessions.Add(row);
        }

        SessionsView.Refresh();

        if (selectedId is not null)
        {
            SelectedSession = _rows.FirstOrDefault(row => row.Id == selectedId);
        }
        else if (_targetSessionId is { Length: > 0 } target)
        {
            SelectedSession = _rows.FirstOrDefault(row => row.Id == target);
            if (SelectedSession is null)
            {
                SetTargetUnavailable(_localizer.Get("logs.targetUnavailable", target));
            }
        }
        else if (selectFallback)
        {
            SelectedSession = _rows.FirstOrDefault();
        }

        OnPropertyChanged(nameof(EmptySessionsText));
    }

    private void RebuildInstanceFilterOptions()
    {
        var selected = !string.IsNullOrEmpty(SelectedInstanceFilter?.Id)
            ? SelectedInstanceFilter.Id
            : _instanceFocusId ?? string.Empty;
        var options = new List<LogFilterOption>
        {
            new(string.Empty, _localizer.Get("logs.filter.allInstances")),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            var id = row.Manifest.InstanceId ?? string.Empty;
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }

            options.Add(new LogFilterOption(id, row.InstanceName));
        }

        if (_instanceFocusId is { Length: > 0 } focusId && !seen.Contains(focusId))
        {
            options.Add(new LogFilterOption(focusId, _instanceFocusName ?? focusId));
        }

        _suppressFilterRefresh = true;
        try
        {
            InstanceFilterOptions.Clear();
            foreach (var option in options)
            {
                InstanceFilterOptions.Add(option);
            }

            SelectedInstanceFilter = InstanceFilterOptions.FirstOrDefault(option =>
                                     string.Equals(option.Id, selected, StringComparison.Ordinal))
                                 ?? InstanceFilterOptions[0];
        }
        finally
        {
            _suppressFilterRefresh = false;
        }

        ApplySessionFilter();
    }

    private bool FilterSession(object item)
    {
        if (item is not LogSessionRowViewModel row)
        {
            return false;
        }

        if (SelectedInstanceFilter is { Id.Length: > 0 } instance &&
            !string.Equals(row.Manifest.InstanceId, instance.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (SelectedStateFilter is { } state)
        {
            var filter = state.Id switch
            {
                "running" => SessionStateFilter.Running,
                "exited" => SessionStateFilter.Exited,
                "interrupted" => SessionStateFilter.Interrupted,
                "unknown" => SessionStateFilter.Unknown,
                _ => SessionStateFilter.All,
            };
            if (!row.MatchesState(filter))
            {
                return false;
            }
        }

        if (SelectedTimeFilter is { Range: { } range } &&
            DateTimeOffset.UtcNow - row.CreatedAtUtc > range)
        {
            return false;
        }

        var query = MetadataSearch.Trim();
        if (query.Length > 0 &&
            !row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void ApplySessionFilter()
    {
        if (_suppressFilterRefresh)
        {
            return;
        }

        SessionsView.Refresh();

        // 当前选中项被筛掉时保留筛选结果与空状态，不自动改看别的会话。
        if (SelectedSession is { } selected && !SessionsView.Cast<object>().Contains(selected))
        {
            SelectedSession = null;
        }

        OnPropertyChanged(nameof(EmptySessionsText));
    }

    partial void OnSelectedInstanceFilterChanged(LogFilterOption value) => ApplySessionFilter();

    partial void OnSelectedStateFilterChanged(LogFilterOption value) => ApplySessionFilter();

    partial void OnSelectedTimeFilterChanged(LogTimeRangeOption value)
    {
        ApplySessionFilter();
        ApplyLineFilter();
    }

    partial void OnMetadataSearchChanged(string value) => ApplySessionFilter();

    partial void OnFilterTraceChanged(bool value)
    {
        SaveHiddenLevels();
        ApplyLineFilter();
    }

    partial void OnFilterDebugChanged(bool value)
    {
        SaveHiddenLevels();
        ApplyLineFilter();
    }

    partial void OnFilterInfoChanged(bool value)
    {
        SaveHiddenLevels();
        ApplyLineFilter();
    }

    partial void OnFilterWarnChanged(bool value)
    {
        SaveHiddenLevels();
        ApplyLineFilter();
    }

    partial void OnFilterErrorChanged(bool value)
    {
        SaveHiddenLevels();
        ApplyLineFilter();
    }

    partial void OnFilterFatalChanged(bool value)
    {
        SaveHiddenLevels();
        ApplyLineFilter();
    }

    partial void OnOnlyWarningsAndErrorsChanged(bool value)
    {
        SavePreference(LogDisplayKeys.OnlyWarningsAndErrors, value);
        ApplyLineFilter();
    }

    // 线程/Logger 过滤是逐字输入框：合并快速连击，只重建一次可见集合，
    // 避免每键入一个字符就清空并重新填充上千行（logs.md §6.2 输入必须即时响应）。
    partial void OnThreadFilterChanged(string value)
    {
        if (_suppressFilterRefresh)
        {
            return;
        }

        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        var cancellation = new CancellationTokenSource();
        _filterDebounce = cancellation;
        _ = DebounceFilterAsync(cancellation);
    }

    private async Task DebounceFilterAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(180, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed || cancellation.IsCancellationRequested)
        {
            return;
        }

        RebuildVisibleLines();
        await ReapplySearchAsync().ConfigureAwait(true);
    }

    partial void OnSearchCaseSensitiveChanged(bool value) => _ = ReapplySearchAsync();

    partial void OnSearchUseRegexChanged(bool value) => _ = ReapplySearchAsync();

    private void ApplyLineFilter()
    {
        RebuildVisibleLines();
        _ = ReapplySearchAsync();
    }

    partial void OnSelectedSessionChanged(LogSessionRowViewModel? value)
    {
        if (!_suppressFilterRefresh &&
            value is not null &&
            _targetSessionId is { Length: > 0 } target &&
            !string.Equals(value.Id, target, StringComparison.Ordinal))
        {
            _targetSessionId = null;
            SetTargetUnavailable(string.Empty);
        }

        ClearLoadedLines();
        Evidence.Clear();
        Sources.Clear();
        _evidenceArtifact = null;
        SelectedEvidence = null;
        SearchText = string.Empty;
        // F-09 S5：切换会话后停滞提示必须重新判定，不能沿用上一个会话的静默状态。
        SetStallHint(string.Empty);
        UpdateLineSelection([]);

        if (value is null)
        {
            SessionSummary = string.Empty;
            SetSourceWarning(string.Empty);
            SetInspectorSelection(BuildInspectorContext());
            return;
        }

        SessionSummary = $"{value.InstanceName} · {value.StartedText} · {value.StateText}";
        _sessionSourcesLoad = LoadSourcesAsync(value);
        UpdateInspector(value);
    }

    private bool IsCurrentSession(LogSessionRowViewModel row) =>
        ReferenceEquals(SelectedSession, row);

    /// <summary>
    /// 读取来源/证据清单需要访问磁盘（文件存在性、大小与写入时间）。放到后台线程，
    /// 避免在会话切换时阻塞 UI；返回后再回到 UI 线程填充集合。
    /// </summary>
    private async Task LoadSourcesAsync(LogSessionRowViewModel row)
    {
        IReadOnlyList<SessionLogSourceInfo> sources;
        IReadOnlyList<SessionEvidenceItem> evidence;
        try
        {
            (sources, evidence) = await Task.Run(() => (
                _reader.DescribeSources(row.Session),
                _reader.DescribeEvidence(row.Session))).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "读取会话来源失败：{SessionId}", row.Id);
            return;
        }

        if (_disposed || !IsCurrentSession(row))
        {
            return;
        }

        foreach (var source in sources)
        {
            Sources.Add(new LogSourceViewModel(
                source.Kind,
                _localizer.Get("logs.source." + SourceKey(source.Kind)),
                source.Availability == SessionSourceAvailability.Available,
                _localizer.Get("logs.source.availability." + source.Availability) +
                (source.Size is { } size ? $" · {FormatBytes(size)}" : string.Empty),
                source.Warning,
                source.Ownership));
        }

        foreach (var item in evidence)
        {
            Evidence.Add(new LogEvidenceRowViewModel(item, _localizer));
        }

        Sources.Add(new LogSourceViewModel(
            SessionLogSourceKind.Evidence,
            _localizer.Get("logs.source.evidence"),
            evidence.Count > 0,
            evidence.Count == 0
                ? _localizer.Get("logs.source.availability.Missing")
                : _localizer.Get("logs.evidence.count", evidence.Count),
            evidence.Count == 0 ? _localizer.Get("logs.evidence.none") : null,
            null));

        var preferred = Sources.FirstOrDefault(source =>
                            source.IsGameLog &&
                            source.IsAvailable)
                        ?? Sources.FirstOrDefault(source => source.IsAvailable)
                        ?? Sources[0];
        SelectedSource = preferred;
    }

    partial void OnSelectedSourceChanged(LogSourceViewModel? value)
    {
        OnPropertyChanged(nameof(IsEvidenceSource));
        OnPropertyChanged(nameof(CanLoadOlder));
        SetSourceWarning(value?.Warning ?? string.Empty);

        // F-09 S5：换来源重新判定，避免把另一个来源的静默算到当前来源上。
        SetStallHint(string.Empty);

        if (value is null)
        {
            return;
        }

        _selectedEvidence = null;
        _sourceLoad = LoadSourceAsync(value);
    }

    /// <summary>
    /// 等待当前来源的首屏读取完成；供测试与需要确定时序的调用方使用。
    /// 先等会话清单（磁盘枚举），再等来源清单（文件状态），最后等来源正文读取。
    /// </summary>
    public async Task WaitForSourceLoadAsync()
    {
        // 会话清单与来源清单可能因一次刷新/切换而替换，逐层等待当前链路完成。
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var sessions = _sessionsLoad;
            var sources = _sessionSourcesLoad;
            var content = _sourceLoad;
            await sessions.ConfigureAwait(true);
            await sources.ConfigureAwait(true);
            await content.ConfigureAwait(true);
            if (ReferenceEquals(sessions, _sessionsLoad)
                && ReferenceEquals(sources, _sessionSourcesLoad)
                && ReferenceEquals(content, _sourceLoad))
            {
                return;
            }
        }
    }

    private async Task LoadSourceAsync(LogSourceViewModel source)
    {
        if (SelectedSession is not { } row)
        {
            return;
        }

        var generation = ++_loadGeneration;
        ClearLoadedLines();
        _evidenceArtifact = null;

        if (!source.IsAvailable)
        {
            SetSourceWarning(source.Warning ?? _localizer.Get("logs.reader.sourceUnavailable"));
            return;
        }

        try
        {
            if (source.IsEvidence)
            {
                if (SelectedEvidence is not { IsReadable: true } evidence)
                {
                    SelectedEvidence = Evidence.FirstOrDefault(item => item.IsReadable);
                    evidence = SelectedEvidence;
                }

                if (evidence is null)
                {
                    StatusMessage = _localizer.Get("logs.evidence.noReadable");
                    return;
                }

                _evidenceArtifact = evidence.Artifact;
                var chunk = await _reader.ReadEvidenceAsync(row.Session, evidence.Artifact, InitialChunkBytes);
                if (generation != _loadGeneration || !IsCurrent(row, source))
                {
                    return;
                }

                _sourceWindowStart = 0;
                _sourceOffset = 0;
                HasOlderLines = false;
                await AppendLinesAsync(chunk.Lines, initial: true).ConfigureAwait(true);
                if (generation != _loadGeneration)
                {
                    return;
                }

                RequestScrollToEnd();
                StatusMessage = evidence.Size is { } size && chunk.EndOffset < size
                    ? _localizer.Get("logs.evidence.truncated")
                    : _localizer.Get("logs.evidence.preview", evidence.KindText);
                return;
            }

            var loaded = await _reader.ReadLatestAsync(row.Session, source.Kind, InitialChunkBytes);
            if (generation != _loadGeneration || !IsCurrent(row, source))
            {
                return;
            }

            _sourceWindowStart = loaded.StartOffset;
            _sourceOffset = loaded.EndOffset;
            HasOlderLines = loaded.HasMoreBefore;
            await AppendLinesAsync(loaded.Lines, initial: true).ConfigureAwait(true);
            if (generation != _loadGeneration)
            {
                return;
            }

            StatusMessage = loaded.Error is not null
                ? _localizer.Get("logs.readError", loaded.Error)
                : loaded.SkippedPartialLine
                    ? _localizer.Get("logs.loadedTailPartial", _allLines.Count)
                    : _localizer.Get("logs.loadedTail", _allLines.Count);
            UpdateLoadedRangeText();
            if (Follow)
            {
                RequestScrollToEnd();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "读取日志来源失败：{SessionId}", row.Id);
            StatusMessage = _localizer.Get("logs.readError", exception.Message);
        }
    }

    private static string SourceKey(SessionLogSourceKind kind) =>
        kind switch
        {
            SessionLogSourceKind.GameLog => "gameLog",
            SessionLogSourceKind.Stdout => "stdout",
            SessionLogSourceKind.Stderr => "stderr",
            SessionLogSourceKind.Evidence => "evidence",
            _ => "gameLog",
        };

    private bool IsCurrent(LogSessionRowViewModel row, LogSourceViewModel source) =>
        ReferenceEquals(SelectedSession, row) && ReferenceEquals(SelectedSource, source);

    private void ClearLoadedLines()
    {
        _allLines.Clear();
        Lines.Clear();
        _matches.Clear();
        _currentMatchIndex = -1;
        SearchStatus = string.Empty;
        HasOlderLines = false;
        HasNewLines = false;
        _sourceOffset = 0;
        _sourceWindowStart = 0;
        LoadedRangeText = string.Empty;
    }

    /// <summary>
    /// 增量追加日志行。解析（每行最多 4 个正则）放到后台线程；UI 线程只做有界增量插入，
    /// 不再每次清空并重建整个可见集合，也不逐行 <c>RemoveAt(0)</c>（logs.md §9 背压与锚点）。
    /// </summary>
    private async Task AppendLinesAsync(IReadOnlyList<string> rawLines, bool initial)
    {
        if (rawLines.Count == 0)
        {
            return;
        }

        var createdAt = SelectedSession?.Manifest.CreatedAtUtc ?? DateTimeOffset.UtcNow;
        var startNumber = _allLines.Count + 1;
        var generation = _loadGeneration;

        var parsed = await Task.Run(() =>
        {
            var list = new List<LogLineViewModel>(rawLines.Count);
            var number = startNumber;
            foreach (var raw in rawLines)
            {
                list.Add(new LogLineViewModel(number++, LogLineParser.Parse(raw, createdAt)));
            }

            return list;
        }).ConfigureAwait(true);

        // 读取期间会话/来源可能已切换：过期结果直接丢弃，不能写进新视图。
        if (_disposed || generation != _loadGeneration)
        {
            return;
        }

        _allLines.AddRange(parsed);
        var excess = _allLines.Count - _maxLines;
        HashSet<LogLineViewModel>? removed = null;
        var trimmed = false;
        if (excess > 0)
        {
            // 一次性移除超出上限的头部（批量），并同步移除这些行在可见集合中的副本。
            removed = new HashSet<LogLineViewModel>(_allLines.Take(excess));
            _allLines.RemoveRange(0, excess);
            for (var index = Lines.Count - 1; index >= 0; index--)
            {
                if (removed.Contains(Lines[index]))
                {
                    Lines.RemoveAt(index);
                }
            }

            trimmed = true;
            RenumberLines();
        }

        foreach (var line in parsed)
        {
            if (removed is not null && removed.Contains(line))
            {
                continue;
            }

            if (MatchesFilters(line))
            {
                Lines.Add(line);
            }
        }

        if (trimmed)
        {
            HasOlderLines = false;
            StatusMessage = _localizer.Get("logs.trimmed", _allLines.Count);
        }

        OnPropertyChanged(nameof(CanLoadOlder));

        if (!initial && !Follow)
        {
            HasNewLines = true;
        }
        else if (!initial)
        {
            RequestScrollToEnd();
        }

        UpdateLoadedRangeText();
        await ReapplySearchAsync().ConfigureAwait(true);
    }

    private void RenumberLines()
    {
        for (var index = 0; index < _allLines.Count; index++)
        {
            _allLines[index].DisplayNumber = index + 1;
        }
    }

    private void RebuildVisibleLines()
    {
        Lines.Clear();
        foreach (var line in _allLines)
        {
            if (MatchesFilters(line))
            {
                Lines.Add(line);
            }
        }

        OnPropertyChanged(nameof(CanLoadOlder));
    }

    private bool MatchesFilters(LogLineViewModel line)
    {
        if (OnlyWarningsAndErrors && !line.IsWarning && !line.IsError)
        {
            return false;
        }

        var allowed = line.Level switch
        {
            Microsoft.Extensions.Logging.LogLevel.Trace => FilterTrace,
            Microsoft.Extensions.Logging.LogLevel.Debug => FilterDebug,
            Microsoft.Extensions.Logging.LogLevel.Information => FilterInfo,
            Microsoft.Extensions.Logging.LogLevel.Warning => FilterWarn,
            Microsoft.Extensions.Logging.LogLevel.Error => FilterError,
            Microsoft.Extensions.Logging.LogLevel.Critical => FilterFatal,
            // 无法解析级别的行不被丢弃：用户仍然能看到原始文本。
            _ => true,
        };
        if (!allowed)
        {
            return false;
        }

        if (SelectedTimeFilter is { Range: { } range } &&
            line.Timestamp is { } timestamp &&
            DateTimeOffset.Now - timestamp > range)
        {
            return false;
        }

        var thread = ThreadFilter.Trim();
        if (thread.Length > 0 &&
            !line.Thread.Contains(thread, StringComparison.OrdinalIgnoreCase) &&
            !line.Logger.Contains(thread, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void UpdateLoadedRangeText()
    {
        LoadedRangeText = _allLines.Count == 0
            ? string.Empty
            : _localizer.Get("logs.range", _allLines[0].DisplayNumber, _allLines[^1].DisplayNumber, _allLines.Count);
    }

    [RelayCommand]
    private void ToggleFollow()
    {
        Follow = !Follow;
        if (Follow)
        {
            HasNewLines = false;
            _ = DrainPendingAsync();
            RequestScrollToEnd();
        }
    }

    [RelayCommand]
    private void ToggleWrap() => AutoWrap = !AutoWrap;

    /// <summary>F-09 S4：切换“启动失败后自动打开日志”。只改导航偏好，不影响采集。</summary>
    [RelayCommand]
    private void ToggleAutoOpenOnFailure() => AutoOpenOnFailure = !AutoOpenOnFailure;

    [RelayCommand]
    private void ToggleSessionList()
    {
        IsSessionListOpen = !IsSessionListOpen;
        OnPropertyChanged(nameof(ShowSessionSplitter));
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressFilterRefresh = true;
        try
        {
            OnlyWarningsAndErrors = false;
            FilterTrace = true;
            FilterDebug = true;
            FilterInfo = true;
            FilterWarn = true;
            FilterError = true;
            FilterFatal = true;
            ThreadFilter = string.Empty;
            MetadataSearch = string.Empty;
            SelectedStateFilter = StateFilterOptions[0];
            SelectedTimeFilter = TimeFilterOptions[0];
            SelectedInstanceFilter = InstanceFilterOptions[0];
            _instanceFocusId = null;
            _instanceFocusName = null;
            SetInstanceFocus(string.Empty);
        }
        finally
        {
            _suppressFilterRefresh = false;
        }

        ApplySessionFilter();
        RebuildVisibleLines();
        OnPropertyChanged(nameof(EmptySessionsText));
    }

    [RelayCommand]
    private async Task SearchAsync() => await ReapplySearchAsync().ConfigureAwait(true);

    /// <summary>
    /// 重新计算命中。小集合同步完成（保持低延迟与确定性），大集合移出 UI 线程并支持取消；
    /// 新查询到来时旧查询被取消，旧结果不会写回（logs.md §6.2）。
    /// </summary>
    private async Task ReapplySearchAsync()
    {
        var version = ++_searchVersion;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;

        // 只清除上一轮命中标记（O(命中数)），不要每次追加都遍历全部已加载行。
        foreach (var line in _matches)
        {
            line.IsMatch = false;
            line.IsCurrentMatch = false;
        }

        _matches.Clear();
        _currentMatchIndex = -1;

        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            SearchStatus = string.Empty;
            return;
        }

        Func<string, bool> predicate;
        if (SearchUseRegex)
        {
            Regex regex;
            try
            {
                regex = new Regex(
                    query,
                    SearchCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException exception)
            {
                SearchStatus = _localizer.Get("logs.search.invalid", exception.Message);
                return;
            }

            predicate = text =>
            {
                try
                {
                    return regex.IsMatch(text);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            };
        }
        else
        {
            var comparison = SearchCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            predicate = text => text.Contains(query, comparison);
        }

        var snapshot = _allLines.ToArray();
        LogLineViewModel[] hits;
        if (snapshot.Length <= SynchronousSearchThreshold)
        {
            hits = snapshot
                .Where(line => MatchesFilters(line) && predicate(line.Raw))
                .ToArray();
        }
        else
        {
            var cancellation = new CancellationTokenSource();
            _searchCancellation = cancellation;
            try
            {
                hits = await Task.Run(
                    () =>
                    {
                        var result = new List<LogLineViewModel>();
                        foreach (var line in snapshot)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            if (MatchesFilters(line) && predicate(line.Raw))
                            {
                                result.Add(line);
                            }
                        }

                        return result.ToArray();
                    },
                    cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (version != _searchVersion || _disposed)
        {
            return;
        }

        foreach (var line in hits)
        {
            line.IsMatch = true;
        }

        _matches.AddRange(hits);
        SearchStatus = _matches.Count == 0
            ? _localizer.Get("logs.search.none")
            : HasOlderLines || _maxLines < MaxLines
                ? _localizer.Get("logs.search.atLeast", _matches.Count)
                : _localizer.Get("logs.search.count", _matches.Count);

        if (_matches.Count > 0)
        {
            SelectMatch(0);
        }
    }

    [RelayCommand]
    private void NextMatch() => MoveMatch(1);

    [RelayCommand]
    private void PreviousMatch() => MoveMatch(-1);

    private void MoveMatch(int direction)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        var next = _currentMatchIndex < 0
            ? (direction > 0 ? 0 : _matches.Count - 1)
            : (_currentMatchIndex + direction + _matches.Count) % _matches.Count;
        SelectMatch(next);
    }

    private void SelectMatch(int index)
    {
        if (index < 0 || index >= _matches.Count)
        {
            return;
        }

        if (_currentMatchIndex >= 0 && _currentMatchIndex < _matches.Count)
        {
            _matches[_currentMatchIndex].IsCurrentMatch = false;
        }

        _currentMatchIndex = index;
        var line = _matches[index];
        line.IsCurrentMatch = true;
        SelectedLine = line;
        OnPropertyChanged(nameof(SearchStatus));
        RequestScrollToLine(line);
    }

    [RelayCommand]
    private async Task LoadOlderAsync()
    {
        if (!CanLoadOlder || SelectedSession is not { } row || SelectedSource is not { } source)
        {
            return;
        }

        try
        {
            var chunk = source.IsEvidence
                ? await _reader.ReadEvidenceAsync(row.Session, _evidenceArtifact!, InitialChunkBytes)
                : await _reader.ReadBeforeAsync(row.Session, source.Kind, _sourceWindowStart, InitialChunkBytes);
            if (chunk.Lines.Count == 0)
            {
                HasOlderLines = false;
                return;
            }

            _sourceWindowStart = chunk.StartOffset;
            HasOlderLines = chunk.HasMoreBefore;
            var createdAt = row.Manifest.CreatedAtUtc;
            var prepended = await Task.Run(() => chunk.Lines
                .Select(raw => LogLineParser.Parse(raw, createdAt))
                .Select(parsed => new LogLineViewModel(0, parsed))
                .ToList()).ConfigureAwait(true);
            if (SelectedSession != row || SelectedSource != source)
            {
                return;
            }

            _allLines.InsertRange(0, prepended);
            RenumberLines();
            RebuildVisibleLines();
            await ReapplySearchAsync().ConfigureAwait(true);
            UpdateLoadedRangeText();
            StatusMessage = _localizer.Get("logs.loadedOlder", prepended.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = _localizer.Get("logs.readError", exception.Message);
        }
    }

    [RelayCommand]
    private async Task PreviewEvidenceAsync(LogEvidenceRowViewModel? evidence)
    {
        if (evidence is null || SelectedSession is not { } row)
        {
            return;
        }

        SelectedEvidence = evidence;
        if (!evidence.IsReadable)
        {
            StatusMessage = _localizer.Get("logs.evidence.unreadable", evidence.KindText);
            return;
        }

        try
        {
            var chunk = await _reader.ReadEvidenceAsync(row.Session, evidence.Artifact, InitialChunkBytes);
            ClearLoadedLines();
            _evidenceArtifact = evidence.Artifact;
            await AppendLinesAsync(chunk.Lines, initial: true).ConfigureAwait(true);
            RequestScrollToEnd();
            StatusMessage = _localizer.Get("logs.evidence.preview", evidence.KindText);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = _localizer.Get("logs.readError", exception.Message);
        }
    }

    [RelayCommand]
    private void CopySessionId(LogSessionRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(row.Id);
            StatusMessage = _localizer.Get("logs.copiedSessionId", row.Id);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or System.Threading.ThreadStateException)
        {
            StatusMessage = _localizer.Get("logs.clipboardFailed", exception.Message);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedSession is not { } row)
        {
            return;
        }

        OpenSessionFolder(row);
    }

    [RelayCommand]
    private void OpenSessionFolder(LogSessionRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!Directory.Exists(row.DirectoryPath))
        {
            StatusMessage = _localizer.Get("logs.folderUnavailable");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(row.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            StatusMessage = _localizer.Get("logs.folderFailed", exception.Message);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (SelectedSession is not { } row)
        {
            return;
        }

        await ExportSessionCoreAsync(row);
    }

    [RelayCommand]
    private async Task ExportSessionAsync(LogSessionRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        await ExportSessionCoreAsync(row);
    }

    private async Task ExportSessionCoreAsync(LogSessionRowViewModel row)
    {
        if (IsBusy)
        {
            return;
        }

        var defaultPath = Path.Combine(_paths.LogsDirectory, "diagnostics", $"ecl-session-{row.Id}.zip");
        var dialog = new LogExportDialogViewModel(
            row,
            Evidence,
            defaultPath,
            _localizer);
        var accepted = await ShowExportDialogAsync(row, dialog);
        if (!accepted)
        {
            return;
        }

        await RunExportAsync(row, defaultPath, new SessionExportOptions { Redact = dialog.Redact });
    }

    private async Task<bool> ShowExportDialogAsync(LogSessionRowViewModel row, LogExportDialogViewModel dialog)
    {
        try
        {
            var result = await _dialogs.ShowAsync(new DialogRequest
            {
                Title = _localizer.Get("logs.export.title"),
                Content = dialog,
                InitialFocus = DialogInitialFocus.Primary,
                Severity = DialogSeverity.Warning,
                MinWidth = 480,
                MaxWidth = 680,
                MinHeight = 360,
                MaxHeight = 620,
            });
            return result.IsPrimary;
        }
        catch (DialogHostUnavailableException)
        {
            // 宿主尚未附加时回退到兼容确认；仍显示范围与隐私提示，不静默导出。
            return _dialogService.Confirm(
                _localizer.Get("logs.export.title"),
                _localizer.Get("logs.export.fallbackConfirm", row.Id, dialog.OutputPath));
        }
    }

    private async Task RunExportAsync(
        LogSessionRowViewModel row,
        string outputPath,
        SessionExportOptions options)
    {
        IsBusy = true;
        using var cancellation = new CancellationTokenSource();
        using var operation = _operations.Begin(new OperationStart
        {
            Kind = OperationKind.Export,
            Title = _localizer.Get("logs.export.taskTitle", row.InstanceName),
            StageId = "session-export",
            Source = "ECL",
            CanCancel = true,
            CanRetry = false,
        });
        _operations.AttachControl(operation.Id, new OperationControl(cancellation));
        operation.Report(new OperationProgressUpdate
        {
            StageId = "session-export",
            StageText = _localizer.Get("logs.export.running"),
            CurrentItem = row.Id,
        });

        try
        {
            var result = await _exporter.ExportAsync(row.Id, outputPath, options, cancellation.Token);
            if (result.IsSuccess)
            {
                var exported = result.Value!;
                operation.Complete(new OperationProgressUpdate
                {
                    StageId = "session-export",
                    StageText = _localizer.Get("logs.export.completed"),
                });
                StatusMessage = exported.Missing.Count == 0
                    ? _localizer.Get("logs.export.success", exported.OutputPath)
                    : _localizer.Get("logs.export.successWithMissing", exported.OutputPath, exported.Missing.Count);
            }
            else
            {
                operation.Fail(new OperationError(
                    OperationErrorKind.LocalIO,
                    result.Error?.Code ?? "session.export.failed",
                    result.Error?.Message ?? _localizer.Get("logs.export.failed", string.Empty)));
                StatusMessage = _localizer.Get("logs.export.failed", result.Error?.Message ?? string.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            StatusMessage = _localizer.Get("logs.export.cancelled");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            operation.Fail(new OperationError(
                OperationErrorKind.LocalIO,
                "session.export.failed",
                exception.Message));
            StatusMessage = _localizer.Get("logs.export.failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CopyLine(LogLineViewModel? line)
    {
        if (line is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(line.CopyText);
            StatusMessage = _localizer.Get("logs.copiedLines", 1);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or System.Threading.ThreadStateException)
        {
            StatusMessage = _localizer.Get("logs.clipboardFailed", exception.Message);
        }
    }

    [RelayCommand]
    private void CopyLines()
    {
        var text = SelectionCopyText();
        if (text.Length == 0)
        {
            StatusMessage = _localizer.Get("logs.selectionHint");
            return;
        }

        CopyToClipboard(text, SelectedLines().Count);
    }

    /// <summary>F-09 S8：复制当前可见范围（过滤/搜索后的视图范围），不改动原始日志。</summary>
    [RelayCommand]
    private void CopyVisibleLines()
    {
        if (Lines.Count == 0)
        {
            StatusMessage = _localizer.Get("logs.selectionHint");
            return;
        }

        CopyToClipboard(VisibleCopyText(), Lines.Count);
    }

    /// <summary>多选取中的原始文本（按显示顺序）；没有多选时退回当前单行。</summary>
    public string SelectionCopyText() =>
        string.Join(Environment.NewLine, SelectedLines().Select(line => line.CopyText));

    /// <summary>当前可见范围的原始文本（按显示顺序）。</summary>
    public string VisibleCopyText() =>
        string.Join(Environment.NewLine, Lines.Select(line => line.CopyText));

    /// <summary>
    /// F-09 S8：视图的多选结果。WPF <c>SelectedItems</c> 不可直接绑定，由视图的
    /// <c>SelectionChanged</c> 推进来；单选仍更新 <see cref="SelectedLine"/> 以保持既有语义。
    /// </summary>
    public void UpdateLineSelection(IReadOnlyList<LogLineViewModel> lines)
    {
        _selectedLines.Clear();
        _selectedLines.AddRange(lines);
        SelectedLineCount = _selectedLines.Count;
        SelectedLine = _selectedLines.Count > 0 ? _selectedLines[^1] : null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CopySelectedText));
    }

    /// <summary>多选结果按显示顺序返回；没有多选时退回当前单行。</summary>
    private List<LogLineViewModel> SelectedLines()
    {
        if (_selectedLines.Count > 0)
        {
            return _selectedLines.OrderBy(line => line.DisplayNumber).ToList();
        }

        return SelectedLine is { } line ? [line] : [];
    }

    private void CopyToClipboard(string text, int count)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusMessage = _localizer.Get("logs.copiedLines", count);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or System.Threading.ThreadStateException)
        {
            StatusMessage = _localizer.Get("logs.clipboardFailed", exception.Message);
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _ = PollAsync();
    }

    /// <summary>测试与定时器共用的增量轮询入口。</summary>
    public async Task PollAsync()
    {
        if (_disposed || _polling || !_sourceLoad.IsCompleted || SelectedSession is not { } row)
        {
            return;
        }

        _polling = true;
        try
        {
            var current = _sessions.Get(row.Id);
            if (current is null)
            {
                SetTargetUnavailable(_localizer.Get("logs.targetUnavailable", row.Id));
                SelectedSession = null;
                return;
            }

            if (current.Manifest.UpdatedAtUtc != row.Manifest.UpdatedAtUtc ||
                current.Manifest.State != row.Manifest.State)
            {
                row.Refresh(current.Manifest, current.DirectoryPath);
                SessionSummary = $"{row.InstanceName} · {row.StartedText} · {row.StateText}";
                UpdateInspector(row);
            }

            if (SelectedSource is not { IsAvailable: true, IsEvidence: false } source)
            {
                return;
            }

            await DrainPendingAsync();
            UpdateStallHint(row, source);
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// F-09 S5：运行期停滞提示。只用两个信号：来源文件的最后写入时间（证据）与该会话已运行较久。
    /// 静默本身不是卡死证据（加载、暂停、GC 都会静默），因此提示必须带不确定性；
    /// 控制台模式没有 stdout/stderr 采集时更要说清“静默不代表无输出”。
    /// </summary>
    private void UpdateStallHint(LogSessionRowViewModel row, LogSourceViewModel source)
    {
        var manifest = row.Manifest;
        if (manifest.State is not (GameSessionState.Running or GameSessionState.Starting))
        {
            SetStallHint(string.Empty);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var startedAt = manifest.ProcessStartedAtUtc ?? manifest.CreatedAtUtc;
        if (now - startedAt < HomeLaunchPresentation.LongRunningAfter)
        {
            SetStallHint(string.Empty);
            return;
        }

        SessionLogSourceInfo? info = null;
        try
        {
            info = _reader.DescribeSources(row.Session)
                .FirstOrDefault(item => item.Kind == source.Kind);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "读取来源状态失败：{SessionId}", row.Id);
        }

        var consoleMode = string.Equals(manifest.CaptureMode, SessionCaptureModes.Console, StringComparison.Ordinal);
        if (consoleMode && info is not { Availability: SessionSourceAvailability.Available })
        {
            // 没有进程输出采集：静默不代表进程没有输出，不能把空洞说成“无输出”。
            SetStallHint(_localizer.Get("logs.stall.captureGap"));
            return;
        }

        if (info?.LastWriteTimeUtc is not { } lastWrite)
        {
            SetStallHint(string.Empty);
            return;
        }

        var silentFor = now - lastWrite;
        if (silentFor < HomeLaunchPresentation.SilentOutputAfter)
        {
            SetStallHint(string.Empty);
            return;
        }

        var seconds = (int)silentFor.TotalSeconds;
        if (consoleMode)
        {
            SetStallHint(_localizer.Get("logs.stall.captureGap"));
            return;
        }

        SetStallHint(manifest.ProcessId is null
            ? _localizer.Get("logs.stall.unverified", seconds)
            : _localizer.Get("logs.stall.silent", seconds));
    }

    private void SetStallHint(string hint)
    {
        if (string.Equals(StallHint, hint, StringComparison.Ordinal))
        {
            return;
        }

        StallHint = hint;
        OnPropertyChanged(nameof(HasStallHint));
    }

    #region 显示偏好（F-09 S4/S8）

    private void LoadDisplayPreferences()
    {
        _loadingDisplayPreferences = true;
        try
        {
            AutoWrap = ReadPreference(LogDisplayKeys.AutoWrap, defaultValue: false);
            Follow = ReadPreference(LogDisplayKeys.Follow, defaultValue: true);
            OnlyWarningsAndErrors = ReadPreference(LogDisplayKeys.OnlyWarningsAndErrors, defaultValue: false);
            AutoOpenOnFailure = ReadPreference(LogDisplayKeys.AutoOpenOnFailure, defaultValue: false);

            var hidden = ParseHiddenLevels(_settings.GetRawString(LogDisplayKeys.HiddenLevels));
            FilterTrace = !hidden.Contains(Microsoft.Extensions.Logging.LogLevel.Trace);
            FilterDebug = !hidden.Contains(Microsoft.Extensions.Logging.LogLevel.Debug);
            FilterInfo = !hidden.Contains(Microsoft.Extensions.Logging.LogLevel.Information);
            FilterWarn = !hidden.Contains(Microsoft.Extensions.Logging.LogLevel.Warning);
            FilterError = !hidden.Contains(Microsoft.Extensions.Logging.LogLevel.Error);
            FilterFatal = !hidden.Contains(Microsoft.Extensions.Logging.LogLevel.Critical);
        }
        finally
        {
            _loadingDisplayPreferences = false;
        }
    }

    /// <summary>未显式保存时使用页面默认值：不把“从未设置”解释成 false。</summary>
    private bool ReadPreference(string key, bool defaultValue) =>
        _settings.GetSource(key) == SettingsValueSource.Default
            ? defaultValue
            : _settings.Get<bool>(key);

    private void SavePreference(string key, bool value)
    {
        if (_loadingDisplayPreferences)
        {
            return;
        }

        try
        {
            _settings.TrySetRaw(key, value ? "true" : "false", null, out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "保存日志显示偏好失败：{Key}", key);
        }
    }

    private void SaveHiddenLevels()
    {
        if (_loadingDisplayPreferences)
        {
            return;
        }

        var hidden = new List<string>();
        if (!FilterTrace)
        {
            hidden.Add(nameof(Microsoft.Extensions.Logging.LogLevel.Trace));
        }

        if (!FilterDebug)
        {
            hidden.Add(nameof(Microsoft.Extensions.Logging.LogLevel.Debug));
        }

        if (!FilterInfo)
        {
            hidden.Add(nameof(Microsoft.Extensions.Logging.LogLevel.Information));
        }

        if (!FilterWarn)
        {
            hidden.Add(nameof(Microsoft.Extensions.Logging.LogLevel.Warning));
        }

        if (!FilterError)
        {
            hidden.Add(nameof(Microsoft.Extensions.Logging.LogLevel.Error));
        }

        if (!FilterFatal)
        {
            hidden.Add(nameof(Microsoft.Extensions.Logging.LogLevel.Critical));
        }

        try
        {
            _settings.TrySetRaw(
                LogDisplayKeys.HiddenLevels,
                string.Join(LogDisplayKeys.HiddenLevelsSeparator, hidden),
                null,
                out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "保存日志显示偏好失败：{Key}", LogDisplayKeys.HiddenLevels);
        }
    }

    /// <summary>未知级别名被忽略：旧配置或手工编辑不得让过滤器失效。</summary>
    private static HashSet<Microsoft.Extensions.Logging.LogLevel> ParseHiddenLevels(string? raw)
    {
        var hidden = new HashSet<Microsoft.Extensions.Logging.LogLevel>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return hidden;
        }

        foreach (var part in raw.Split(
                     LogDisplayKeys.HiddenLevelsSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(part, ignoreCase: true, out var level) &&
                Enum.IsDefined(level))
            {
                hidden.Add(level);
            }
        }

        return hidden;
    }

    partial void OnAutoWrapChanged(bool value)
    {
        SavePreference(LogDisplayKeys.AutoWrap, value);
        OnPropertyChanged(nameof(WrapText));
    }

    partial void OnFollowChanged(bool value)
    {
        SavePreference(LogDisplayKeys.Follow, value);
        OnPropertyChanged(nameof(FollowText));
    }

    partial void OnAutoOpenOnFailureChanged(bool value)
    {
        SavePreference(LogDisplayKeys.AutoOpenOnFailure, value);
        OnPropertyChanged(nameof(AutoOpenText));
    }

    partial void OnSelectedLineCountChanged(int value) => OnPropertyChanged(nameof(CopySelectedText));

    #endregion

    private async Task DrainPendingAsync()
    {
        if (SelectedSession is not { } row ||
            SelectedSource is not { IsAvailable: true, IsEvidence: false } source)
        {
            return;
        }

        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var chunk = await _reader.ReadFromAsync(row.Session, source.Kind, _sourceOffset, PollChunkBytes);
                if (!IsCurrent(row, source))
                {
                    return;
                }

                if (chunk.Error is not null)
                {
                    SetSourceWarning(chunk.Error);
                    return;
                }

                if (_sourceOffset > 0 && chunk.StartOffset == 0 && chunk.EndOffset < _sourceOffset)
                {
                    // 文件被截断/轮转：重建视图而不是把新内容拼到旧偏移后。
                    HasOlderLines = false;
                    _sourceWindowStart = 0;
                    ClearLoadedLines();
                    await LoadSourceAsync(source);
                    StatusMessage = _localizer.Get("logs.rotated");
                    return;
                }

                if (chunk.Lines.Count > 0)
                {
                    _sourceOffset = chunk.EndOffset;
                    await AppendLinesAsync(chunk.Lines, initial: false).ConfigureAwait(true);
                }
                else
                {
                    _sourceOffset = chunk.EndOffset;
                }

                if (!chunk.HasMoreAfter)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetSourceWarning(exception.Message);
        }
    }

    private void OnPressureChanged(object? sender, MemoryPressureLevel level)
    {
        switch (level)
        {
            case MemoryPressureLevel.Critical:
                _maxLines = 400;
                _timer.Interval = TimeSpan.FromSeconds(4);
                TrimToLimit();
                break;
            case MemoryPressureLevel.Elevated:
                _maxLines = 800;
                _timer.Interval = TimeSpan.FromSeconds(2);
                TrimToLimit();
                break;
            case MemoryPressureLevel.Recovery:
                _maxLines = 1200;
                _timer.Interval = TimeSpan.FromSeconds(2);
                TrimToLimit();
                break;
            default:
                _maxLines = MaxLines;
                _timer.Interval = TimeSpan.FromSeconds(1);
                break;
        }
    }

    private void TrimToLimit()
    {
        if (_allLines.Count <= _maxLines)
        {
            return;
        }

        var excess = _allLines.Count - _maxLines;
        var removed = new HashSet<LogLineViewModel>(_allLines.Take(excess));
        _allLines.RemoveRange(0, excess);
        for (var index = Lines.Count - 1; index >= 0; index--)
        {
            if (removed.Contains(Lines[index]))
            {
                Lines.RemoveAt(index);
            }
        }

        HasOlderLines = false;
        RenumberLines();
        _ = ReapplySearchAsync();
        UpdateLoadedRangeText();
        StatusMessage = _localizer.Get("logs.trimmed", _allLines.Count);
    }

    private void SetTargetUnavailable(string message)
    {
        TargetUnavailableMessage = message;
        OnPropertyChanged(nameof(HasTargetUnavailable));
    }

    private void SetInstanceFocus(string message)
    {
        InstanceFocusMessage = message;
        OnPropertyChanged(nameof(HasInstanceFocus));
        OnPropertyChanged(nameof(EmptySessionsText));
    }

    private void SetSourceWarning(string message)
    {
        SourceWarning = message;
        HasSourceWarning = !string.IsNullOrWhiteSpace(message);
    }

    public InspectorSelectionContext? BuildInspectorContext()
    {
        if (SelectedSession is not { } row)
        {
            return null;
        }

        var manifest = row.Manifest;
        var fields = new List<InspectorSelectionField>
        {
            new(_localizer.Get("logs.inspector.sessionId"), manifest.SessionId),
            new(_localizer.Get("logs.inspector.instance"), $"{row.InstanceName} ({manifest.InstanceId ?? _localizer.Get("logs.unknown")})"),
            new(_localizer.Get("logs.inspector.state"), row.StateText),
            new(_localizer.Get("logs.inspector.created"), manifest.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
            new(_localizer.Get("logs.inspector.updated"), manifest.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
            new(_localizer.Get("logs.inspector.duration"), FormatDuration(manifest)),
            new(_localizer.Get("logs.inspector.pid"), manifest.ProcessId?.ToString() ?? _localizer.Get("logs.unknown")),
            new(_localizer.Get("logs.inspector.processStarted"), manifest.ProcessStartedAtUtc is { } started
                ? started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : _localizer.Get("logs.unknown")),
            new(_localizer.Get("logs.inspector.version"), $"{manifest.MinecraftVersion ?? _localizer.Get("logs.unknown")} · {manifest.Loader ?? _localizer.Get("logs.unknownLoader")}"),
            new(_localizer.Get("logs.inspector.java"), manifest.JavaVersion ?? _localizer.Get("logs.unknown")),
            new(_localizer.Get("logs.inspector.javaPath"), string.IsNullOrWhiteSpace(manifest.JavaPath)
                ? _localizer.Get("logs.unknown")
                : Path.GetFileName(manifest.JavaPath)),
            new(_localizer.Get("logs.inspector.exitCode"), manifest.ExitCode?.ToString() ?? _localizer.Get("logs.unknown")),
            new(_localizer.Get("logs.inspector.isolation"), DescribeIsolation(manifest)),
            new(_localizer.Get("logs.inspector.evidenceCount"), manifest.Artifacts.Count.ToString()),
        };

        AddCaptureFields(manifest, fields);

        AddLaunchStageFields(manifest.SessionId, fields);

        if (!string.IsNullOrWhiteSpace(manifest.CaptureFault))
        {
            fields.Add(new(_localizer.Get("logs.inspector.captureFault"), manifest.CaptureFault));
        }

        foreach (var warning in manifest.Warnings)
        {
            fields.Add(new(_localizer.Get("logs.inspector.warning"), DescribeWarning(warning)));
        }

        foreach (var evidence in Evidence)
        {
            fields.Add(new(
                $"{evidence.KindText} · {evidence.OwnershipText}",
                $"{evidence.Summary}{(evidence.HasWarning ? $" · {evidence.WarningText}" : string.Empty)}"));
        }

        var missing = new List<string>();
        foreach (var kind in new[]
                 {
                     SessionArtifactKinds.GameLog,
                     SessionArtifactKinds.ClientCrashReport,
                     SessionArtifactKinds.ServerCrashReport,
                     SessionArtifactKinds.JvmFatalError,
                 })
        {
            if (!manifest.Artifacts.Any(artifact => string.Equals(artifact.Kind, kind, StringComparison.Ordinal)))
            {
                missing.Add(_localizer.Get("logs.evidence.kind." + kind));
            }
        }

        fields.Add(new(
            _localizer.Get("logs.inspector.diagnostics"),
            missing.Count == 0
                ? _localizer.Get("logs.inspector.evidenceComplete")
                : _localizer.Get("logs.inspector.evidenceMissing", string.Join(_localizer.Get("logs.listSeparator"), missing))));

        return new InspectorSelectionContext(
            row.InstanceName,
            $"{row.StateText} · {manifest.MinecraftVersion ?? _localizer.Get("logs.unknownVersion")}",
            fields);
    }

    /// <summary>
    /// F-09 S3/S9：采集方式与字节计量。没有记录过计量时只显示采集方式（若已知），
    /// 不把 0 字节显示成“采集完成”。
    /// </summary>
    private void AddCaptureFields(SessionManifest manifest, List<InspectorSelectionField> fields)
    {
        if (!string.Equals(manifest.CaptureMode, SessionCaptureModes.Unknown, StringComparison.Ordinal))
        {
            fields.Add(new(_localizer.Get("logs.inspector.captureMode"), DescribeCaptureMode(manifest.CaptureMode)));
        }

        if (manifest.CaptureAccountedAtUtc is null)
        {
            return;
        }

        fields.Add(new(
            _localizer.Get("logs.inspector.captureWritten"),
            _localizer.Get("logs.capture.bytes", manifest.CaptureWrittenBytes)));
        fields.Add(new(
            _localizer.Get("logs.inspector.captureDropped"),
            _localizer.Get("logs.capture.bytes", manifest.CaptureDroppedBytes)));
    }

    private string DescribeCaptureMode(string mode) => mode switch
    {
        SessionCaptureModes.LogHost => _localizer.Get("logs.capture.mode.loghost"),
        SessionCaptureModes.Redirect => _localizer.Get("logs.capture.mode.redirect"),
        SessionCaptureModes.Console => _localizer.Get("logs.capture.mode.console"),
        _ => _localizer.Get("logs.capture.mode.unknown"),
    };

    /// <summary>
    /// F-09 S2：启动阶段事实来自会话 journal，不属于面板状态。没有阶段事件的旧会话
    /// 不显示这一组字段（不伪造“0 步”）。
    /// </summary>
    private void AddLaunchStageFields(string sessionId, List<InspectorSelectionField> fields)
    {
        var stages = _sessions.ReadEvents(sessionId)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.StepId))
            .ToArray();
        if (stages.Length == 0)
        {
            return;
        }

        var completed = stages
            .Where(entry => string.Equals(entry.Phase, LaunchStageEvent.PhaseCompleted, StringComparison.Ordinal))
            .ToArray();
        var failed = completed
            .Where(entry => string.Equals(entry.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        fields.Add(new(
            _localizer.Get("logs.inspector.launchStages"),
            _localizer.Get("logs.inspector.launchStagesSummary", completed.Length, failed.Length)));

        var slowest = completed
            .Where(entry => entry.DurationMs is not null)
            .OrderByDescending(entry => entry.DurationMs)
            .FirstOrDefault();
        if (slowest is not null)
        {
            fields.Add(new(
                _localizer.Get("logs.inspector.launchSlowest"),
                $"{slowest.StepId} · {slowest.DurationMs} ms"));
        }

        if (failed.Length > 0)
        {
            fields.Add(new(
                _localizer.Get("logs.inspector.launchFailed"),
                string.Join(_localizer.Get("logs.listSeparator"), failed.Select(entry => entry.StepId))));
        }
    }

    private string DescribeIsolation(SessionManifest manifest)
    {
        var text = _localizer.Get("logs.isolation." + manifest.LogIsolation);
        return string.IsNullOrWhiteSpace(manifest.LogConfigFingerprint)
            ? text
            : $"{text} · {_localizer.Get("logs.inspector.fingerprint")} {manifest.LogConfigFingerprint[..Math.Min(12, manifest.LogConfigFingerprint.Length)]}";
    }

    private string DescribeWarning(string warning)
    {
        if (warning.StartsWith("session.", StringComparison.Ordinal))
        {
            return _localizer.Get("logs.warning." + warning);
        }

        return warning;
    }

    private string FormatDuration(SessionManifest manifest)
    {
        var end = manifest.State is
            GameSessionState.CaptureCompleted or
            GameSessionState.CaptureIncomplete or
            GameSessionState.Interrupted
            ? manifest.UpdatedAtUtc
            : DateTimeOffset.UtcNow;
        var duration = end - manifest.CreatedAtUtc;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private void UpdateInspector(LogSessionRowViewModel row)
    {
        if (!ReferenceEquals(row, SelectedSession))
        {
            return;
        }

        SetInspectorSelection(BuildInspectorContext());
    }

    private void SetInspectorSelection(InspectorSelectionContext? context) =>
        InspectorSelectionChanged?.Invoke(this, context);

    private static string FormatBytes(long bytes) => Ecl.App.Services.StorageSizeFormatter.FormatBytes(bytes);

    private void RequestScrollToEnd() => ScrollToEndRequested?.Invoke(this, EventArgs.Empty);

    private void RequestScrollToLine(LogLineViewModel line) => ScrollToLineRequested?.Invoke(this, line);

    /// <summary>视图订阅：跟随模式或恢复跟随时滚动到最新行。</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>视图订阅：搜索命中导航时滚动到指定行。</summary>
    public event EventHandler<LogLineViewModel>? ScrollToLineRequested;

    public void SetNarrow(bool narrow)
    {
        if (IsNarrow == narrow)
        {
            return;
        }

        IsNarrow = narrow;
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(ShowSessionSplitter));
        if (narrow)
        {
            IsSessionListOpen = false;
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
        _timer.Tick -= OnTimerTick;
        _pressure.LevelChanged -= OnPressureChanged;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        _filterDebounce = null;
        _allLines.Clear();
        Lines.Clear();
        Evidence.Clear();
        Sources.Clear();
        Sessions.Clear();
    }
}
