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
/// - 待刷新队列：<see cref="PendingCapacity"/> 条，溢出丢最旧并计数；队列自己维护代次下限。
/// - UI 可见集合：<see cref="MaxVisibleEntries"/> 条，超限移除最旧并计入截断数。
/// 丢弃数与可见截断数汇总为 <see cref="DroppedEntries"/>，在面板中显示，避免压力下静默丢日志。
///
/// 清空语义（线性化）：
/// 日志条目提交与通知投递之间有窗口，通知可能在清空之后才到达。为了让清空结果可判定，
/// 每条通知携带它在日志锁内捕获的输出代次（<see cref="AppLogNotification.Generation"/>），
/// 面板只接受代次不低于 <see cref="_generationFloor"/> 的通知与待刷新条目。清空时按
/// “清空待刷新队列 → 在日志锁内推进代次 → 在队列锁内推进代次下限 → 发布下限 → 清空可见集合”
/// 执行：之前提交的条目无论何时到达都会被拒绝，之后提交的条目携带新代次并正常显示。
///
/// 释放（线性化）：
/// 日志事件可能来自任意线程，而 Dispose 可能在任何线程调用。事件准入、队列入队、刷新调度
/// 与 Dispose 共用 <see cref="_lifecycleGate"/>，因此 Dispose 返回后不会再有新的有效入队或
/// 新的 Dispatcher 调度；Dispose 只在释放该锁之后才通过 UI Dispatcher 屏障等待已经在执行的
/// 刷新回调，不会持锁等待 UI 线程。刷新回调自身在触碰可见集合前再次检查释放状态。
///
/// 线程契约：本 ViewModel 及其集合只属于创建它的 UI Dispatcher。日志事件可能来自任意线程，
/// 这里只把条目放入加锁的有界队列，并把一次刷新封送到 UI 线程；批量刷新，不逐条调度。
/// </summary>
public sealed partial class OutputPanelViewModel : ObservableObject, IDisposable
{
    public const int MaxVisibleEntries = 500;
    public const int PendingCapacity = 1000;

    private readonly object _lifecycleGate = new();
    private readonly AppLogBuffer _buffer;
    private readonly OutputBuffer _pending = new(PendingCapacity);
    private readonly Dispatcher _dispatcher;
    private readonly ILocalizer _localizer;
    private int _disposed;
    private int _flushQueued;
    private long _generationFloor;
    private int _visibleDropped;
    private DispatcherOperation? _flushOperation;

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

    /// <summary>测试 seam：在生命周期锁内、事件准入检查之后调用，用于构造确定性交错；生产代码不设置。</summary>
    internal Action? AdmissionPaused { get; set; }

    /// <summary>测试 seam：Dispose 获取生命周期锁之前调用；生产代码不设置。</summary>
    internal Action? DisposeStarted { get; set; }

    /// <summary>测试 seam：刷新已写入可见集合之后调用（仅 UI 线程）；生产代码不设置。</summary>
    internal Action? FlushPaused { get; set; }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Collapse() => IsOpen = false;

    /// <summary>
    /// 清空输出。执行顺序：清空待刷新队列 → 在日志锁内推进输出代次 → 在队列锁内推进代次下限 →
    /// 发布新代次下限 → 清空可见集合。清空提交后，此前提交的条目即使通知迟到也会被代次判定拒绝，
    /// 不可能再出现在界面上；清空之后提交的条目携带新代次，不会被这里的队列清空误删。
    /// </summary>
    [RelayCommand]
    private void ClearOutput()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            try
            {
                _dispatcher.BeginInvoke(new Action(ClearOutput));
            }
            catch (InvalidOperationException)
            {
                // Dispatcher 已关停：没有可见集合需要清理。
            }

            return;
        }

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            // 1. 清空队列。此时所有待处理条目仍属于旧代次，可以连同其丢弃计数一起清除。
            _pending.Clear();

            // 2. 推进代次与日志提交共用日志锁：此前提交的条目获得更小的代次。
            var generation = _buffer.BeginOutputGeneration();

            // 3. 在队列锁内拒绝/清理清空期间入队的旧代次条目及旧代次丢弃计数。
            _pending.AdvanceGenerationFloor(generation);

            // 4. 发布代次下限：之后到达的旧通知在事件入口被拒绝。
            Volatile.Write(ref _generationFloor, generation);
            _visibleDropped = 0;
        }

        // 5. 可见集合只在 UI 线程上清理；此后不会再有旧条目进入。
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

    /// <summary>
    /// 释放面板：与日志事件准入共用生命周期锁，幂等地发布释放状态、退订事件并清空队列；
    /// 释放锁之后再取消尚未开始的刷新操作，并用 UI 屏障等待已开始的刷新回调结束。
    /// </summary>
    public void Dispose()
    {
        DisposeStarted?.Invoke();

        DispatcherOperation? queuedFlush;
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
            _buffer.EntryWritten -= OnEntryWritten;
            Operations.PropertyChanged -= OnOperationsPropertyChanged;
            queuedFlush = _flushOperation;
            _flushOperation = null;
            _flushQueued = 0;
            _pending.Clear();
        }

        if (queuedFlush is not null)
        {
            try
            {
                queuedFlush.Abort();
            }
            catch (InvalidOperationException)
            {
                // Dispatcher 已关停：操作不会再执行，回调本身也会检查释放状态。
            }
        }

        if (_dispatcher.CheckAccess() || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            // 屏障：已经在 UI 线程执行的刷新回调先完成，之后才允许 Dispose 返回。
            _dispatcher.Invoke(DispatcherPriority.Background, new Action(static () => { }));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 在检查之后开始关停；此时不再等待 UI 线程。
        }
        catch (TaskCanceledException)
        {
            // 屏障操作被关停取消；刷新回调自身会在触碰集合前检查释放状态。
        }
    }

    private void OnOperationsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(OperationCenterViewModel.HasActive) or nameof(OperationCenterViewModel.HasRecent)))
        {
            return;
        }

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            OnPropertyChanged(nameof(HasTasks));
        }
    }

    private void OnEntryWritten(object? sender, AppLogNotification notification)
    {
        // 快速过滤：下限只会前进，读到旧值最多是在锁内再判定一次。
        if (notification.Generation < Volatile.Read(ref _generationFloor))
        {
            return;
        }

        var flushOnDispatcher = false;
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (notification.Generation < Volatile.Read(ref _generationFloor))
            {
                return;
            }

            AdmissionPaused?.Invoke();

            // 队列在锁内再次拒绝旧代次；ViewModel 的检查不是唯一防线。
            _pending.Enqueue(notification.Entry, notification.Generation);

            flushOnDispatcher = _dispatcher.CheckAccess();
            if (!flushOnDispatcher)
            {
                // 刷新调度与入队在同一个临界区内完成，Dispose 不会漏掉已提交的操作。
                ScheduleFlushLocked();
            }
        }

        if (flushOnDispatcher)
        {
            FlushPending();
        }
    }

    private void ScheduleFlushLocked()
    {
        if (_flushQueued != 0)
        {
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _flushQueued = 1;
        try
        {
            // 高频日志只保留一个待执行刷新，避免每行日志都调度一次 UI 更新。
            _flushOperation = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(FlushQueued));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 已关停：不再刷新，队列本身有界，不会泄漏增长。
            _flushQueued = 0;
            _flushOperation = null;
        }
    }

    private void FlushQueued()
    {
        lock (_lifecycleGate)
        {
            _flushOperation = null;
            _flushQueued = 0;
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
        }

        FlushPending();
    }

    /// <summary>在 UI 线程把待处理条目合并到可见集合。测试可直接在 Dispatcher 上调用。</summary>
    internal void FlushPending()
    {
        AppLogEntry[] drained;
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_dispatcher.CheckAccess())
            {
                return;
            }

            // 代次下限在刷新时再读一次：清空与队列清空之间进入队列的过期条目也在此丢弃。
            drained = _pending.Drain(Volatile.Read(ref _generationFloor));
        }

        if (drained.Length == 0)
        {
            return;
        }

        foreach (var entry in drained)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            OutputEntries.Add(entry);
        }

        FlushPaused?.Invoke();

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
