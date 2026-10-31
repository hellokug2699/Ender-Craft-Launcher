// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Core.Localization;
using Ecl.Operations;

namespace Ecl.App.ViewModels;

/// <summary>
/// 任务中心：分“当前任务 / 最近任务”展示 <see cref="IOperationStore"/> 的快照，
/// 支持取消、重试（由任务提供者决定是否可用）与展开诊断详情。
/// UI 不依赖 DownloadService，也不解析异常文本来判断错误类型。
/// </summary>
public sealed partial class OperationCenterViewModel : ObservableObject
{
    private static readonly TimeSpan KeepFinishedFor = TimeSpan.FromMinutes(2);
    private const int MaxActive = 2;
    private const int MaxRecent = 1;

    private readonly IOperationService _operations;
    private readonly IOperationStore _store;
    private readonly ILocalizer _localizer;
    private readonly Dictionary<Guid, OperationItemViewModel> _items = [];
    private int _refreshQueued;

    public ObservableCollection<OperationItemViewModel> ActiveOperations { get; } = [];

    public ObservableCollection<OperationItemViewModel> RecentOperations { get; } = [];

    [ObservableProperty]
    private bool _hasActive;

    [ObservableProperty]
    private bool _hasRecent;

    public OperationCenterViewModel(IOperationService operations, ILocalizer localizer)
    {
        _operations = operations;
        _store = operations.Store;
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
        var snapshots = _store.Snapshots;
        var active = snapshots
            .Where(snapshot => !snapshot.IsTerminal)
            .OrderByDescending(snapshot => snapshot.StartedAt)
            .Take(MaxActive)
            .ToArray();
        var recent = snapshots
            .Where(snapshot => snapshot.IsTerminal && now - snapshot.UpdatedAt <= KeepFinishedFor)
            .OrderByDescending(snapshot => snapshot.UpdatedAt)
            .Take(MaxRecent)
            .ToArray();

        Sync(ActiveOperations, active);
        Sync(RecentOperations, recent);
        HasActive = ActiveOperations.Count > 0;
        HasRecent = RecentOperations.Count > 0;
    }

    private void Sync(ObservableCollection<OperationItemViewModel> target, IReadOnlyList<OperationSnapshot> snapshots)
    {
        var visible = snapshots.Select(snapshot => snapshot.Id).ToHashSet();
        foreach (var stale in target.Where(item => !visible.Contains(item.Id)).ToArray())
        {
            target.Remove(stale);
            _items.Remove(stale.Id);
        }

        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            if (!_items.TryGetValue(snapshot.Id, out var item))
            {
                item = new OperationItemViewModel(
                    snapshot.Id,
                    CancelOperation,
                    RetryOperation,
                    _localizer);
                _items[snapshot.Id] = item;
            }

            item.Apply(snapshot, _localizer);

            var currentIndex = target.IndexOf(item);
            if (currentIndex < 0)
            {
                target.Insert(Math.Min(index, target.Count), item);
            }
            else if (currentIndex > index)
            {
                target.Move(currentIndex, index);
            }
        }
    }

    private bool CancelOperation(Guid id) => _operations.TryCancel(id);

    private Task<bool> RetryOperation(Guid id) => _operations.RetryAsync(id);

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

    public sealed partial class OperationItemViewModel : ObservableObject
    {
        private readonly Func<Guid, bool> _cancel;
        private readonly Func<Guid, Task<bool>> _retry;
        private readonly ILocalizer _localizer;

        internal OperationItemViewModel(
            Guid id,
            Func<Guid, bool> cancel,
            Func<Guid, Task<bool>> retry,
            ILocalizer localizer)
        {
            Id = id;
            _cancel = cancel;
            _retry = retry;
            _localizer = localizer;
        }

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
        private string _detailsText = string.Empty;

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

        [ObservableProperty]
        private bool _canCancel;

        [ObservableProperty]
        private bool _canRetry;

        [ObservableProperty]
        private bool _isExpanded;

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
            CanCancel = !snapshot.IsTerminal && snapshot.CanCancel;
            CanRetry = snapshot.State == OperationState.Failed && snapshot.CanRetry;

            DiagnosticText = DescribeDiagnostic(snapshot, localizer);
            HasDiagnostic = DiagnosticText.Length > 0;
            DetailsText = DescribeDetails(snapshot, localizer);
        }

        [RelayCommand]
        private void Cancel()
        {
            if (_cancel(Id))
            {
                return;
            }

            DiagnosticText = _localizer.Get("operations.cancelUnavailable");
            HasDiagnostic = true;
        }

        [RelayCommand]
        private async Task RetryAsync()
        {
            try
            {
                if (!await _retry(Id).ConfigureAwait(true))
                {
                    DiagnosticText = _localizer.Get("operations.retryUnavailable");
                    HasDiagnostic = true;
                }
            }
            catch (Exception exception)
            {
                DiagnosticText = _localizer.Get("operations.failedReason", _localizer.Get("operations.error.eclinternal"), exception.Message);
                HasDiagnostic = true;
            }
        }

        [RelayCommand]
        private void ToggleDetails() => IsExpanded = !IsExpanded;

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

        private static string DescribeDetails(OperationSnapshot snapshot, ILocalizer localizer)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(snapshot.Source))
            {
                lines.Add(localizer.Get("operations.details.source", snapshot.Source));
            }

            if (!string.IsNullOrWhiteSpace(snapshot.StageId))
            {
                lines.Add(localizer.Get("operations.details.stage", snapshot.StageId));
            }

            if (snapshot.Error is { } error)
            {
                lines.Add(localizer.Get(
                    "operations.details.error",
                    localizer.Get($"operations.error.{error.Kind.ToString().ToLowerInvariant()}"),
                    error.Code));
                if (!string.IsNullOrWhiteSpace(error.Detail))
                {
                    lines.Add(localizer.Get("operations.details.inner", error.Detail));
                }
            }

            lines.Add(localizer.Get(
                "operations.details.updated",
                snapshot.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
            return string.Join(Environment.NewLine, lines);
        }
    }
}
