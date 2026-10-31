// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core.Localization;
using Ecl.Operations;

namespace Ecl.App.ViewModels;

/// <summary>
/// 最小任务中心：只读 <see cref="IOperationStore"/> 的快照，显示当前任务、阶段、
/// 字节进度、速度、ETA、当前文件以及停滞/重试/失败归因。
/// UI 不依赖 DownloadService，也不解析异常文本来判断错误类型。
/// </summary>
public sealed partial class OperationCenterViewModel : ObservableObject
{
    private static readonly TimeSpan KeepFinishedFor = TimeSpan.FromMinutes(2);
    private const int MaxVisibleOperations = 3;

    private readonly IOperationStore _store;
    private readonly ILocalizer _localizer;
    private readonly Dictionary<Guid, OperationItemViewModel> _items = [];
    private int _refreshQueued;

    public ObservableCollection<OperationItemViewModel> Operations { get; } = [];

    [ObservableProperty]
    private bool _hasOperations;

    public OperationCenterViewModel(IOperationStore store, ILocalizer localizer)
    {
        _store = store;
        _localizer = localizer;
        _store.Changed += OnOperationChanged;
        Refresh();
    }

    private void OnOperationChanged(object? sender, OperationSnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Refresh();
            return;
        }

        // 高频进度事件只保留一个待执行的刷新，避免 UI 线程被刷新任务淹没。
        if (Interlocked.CompareExchange(ref _refreshQueued, 1, 0) != 0)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var visible = _store.Snapshots
            .Where(snapshot => !snapshot.IsTerminal || now - snapshot.UpdatedAt <= KeepFinishedFor)
            .OrderBy(snapshot => snapshot.IsTerminal)
            .ThenByDescending(snapshot => snapshot.StartedAt)
            .Take(MaxVisibleOperations)
            .ToArray();

        var visibleIds = visible.Select(snapshot => snapshot.Id).ToHashSet();
        foreach (var stale in _items.Keys.Where(id => !visibleIds.Contains(id)).ToArray())
        {
            Operations.Remove(_items[stale]);
            _items.Remove(stale);
        }

        for (var index = 0; index < visible.Length; index++)
        {
            var snapshot = visible[index];
            if (!_items.TryGetValue(snapshot.Id, out var item))
            {
                item = new OperationItemViewModel(snapshot.Id);
                _items[snapshot.Id] = item;
                Operations.Add(item);
            }

            item.Apply(snapshot, _localizer);

            var currentIndex = Operations.IndexOf(item);
            if (currentIndex > index)
            {
                Operations.Move(currentIndex, index);
            }
        }

        HasOperations = Operations.Count > 0;
    }
}

public sealed partial class OperationItemViewModel : ObservableObject
{
    public OperationItemViewModel(Guid id) => Id = id;

    public Guid Id { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _stageText = string.Empty;

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private string _currentItem = string.Empty;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _etaText = string.Empty;

    [ObservableProperty]
    private string _diagnosticText = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private bool _hasDiagnostic;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private bool _hasCurrentItem;

    public void Apply(OperationSnapshot snapshot, ILocalizer localizer)
    {
        Title = snapshot.Title;
        StateText = localizer.Get(StateKey(snapshot));
        StageText = StageTextFor(snapshot, localizer);
        CurrentItem = snapshot.CurrentItem ?? string.Empty;
        HasCurrentItem = CurrentItem.Length > 0;

        var percent = snapshot.Progress ?? snapshot.ItemProgress;
        IsIndeterminate = percent is null;
        ProgressValue = Math.Clamp((percent ?? 0) * 100, 0, 100);
        ProgressText = DescribeProgress(snapshot, localizer);
        SpeedText = snapshot.BytesPerSecond is > 0
            ? FormatSpeed(snapshot.BytesPerSecond.Value)
            : string.Empty;
        EtaText = snapshot.Eta is { } eta
            ? localizer.Get("operations.eta", FormatDuration(eta))
            : string.Empty;

        IsFailed = snapshot.State == OperationState.Failed;
        DiagnosticText = DescribeDiagnostic(snapshot, localizer);
        HasDiagnostic = DiagnosticText.Length > 0;
    }

    private static string StateKey(OperationSnapshot snapshot) =>
        $"operations.state.{snapshot.State.ToString().ToLowerInvariant()}";

    private static string StageTextFor(OperationSnapshot snapshot, ILocalizer localizer)
    {
        if (snapshot.State is OperationState.Retrying)
        {
            return localizer.Get(StateKey(snapshot));
        }

        if (!string.IsNullOrWhiteSpace(snapshot.StageId))
        {
            var key = $"operations.stage.{snapshot.StageId.ToLowerInvariant()}";
            var text = localizer.Get(key);
            if (!string.Equals(text, key, StringComparison.Ordinal))
            {
                return text;
            }
        }

        return snapshot.StageText ?? string.Empty;
    }

    private static string DescribeProgress(OperationSnapshot snapshot, ILocalizer localizer)
    {
        if (snapshot.Progress is { } bytesProgress)
        {
            return $"{bytesProgress * 100:F0}% · {FormatBytes(snapshot.BytesDownloaded)} / {FormatBytes(snapshot.TotalBytes ?? 0)}";
        }

        if (snapshot.ItemProgress is { } itemsProgress && snapshot.Total is > 0)
        {
            return localizer.Get("operations.items", snapshot.Completed, snapshot.Total.Value)
                   + (itemsProgress > 0 ? $" ({itemsProgress * 100:F0}%)" : string.Empty);
        }

        return snapshot.BytesDownloaded > 0 ? FormatBytes(snapshot.BytesDownloaded) : string.Empty;
    }

    private static string DescribeDiagnostic(OperationSnapshot snapshot, ILocalizer localizer)
    {
        if (snapshot.State == OperationState.Retrying)
        {
            return localizer.Get(
                "operations.retry",
                snapshot.RetryAttempt,
                snapshot.MaxRetryAttempts,
                snapshot.RetryReason ?? string.Empty);
        }

        if (snapshot.IsStalled)
        {
            return localizer.Get("operations.stalled");
        }

        if (snapshot.Error is { } error)
        {
            var reason = localizer.Get($"operations.error.{error.Kind.ToString().ToLowerInvariant()}");
            return localizer.Get("operations.failedReason", reason, error.Message);
        }

        return string.Empty;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:F2} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:F1} MB",
        >= 1024 => $"{bytes / 1024d:F0} KB",
        > 0 => $"{bytes} B",
        _ => "0 B",
    };

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / 1024 / 1024:F1} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:F0} KB/s",
        > 0 => $"{bytesPerSecond:F0} B/s",
        _ => string.Empty,
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}:{duration.Seconds:00}"
            : $"{Math.Max(0, (int)Math.Ceiling(duration.TotalSeconds))}{GetSecondSuffix()}";
    }

    private static string GetSecondSuffix() =>
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? " 秒" : "s";
}
