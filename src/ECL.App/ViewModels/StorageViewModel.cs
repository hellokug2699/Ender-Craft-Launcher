// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.App.Services;
using Ecl.App.ViewModels.Storage;
using Ecl.Core.Localization;
using Ecl.Minecraft.Storage;
using Ecl.Operations;
using Microsoft.Extensions.Logging;

namespace Ecl.App.ViewModels;

/// <summary>
/// 存储管理（storage-management.md / ADR 0022）。页面只呈现领域清单并提交用户选择；
/// 扫描、分类、白名单、所有权、路径与活动状态校验全部由
/// <see cref="IStorageInventoryService"/> 负责。
/// </summary>
public sealed partial class StorageViewModel : ObservableObject
{
    private readonly IStorageInventoryService _inventory;
    private readonly ILocalizer _localizer;
    private readonly IOperationService _operations;
    private readonly IDialogEngine _dialogs;
    private readonly IDialogService _dialogService;
    private readonly ILogger<StorageViewModel> _logger;
    private readonly List<StorageCategoryViewModel> _categories = [];

    private string? _instanceFocusId;
    private StorageInventory? _lastInventory;
    private bool _suppressDiskRefresh;

    public StorageViewModel(
        IStorageInventoryService inventory,
        ILocalizer localizer,
        IOperationService operations,
        IDialogEngine dialogs,
        IDialogService dialogService,
        ILogger<StorageViewModel> logger)
    {
        _inventory = inventory;
        _localizer = localizer;
        _operations = operations;
        _dialogs = dialogs;
        _dialogService = dialogService;
        _logger = logger;
    }

    public ObservableCollection<StorageDiskViewModel> Disks { get; } = [];

    public ObservableCollection<StorageGroupViewModel> Groups { get; } = [];

    public IReadOnlyList<StorageCategoryViewModel> Categories => _categories;

    public string Title => _localizer.Get("storage.title");

    public string Subtitle => _localizer.Get("storage.subtitle");

    public string ScanText => _localizer.Get("storage.scan");

    public string RescanText => _localizer.Get("storage.rescan");

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _hasScanned;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private bool _hasWarnings;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _warningText = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _scanProgressText = string.Empty;

    [ObservableProperty]
    private double _scanProgress;

    [ObservableProperty]
    private string _scanDateText = string.Empty;

    [ObservableProperty]
    private string _instanceFocusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasInstanceFocus;

    [ObservableProperty]
    private bool _isDeleting;

    public bool CanScan => !IsScanning && !IsDeleting;

    public bool ShowEmptyState => HasScanned && (!HasData || _categories.All(category => category.IsEmpty));

    public async Task ActivateAsync()
    {
        if (HasScanned || IsScanning)
        {
            return;
        }

        await ScanAsync(force: false);
    }

    public void FocusInstance(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        _instanceFocusId = instanceId;
        var name = _lastInventory?.Categories
            .SelectMany(category => category.Entries)
            .FirstOrDefault(entry => string.Equals(entry.InstanceId, instanceId, StringComparison.Ordinal))
            ?.InstanceName;
        SetInstanceFocus(_localizer.Get("storage.instanceFocus", name ?? instanceId));
        ApplyFilters();
    }

    [RelayCommand]
    public void ClearInstanceFocus()
    {
        if (_instanceFocusId is null)
        {
            return;
        }

        _instanceFocusId = null;
        SetInstanceFocus(string.Empty);
        ApplyFilters();
    }

    [RelayCommand]
    private async Task RescanAsync() => await ScanAsync(force: true);

    public async Task ScanAsync(bool force)
    {
        if (IsScanning || IsDeleting || (HasScanned && !force))
        {
            return;
        }

        IsScanning = true;
        HasScanned = false;
        HasData = false;
        HasWarnings = false;
        WarningText = string.Empty;
        StatusMessage = _localizer.Get("storage.scanning");
        ScanProgressText = string.Empty;
        ScanProgress = 0;
        Disks.Clear();
        Groups.Clear();
        _categories.Clear();
        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(CanScan));

        var progress = new Progress<StorageScanProgress>(value =>
        {
            ScanProgressText = value.Detail ?? value.Stage;
            if (value.Fraction is { } fraction)
            {
                ScanProgress = Math.Clamp(fraction, 0, 1);
            }
        });

        try
        {
            var inventory = await _inventory.ScanAsync(progress: progress).ConfigureAwait(true);
            _lastInventory = inventory;
            Build(inventory);
            HasScanned = true;
            HasData = inventory.Categories.Any(category => category.KnownFileCount > 0 || category.UnknownSizeCount > 0);
            HasWarnings = inventory.Warnings.Count > 0;
            WarningText = string.Join(Environment.NewLine, inventory.Warnings);
            if (_instanceFocusId is { } focusId)
            {
                var name = inventory.Categories
                    .SelectMany(category => category.Entries)
                    .FirstOrDefault(entry => string.Equals(entry.InstanceId, focusId, StringComparison.Ordinal))
                    ?.InstanceName;
                SetInstanceFocus(_localizer.Get("storage.instanceFocus", name ?? focusId));
            }

            SummaryText = _localizer.Get(
                "storage.summary",
                StorageSizeFormatter.FormatBytes(inventory.KnownEclBytes),
                inventory.InstanceCount,
                inventory.UnavailableCount);
            ScanDateText = _localizer.Get("storage.scannedAt", inventory.ScannedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            StatusMessage = inventory.IsComplete
                ? _localizer.Get("storage.complete")
                : _localizer.Get("storage.incomplete");
            ApplyFilters();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "存储扫描失败。");
            StatusMessage = _localizer.Get("storage.scanFailed", exception.Message);
        }
        finally
        {
            IsScanning = false;
            ScanProgressText = string.Empty;
            OnPropertyChanged(nameof(CanScan));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private void Build(StorageInventory inventory)
    {
        _suppressDiskRefresh = true;
        try
        {
            foreach (var disk in inventory.Disks)
            {
                Disks.Add(new StorageDiskViewModel(disk, _localizer, OnDiskSelectionChanged));
            }
        }
        finally
        {
            _suppressDiskRefresh = false;
        }

        foreach (var category in inventory.Categories)
        {
            _categories.Add(new StorageCategoryViewModel(category, _localizer, RequestDelete));
        }

        foreach (var group in new[]
                 {
                     StorageGroup.MinecraftInstance,
                     StorageGroup.EclOwn,
                     StorageGroup.Additions,
                 })
        {
            Groups.Add(new StorageGroupViewModel(
                _localizer.Get("storage.group." + group),
                _categories.Where(category => category.Group == group)));
        }

        OnPropertyChanged(nameof(Categories));
    }

    private void OnDiskSelectionChanged()
    {
        if (_suppressDiskRefresh)
        {
            return;
        }

        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var selectedDisks = Disks.Where(disk => disk.IsSelected).Select(disk => disk.Root).ToArray();
        IReadOnlyCollection<string>? diskFilter = selectedDisks.Length == Disks.Count
            ? null
            : selectedDisks;
        foreach (var category in _categories)
        {
            category.ApplyFilter(diskFilter, _instanceFocusId);
        }

        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void RequestDelete(StorageCategoryViewModel category) =>
        _ = OpenDeleteDialogAsync(category);

    private async Task OpenDeleteDialogAsync(StorageCategoryViewModel category)
    {
        var rows = category.SelectedRows;
        if (rows.Count == 0)
        {
            return;
        }

        var deleted = false;
        if (_dialogs.IsAvailable)
        {
            var dialog = new StorageDeleteConfirmViewModel(rows, _localizer, ExecuteDeleteAsync);
            try
            {
                await _dialogs.ShowAsync(new DialogRequest
                {
                    Title = _localizer.Get("storage.delete.title"),
                    Content = dialog,
                    InitialFocus = DialogInitialFocus.Cancel,
                    Severity = DialogSeverity.Warning,
                    MinWidth = 720,
                    MaxWidth = 980,
                    MinHeight = 380,
                    MaxHeight = 720,
                });
                deleted = dialog.DidExecute;
            }
            catch (DialogHostUnavailableException)
            {
                // 宿主暂不可用时退回到兼容确认；仍执行同一个领域清理，不伪造逐项进度。
                deleted = _dialogService.Confirm(
                    _localizer.Get("storage.delete.title"),
                    _localizer.Get("storage.delete.fallback", rows.Count, DescribeSelection(rows)));
                if (deleted)
                {
                    await ExecuteDeleteAsync(rows);
                }
            }
        }
        else
        {
            deleted = _dialogService.Confirm(
                _localizer.Get("storage.delete.title"),
                _localizer.Get("storage.delete.fallback", rows.Count, DescribeSelection(rows)));
            if (deleted)
            {
                await ExecuteDeleteAsync(rows);
            }
        }

        if (deleted)
        {
            // 删除完成后必须重新扫描：不得根据删除任务报告推算新的存储数字。
            await ScanAsync(force: true);
        }
    }

    private string DescribeSelection(IReadOnlyList<StorageFileRowViewModel> rows)
    {
        var known = rows.Where(row => row.Size is not null).Sum(row => row.Size!.Value);
        return _localizer.Get(
            "storage.delete.fallbackDetail",
            StorageSizeFormatter.FormatBytes(known));
    }

    private async Task ExecuteDeleteAsync(IReadOnlyList<StorageFileRowViewModel> rows)
    {
        if (rows.Count == 0 || IsDeleting)
        {
            return;
        }

        IsDeleting = true;
        using var cancellation = new CancellationTokenSource();
        using var operation = _operations.Begin(new OperationStart
        {
            Kind = OperationKind.Generic,
            Title = _localizer.Get("storage.delete.taskTitle"),
            StageId = "storage-cleanup",
            Source = "ECL",
            Total = rows.Count,
            CanCancel = true,
            CanRetry = false,
        });
        _operations.AttachControl(operation.Id, new OperationControl(cancellation));
        var rowById = rows.ToDictionary(row => row.Entry.Id, StringComparer.Ordinal);
        var completed = 0;

        var progress = new Progress<StorageDeleteProgress>(value =>
        {
            if (!rowById.TryGetValue(value.EntryId, out var row))
            {
                return;
            }

            switch (value.State)
            {
                case StorageDeleteItemState.Running:
                    row.BeginProgress();
                    break;
                case StorageDeleteItemState.Succeeded:
                    row.CompleteProgress(true, null);
                    completed++;
                    break;
                case StorageDeleteItemState.Failed:
                case StorageDeleteItemState.Skipped:
                    row.CompleteProgress(false, DescribeReason(value.Reason));
                    completed++;
                    break;
            }

            operation.Report(new OperationProgressUpdate
            {
                Completed = completed,
                Total = rows.Count,
                CurrentItem = row.FileName,
                StageId = "storage-cleanup",
            });
        });

        try
        {
            var result = await _inventory.DeleteAsync(
                rows.Select(row => row.Entry).ToArray(),
                progress,
                cancellation.Token).ConfigureAwait(true);

            if (result.Failed == 0 && result.Skipped == 0)
            {
                operation.Complete(new OperationProgressUpdate
                {
                    Completed = result.Succeeded,
                    Total = rows.Count,
                    StageId = "storage-cleanup",
                    StageText = _localizer.Get("storage.delete.completed"),
                });
                StatusMessage = _localizer.Get("storage.delete.success", result.Succeeded, StorageSizeFormatter.FormatBytes(result.BytesFreed));
            }
            else
            {
                operation.Complete(new OperationProgressUpdate
                {
                    Completed = result.Succeeded,
                    Total = rows.Count,
                    StageId = "storage-cleanup",
                    StageText = _localizer.Get("storage.delete.completed"),
                });
                StatusMessage = _localizer.Get(
                    "storage.delete.partial",
                    result.Succeeded,
                    result.Skipped,
                    result.Failed);
            }

        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            StatusMessage = _localizer.Get("storage.delete.cancelled");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            operation.Fail(new OperationError(
                exception is IOException or UnauthorizedAccessException
                    ? OperationErrorKind.LocalIO
                    : OperationErrorKind.EclInternal,
                "storage.cleanup.failed",
                exception.Message));
            StatusMessage = _localizer.Get("storage.delete.failed", exception.Message);
        }
        finally
        {
            IsDeleting = false;
        }
    }

    private string DescribeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return _localizer.Get("storage.delete.reason.unknown");
        }

        var key = "storage.delete.reason." + reason;
        var localized = _localizer.Get(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? reason
            : localized;
    }

    private void SetInstanceFocus(string message)
    {
        InstanceFocusMessage = message;
        HasInstanceFocus = !string.IsNullOrWhiteSpace(message);
    }

    private static string FormatBytes(long bytes) => StorageSizeFormatter.FormatBytes(bytes);
}
