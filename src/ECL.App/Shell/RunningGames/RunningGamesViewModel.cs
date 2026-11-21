// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Shell.RunningGames;

/// <summary>
/// 全局「运行中的游戏」条目：实例名（用户设定名优先）、已运行时间与强制终止入口。
/// 条目本身不持有业务状态；终止动作交给 <see cref="RunningGamesViewModel"/>。
/// </summary>
public sealed partial class RunningGameItemViewModel : ObservableObject
{
    private readonly Action<RunningGameItemViewModel>? _terminate;
    private readonly ILocalizer _localizer;

    public RunningGameItemViewModel(
        RunningGameSnapshot snapshot,
        string instanceName,
        ILocalizer localizer,
        Action<RunningGameItemViewModel>? terminate = null)
    {
        _localizer = localizer;
        _terminate = terminate;
        SessionId = snapshot.SessionId;
        InstanceId = snapshot.InstanceId;
        InstanceName = instanceName;
        ProcessId = snapshot.ProcessId;
        StartedAtUtc = snapshot.StartedAtUtc;
        OriginText = localizer.Get(snapshot.RecoveredAfterRestart
            ? "runningGames.origin.recovered"
            : "runningGames.origin.current");
        UpdateElapsed(DateTimeOffset.UtcNow);
    }

    public string SessionId { get; }

    public string? InstanceId { get; }

    public string InstanceName { get; }

    public int ProcessId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    [ObservableProperty]
    private string _elapsedText = string.Empty;

    /// <summary>“本次启动”或“ECL 重启前已运行”。</summary>
    public string OriginText { get; }

    public string PidText => _localizer.Get("runningGames.pid", ProcessId);

    [ObservableProperty]
    private bool _isTerminating;

    [RelayCommand]
    private void Terminate() => _terminate?.Invoke(this);

    /// <summary>用单调来源刷新已运行时长（测试可传入固定时间）。</summary>
    public void UpdateElapsed(DateTimeOffset nowUtc)
    {
        var elapsed = nowUtc - StartedAtUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        ElapsedText = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}

/// <summary>
/// 全局运行中游戏监视（显式需求）：Shell 侧边栏在版本显示上方展示所有仍在运行的实例
/// （含 ECL 重启后遗留的进程），每秒刷新运行时长，提供强制终止入口。
///
/// 线程契约：集合只在 UI Dispatcher 上修改；扫描在后台线程读会话 manifest，再封送回来。
/// ECL 退出不会终止游戏进程，也不会清除会话记录，因此重启后仍能从会话恢复并显示。
/// </summary>
public sealed partial class RunningGamesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private readonly IGameProcessMonitor _monitor;
    private readonly IInstanceService _instances;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<RunningGamesViewModel> _logger;
    private readonly DispatcherTimer _ticker;

    private CancellationTokenSource? _cancellation;
    private bool _isRefreshing;

    public RunningGamesViewModel(
        IGameProcessMonitor monitor,
        IInstanceService instances,
        IDialogService dialogs,
        ILocalizer localizer,
        Dispatcher dispatcher,
        TimeProvider time,
        ILogger<RunningGamesViewModel> logger)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _ticker = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _ticker.Tick += (_, _) => UpdateElapsed();
    }

    public ObservableCollection<RunningGameItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>启动后台扫描与时长刷新；重复调用无副作用。</summary>
    public void Start()
    {
        if (_cancellation is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _ticker.Start();
        _ = RunAsync(_cancellation.Token);
    }

    /// <summary>扫描一次并刷新列表；测试可直接调用（需在所属 Dispatcher 上等待完成）。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var snapshots = await Task.Run(_monitor.Scan, cancellationToken).ConfigureAwait(false);
            var names = await ResolveInstanceNamesAsync(snapshots, cancellationToken).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(
                () => Apply(snapshots, names),
                DispatcherPriority.Background,
                cancellationToken);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public void Dispose()
    {
        _ticker.Stop();
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "刷新运行中实例失败。");
            }

            try
            {
                await Task.Delay(RefreshInterval, _time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveInstanceNamesAsync(
        IReadOnlyList<RunningGameSnapshot> snapshots,
        CancellationToken token)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (snapshots.Count == 0)
        {
            return names;
        }

        try
        {
            foreach (var instance in await _instances.GetInstancesAsync(token).ConfigureAwait(false))
            {
                names[instance.Id] = instance.Name;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 实例记录读取失败不影响展示：回退到会话里记录的实例名。
            _logger.LogDebug(exception, "解析实例显示名失败，回退到会话快照。");
        }

        return names;
    }

    private void Apply(
        IReadOnlyList<RunningGameSnapshot> snapshots,
        IReadOnlyDictionary<string, string> instanceNames)
    {
        var existing = Items.ToDictionary(item => item.SessionId, StringComparer.Ordinal);
        var now = _time.GetUtcNow();
        var merged = new List<RunningGameItemViewModel>(snapshots.Count);

        foreach (var snapshot in snapshots)
        {
            if (existing.TryGetValue(snapshot.SessionId, out var item))
            {
                item.UpdateElapsed(now);
                merged.Add(item);
                continue;
            }

            var name = snapshot.InstanceId is { } id && instanceNames.TryGetValue(id, out var resolved)
                ? resolved
                : string.IsNullOrWhiteSpace(snapshot.InstanceName)
                    ? _localizer.Get("logs.unknownInstance")
                    : snapshot.InstanceName;
            var created = new RunningGameItemViewModel(snapshot, name, _localizer, Terminate);
            created.UpdateElapsed(now);
            merged.Add(created);
        }

        Items.Clear();
        foreach (var item in merged)
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(HasItems));
    }

    private void UpdateElapsed()
    {
        var now = _time.GetUtcNow();
        foreach (var item in Items)
        {
            item.UpdateElapsed(now);
        }
    }

    private void Terminate(RunningGameItemViewModel item)
    {
        if (item.IsTerminating)
        {
            return;
        }

        item.IsTerminating = true;
        try
        {
            var confirmed = _dialogs.Confirm(
                _localizer.Get("runningGames.terminate.title"),
                _localizer.Get("runningGames.terminate.message", item.InstanceName));
            if (!confirmed)
            {
                return;
            }

            if (!_monitor.TryTerminate(item.SessionId, out var error))
            {
                _dialogs.ShowError(
                    _localizer.Get("runningGames.terminate.failedTitle"),
                    error ?? _localizer.Get("runningGames.terminate.failed"));
                return;
            }

            _ = RefreshAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "强制终止游戏进程失败：{SessionId}", item.SessionId);
        }
        finally
        {
            item.IsTerminating = false;
        }
    }
}
