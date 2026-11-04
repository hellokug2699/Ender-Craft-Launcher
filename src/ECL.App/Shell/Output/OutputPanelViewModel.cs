// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.ViewModels;

namespace Ecl.App.Shell.Output;

/// <summary>
/// 底部 Output / Task 面板。任务视图直接复用 <see cref="OperationCenterViewModel"/>，
/// 不建立第二套任务登记；输出视图读取有界日志缓冲。面板开合不启动、暂停或取消任何任务。
/// </summary>
public sealed partial class OutputPanelViewModel : ObservableObject
{
    public const int MaxVisibleEntries = 500;

    private readonly AppLogBuffer _buffer;
    private readonly ConcurrentQueue<AppLogEntry> _pending = new();
    private int _flushQueued;

    public OutputPanelViewModel(OperationCenterViewModel operations, AppLogBuffer buffer)
    {
        Operations = operations;
        _buffer = buffer;
        _buffer.EntryWritten += OnEntryWritten;
        Operations.PropertyChanged += OnOperationsPropertyChanged;

        foreach (var entry in _buffer.Snapshot().TakeLast(MaxVisibleEntries))
        {
            OutputEntries.Add(entry);
        }

        HasOutput = OutputEntries.Count > 0;
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

    public bool IsOutputTabSelected => SelectedTabIndex == 1;

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Collapse() => IsOpen = false;

    [RelayCommand]
    private void ClearOutput()
    {
        OutputEntries.Clear();
        HasOutput = false;
    }

    partial void OnSelectedTabIndexChanged(int value) => OnPropertyChanged(nameof(IsOutputTabSelected));

    private void OnOperationsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperationCenterViewModel.HasActive) or nameof(OperationCenterViewModel.HasRecent))
        {
            OnPropertyChanged(nameof(HasTasks));
        }
    }

    private void OnEntryWritten(object? sender, AppLogEntry entry)
    {
        _pending.Enqueue(entry);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Flush();
            return;
        }

        // 高频日志只保留一个待执行刷新，避免每行日志都调度一次 UI 更新。
        if (Interlocked.CompareExchange(ref _flushQueued, 1, 0) != 0)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _flushQueued, 0);
            Flush();
        });
    }

    private void Flush()
    {
        var appended = false;
        while (_pending.TryDequeue(out var entry))
        {
            OutputEntries.Add(entry);
            appended = true;
        }

        while (OutputEntries.Count > MaxVisibleEntries)
        {
            OutputEntries.RemoveAt(0);
        }

        if (appended)
        {
            HasOutput = OutputEntries.Count > 0;
        }
    }
}
