// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.ViewModels;
using Ecl.Core.Localization;

namespace Ecl.App.Shell.Output;

/// <summary>
/// 底部 Output / Task 面板。任务视图直接复用 <see cref="OperationCenterViewModel"/>，
/// 不建立第二套任务登记；输出视图读取有界日志缓冲。面板开合不启动、暂停或取消任何任务。
///
/// 输出管线与内存边界：
/// - 日志文件/观察缓冲：<see cref="AppLogBuffer.Capacity"/> 条，溢出丢最旧并计数。
/// - 待刷新队列：<see cref="PendingCapacity"/> 条，溢出丢最旧并计数。
/// - UI 可见集合：<see cref="MaxVisibleEntries"/> 条，超限移除最旧并计入截断数。
/// 丢弃数与可见截断数汇总为 <see cref="DroppedEntries"/>，在面板中显示，避免压力下静默丢日志。
///
/// 线程契约：本 ViewModel 及其集合只属于创建它的 UI Dispatcher。日志事件可能来自任意线程，
/// 这里只把条目放入加锁的有界队列，并把一次刷新封送到 UI 线程；批量刷新，不逐条调度。
/// </summary>
public sealed partial class OutputPanelViewModel : ObservableObject, IDisposable
{
    public const int MaxVisibleEntries = 500;
    public const int PendingCapacity = 1000;

    private readonly AppLogBuffer _buffer;
    private readonly OutputBuffer _pending = new(PendingCapacity);
    private readonly Dispatcher _dispatcher;
    private readonly ILocalizer _localizer;
    private int _flushQueued;
    private int _visibleDropped;
    private bool _disposed;

    public OutputPanelViewModel(
        OperationCenterViewModel operations,
        AppLogBuffer buffer,
        Dispatcher dispatcher,
        ILocalizer localizer)
    {
        Operations = operations;
        _buffer = buffer;
        _dispatcher = dispatcher;
        _localizer = localizer;

        _buffer.EntryWritten += OnEntryWritten;
        Operations.PropertyChanged += OnOperationsPropertyChanged;

        foreach (var entry in _buffer.Snapshot().TakeLast(MaxVisibleEntries))
        {
            OutputEntries.Add(entry);
        }

        HasOutput = OutputEntries.Count > 0;
        _droppedEntries = _buffer.DroppedCount;
    }

    public OperationCenterViewModel Operations { get; }

    public bool HasTasks => Operations.HasActive || Operations.HasRecent;

    public ObservableCollection<AppLogEntry> OutputEntries { get; } = [];

    [ObservableProperty]
    private bool _isOpen = true;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _hasOutput;

    [ObservableProperty]
    private int _droppedEntries;

    public bool IsOutputTabSelected => SelectedTabIndex == 1;

    /// <summary>是否存在因容量限制被丢弃或从可见区移除的输出。</summary>
    public bool HasDropped => DroppedEntries > 0;

    /// <summary>供界面显示的截断提示；没有丢弃时为空。</summary>
    public string TruncationText => HasDropped ? _localizer.Get("output.truncated", DroppedEntries) : string.Empty;

    internal int PendingDroppedCount => _pending.DroppedCount;

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Collapse() => IsOpen = false;

    /// <summary>
    /// 清空输出。执行顺序：清空待刷新队列 → 清空可见集合 → 重置截断计数。
    /// 清空提交后，此前 pending 的条目不可能再出现在界面上。
    /// </summary>
    [RelayCommand]
    private void ClearOutput()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(ClearOutput));
            return;
        }

        _pending.Clear();
        _buffer.ResetDroppedCount();
        _visibleDropped = 0;
        OutputEntries.Clear();
        HasOutput = false;
        UpdateDroppedEntries();
    }

    partial void OnSelectedTabIndexChanged(int value) => OnPropertyChanged(nameof(IsOutputTabSelected));

    partial void OnDroppedEntriesChanged(int value)
    {
        OnPropertyChanged(nameof(HasDropped));
        OnPropertyChanged(nameof(TruncationText));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer.EntryWritten -= OnEntryWritten;
        Operations.PropertyChanged -= OnOperationsPropertyChanged;
        _pending.Clear();
    }

    private void OnOperationsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperationCenterViewModel.HasActive) or nameof(OperationCenterViewModel.HasRecent))
        {
            OnPropertyChanged(nameof(HasTasks));
        }
    }

    private void OnEntryWritten(object? sender, AppLogEntry entry)
    {
        if (_disposed)
        {
            return;
        }

        _pending.Enqueue(entry);
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            FlushPending();
            return;
        }

        // 高频日志只保留一个待执行刷新，避免每行日志都调度一次 UI 更新。
        if (Interlocked.CompareExchange(ref _flushQueued, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() =>
                {
                    Interlocked.Exchange(ref _flushQueued, 0);
                    FlushPending();
                }));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 已关停：不再刷新，队列本身有界，不会泄漏增长。
            Interlocked.Exchange(ref _flushQueued, 0);
        }
    }

    /// <summary>在 UI 线程把待处理条目合并到可见集合。测试可直接在 Dispatcher 上调用。</summary>
    internal void FlushPending()
    {
        if (_disposed || !_dispatcher.CheckAccess())
        {
            return;
        }

        var drained = _pending.Drain();
        if (drained.Length == 0)
        {
            return;
        }

        foreach (var entry in drained)
        {
            OutputEntries.Add(entry);
        }

        if (OutputEntries.Count > MaxVisibleEntries)
        {
            var overflow = OutputEntries.Count - MaxVisibleEntries;
            for (var index = 0; index < overflow; index++)
            {
                OutputEntries.RemoveAt(0);
            }

            _visibleDropped += overflow;
        }

        HasOutput = OutputEntries.Count > 0;
        UpdateDroppedEntries();
    }

    private void UpdateDroppedEntries() =>
        DroppedEntries = _buffer.DroppedCount + _pending.DroppedCount + _visibleDropped;
}
