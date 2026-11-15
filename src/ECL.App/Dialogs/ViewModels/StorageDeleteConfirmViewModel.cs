// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.ViewModels.Storage;
using Ecl.Core.Localization;

namespace Ecl.App.Dialogs.ViewModels;

/// <summary>
/// 存储清理确认（storage-management.md §9、ADR 0052）。弹窗与主页面共享同一批行对象，
/// 复选框双向同步；确认后逐项执行并保留每项的成功/跳过/失败结果。
/// </summary>
public sealed partial class StorageDeleteConfirmViewModel : ObservableObject, IDialogSessionAware
{
    private readonly ILocalizer _localizer;
    private readonly Func<IReadOnlyList<StorageFileRowViewModel>, Task> _execute;
    private IDialogSession? _dialog;

    public StorageDeleteConfirmViewModel(
        IEnumerable<StorageFileRowViewModel> items,
        ILocalizer localizer,
        Func<IReadOnlyList<StorageFileRowViewModel>, Task> execute)
    {
        _localizer = localizer;
        _execute = execute;
        foreach (var item in items)
        {
            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
        }

        UpdateConfirmState();
    }

    public ObservableCollection<StorageFileRowViewModel> Items { get; } = [];

    public string Title => _localizer.Get("storage.delete.title");

    public string Hint => _localizer.Get("storage.delete.hint");

    public string FileHeader => _localizer.Get("storage.column.file");

    public string OwnerHeader => _localizer.Get("storage.column.owner");

    public string SizeHeader => _localizer.Get("storage.column.size");

    public string SuggestionHeader => _localizer.Get("storage.column.suggestion");

    public string CancelText => _localizer.Get("dialog.cancel");

    public string ConfirmText => IsCompleted
        ? _localizer.Get("storage.delete.close")
        : _localizer.Get("storage.delete.confirm");

    public string RunningText => _localizer.Get("storage.delete.running");

    public bool DidExecute { get; private set; }

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _canConfirm;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public void Attach(IDialogSession session) => _dialog = session;

    partial void OnIsCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConfirmText));
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsCompleted)
        {
            _dialog?.TryClose(DialogOutcome.Primary, true);
            return;
        }

        var selected = Items.Where(item => item.IsSelected && item.IsSelectable).ToArray();
        if (selected.Length == 0 || IsExecuting)
        {
            return;
        }

        IsExecuting = true;
        _dialog?.SetBusy(true);
        try
        {
            await _execute(selected);
            DidExecute = true;
            IsCompleted = true;
            var succeeded = Items.Count(item => item.ProgressKind == "success");
            var failed = Items.Count(item => item.ProgressKind == "failed");
            SummaryText = _localizer.Get("storage.delete.summary", succeeded, failed);
        }
        finally
        {
            IsExecuting = false;
            _dialog?.SetBusy(false);
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (IsExecuting)
        {
            return;
        }

        _dialog?.TryClose(IsCompleted ? DialogOutcome.Primary : DialogOutcome.Cancel, DidExecute);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StorageFileRowViewModel.IsSelected))
        {
            UpdateConfirmState();
        }
    }

    private void UpdateConfirmState() =>
        CanConfirm = Items.Any(item => item.IsSelected && item.IsSelectable);
}
