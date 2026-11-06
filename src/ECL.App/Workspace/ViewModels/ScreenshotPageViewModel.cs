// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Screenshots;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>截图页的三种展示形式（screenshots.md §3）。</summary>
public enum ScreenshotViewMode
{
    List,
    Grid,
    Minimal,
}

/// <summary>
/// 一张截图的只读行状态。缩略图按需读取：只有在视图真正需要显示时才向领域服务
/// 请求字节，避免页面打开时把整个截图目录一次性解码。
/// </summary>
public sealed partial class ScreenshotItemViewModel : ObservableObject
{
    public ScreenshotItemViewModel(ScreenshotEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        FileName = entry.FileName;
        RelativePath = entry.RelativePath;
        SizeText = StorageSizeFormatter.FormatBytes(entry.Size);
        TimeText = entry.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public ScreenshotEntry Entry { get; }

    public string FileName { get; }

    public string RelativePath { get; }

    public string SizeText { get; }

    public string TimeText { get; }

    /// <summary>文件签名是否确认是图片；false 时页面显示文件占位图标，不伪造缩略图。</summary>
    public bool IsRecognizedImage => Entry.IsRecognizedImage;

    internal bool ThumbnailRequested { get; set; }

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _isThumbnailLoading;

    [ObservableProperty]
    private bool _thumbnailFailed;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasThumbnail => Thumbnail is not null;

    partial void OnThumbnailChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
    }
}

/// <summary>
/// 实例工作区「世界与文件 · 截图」页面（B2c，screenshots.md）。
/// <para>
/// 三种展示形式共享同一组条目与选择状态：切换形式不改变截图文件，也不丢失已选条目。
/// 截图读取与删除全部经由 <see cref="IScreenshotService"/>；预览在页面内以覆盖层打开，
/// 不跳转到独立页面。
/// </para>
/// </summary>
public sealed partial class ScreenshotPageViewModel : ObservableObject, IDisposable
{
    private const int ThumbnailPixelWidth = 320;

    private readonly string _instanceId;
    private readonly IScreenshotService _screenshots;
    private readonly IScreenshotImageLoader _images;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private int _previewRequest;

    public ScreenshotPageViewModel(
        string instanceId,
        IScreenshotService screenshots,
        IScreenshotImageLoader images,
        IDialogService dialogs,
        ILocalizer localizer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(screenshots);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(localizer);

        _instanceId = instanceId;
        _screenshots = screenshots;
        _images = images;
        _dialogs = dialogs;
        _localizer = localizer;
    }

    public string InstanceId => _instanceId;

    public ObservableCollection<ScreenshotItemViewModel> Items { get; } = [];

    public bool IsDisposed { get; private set; }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _warningMessage = string.Empty;

    [ObservableProperty]
    private ScreenshotViewMode _viewMode = ScreenshotViewMode.List;

    [ObservableProperty]
    private bool _isNewestFirst = true;

    [ObservableProperty]
    private ScreenshotItemViewModel? _previewItem;

    [ObservableProperty]
    private bool _isPreviewOpen;

    [ObservableProperty]
    private ImageSource? _previewImage;

    [ObservableProperty]
    private bool _isPreviewLoading;

    [ObservableProperty]
    private string _previewError = string.Empty;

    public string Title => _localizer.Get("instance.nav.screenshots");

    public string Subtitle => _localizer.Get("screenshots.subtitle");

    public string ViewListText => _localizer.Get("screenshots.view.list");

    public string ViewGridText => _localizer.Get("screenshots.view.grid");

    public string ViewMinimalText => _localizer.Get("screenshots.view.minimal");

    public string SortNewestText => _localizer.Get("screenshots.sort.newest");

    public string SortOldestText => _localizer.Get("screenshots.sort.oldest");

    public string EmptyTitle => _localizer.Get("screenshots.empty.title");

    public string EmptyDescription => _localizer.Get("screenshots.empty.description");

    public string LoadFailedText => _localizer.Get("screenshots.loadFailed");

    public string DeleteButtonText => SelectedCount > 0
        ? _localizer.Get("screenshots.deleteCount", SelectedCount)
        : _localizer.Get("common.delete");

    public string SelectionSummary => _localizer.Get("screenshots.selected", SelectedCount);

    public string CountText => _localizer.Get("screenshots.count", Items.Count);

    public string TotalSizeText => StorageSizeFormatter.FormatBytes(Items.Sum(item => item.Entry.Size));

    public string PreviewOfText => PreviewItem is null || PreviewIndex < 0
        ? string.Empty
        : _localizer.Get("screenshots.previewOf", PreviewIndex + 1, Items.Count);

    public string PreviewFailedText => _localizer.Get("screenshots.previewFailed");

    public string CloseText => _localizer.Get("screenshots.preview.close");

    public string PreviousText => _localizer.Get("screenshots.preview.previous");

    public string NextText => _localizer.Get("screenshots.preview.next");

    public bool IsListView => ViewMode == ScreenshotViewMode.List;

    public bool IsGridView => ViewMode == ScreenshotViewMode.Grid;

    public bool IsMinimalView => ViewMode == ScreenshotViewMode.Minimal;

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => !IsLoading && Items.Count == 0 && string.IsNullOrEmpty(ErrorMessage);

    public int SelectedCount => Items.Count(item => item.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    public string SelectedSizeText => StorageSizeFormatter.FormatBytes(
        Items.Where(item => item.IsSelected).Sum(item => item.Entry.Size));

    public int PreviewIndex => PreviewItem is null ? -1 : Items.IndexOf(PreviewItem);

    public bool CanGoPrevious => IsPreviewOpen && PreviewIndex > 0;

    public bool CanGoNext => IsPreviewOpen && PreviewIndex >= 0 && PreviewIndex < Items.Count - 1;

    /// <summary>页面首次进入时加载；失败只改变本页文案，不抛给 Shell。</summary>
    public async Task InitializeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsDisposed || IsBusy || IsLoading)
        {
            return;
        }

        StatusMessage = string.Empty;
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void UseListMode() => ViewMode = ScreenshotViewMode.List;

    [RelayCommand]
    private void UseGridMode() => ViewMode = ScreenshotViewMode.Grid;

    [RelayCommand]
    private void UseMinimalMode() => ViewMode = ScreenshotViewMode.Minimal;

    [RelayCommand]
    private void SortNewest()
    {
        IsNewestFirst = true;
        ApplySort();
    }

    [RelayCommand]
    private void SortOldest()
    {
        IsNewestFirst = false;
        ApplySort();
    }

    /// <summary>全选/反选当前集合；极简模式不显示复选框，但命令仍可由键盘触发。</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        if (IsDisposed || IsBusy || Items.Count == 0)
        {
            return;
        }

        var select = SelectedCount < Items.Count;
        foreach (var item in Items)
        {
            item.IsSelected = select;
        }
    }

    /// <summary>删除选中截图：先确认，再交给领域事务；失败时保留列表与选择，不伪造成功。</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelection))]
    private async Task DeleteSelectedAsync()
    {
        if (IsDisposed || IsBusy)
        {
            return;
        }

        var selected = Items.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var total = selected.Sum(item => item.Entry.Size);
        if (!_dialogs.Confirm(
                _localizer.Get("screenshots.deleteTitle"),
                _localizer.Get("screenshots.deleteConfirm", selected.Length, StorageSizeFormatter.FormatBytes(total))))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _screenshots
                .DeleteAsync(_instanceId, selected.Select(item => item.RelativePath).ToArray())
                .ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.ToString() ?? _localizer.Get("screenshots.deleteFailed");
                return;
            }

            StatusMessage = _localizer.Get("screenshots.deleted", selected.Length);
            if (PreviewItem is not null && selected.Contains(PreviewItem))
            {
                ClosePreview();
            }

            await ReloadAsync().ConfigureAwait(true);
            if (!IsDisposed)
            {
                // 重新加载会刷新行数据，但不应该覆盖本次删除的结果文案。
                StatusMessage = _localizer.Get("screenshots.deleted", selected.Length);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                IsBusy = false;
                DeleteSelectedCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool CanDeleteSelection() => !IsDisposed && !IsBusy && HasSelection;

    /// <summary>按需读取一张截图的缩略图；同一行只请求一次，失败后显示占位图。</summary>
    public async Task EnsureThumbnailAsync(ScreenshotItemViewModel? item)
    {
        if (IsDisposed || item is null || item.ThumbnailRequested || !item.IsRecognizedImage)
        {
            return;
        }

        item.ThumbnailRequested = true;
        item.IsThumbnailLoading = true;
        try
        {
            var result = await _screenshots
                .ReadImageAsync(_instanceId, item.RelativePath)
                .ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            var image = result.IsSuccess ? _images.Load(result.Value!, ThumbnailPixelWidth) : null;
            item.Thumbnail = image;
            item.ThumbnailFailed = image is null;
        }
        finally
        {
            if (!IsDisposed)
            {
                item.IsThumbnailLoading = false;
            }
        }
    }

    /// <summary>在当前页面内打开大图预览（不跳转目标、不新建工作区）。</summary>
    [RelayCommand]
    private async Task OpenPreviewAsync(ScreenshotItemViewModel? item)
    {
        if (IsDisposed || item is null || !Items.Contains(item))
        {
            return;
        }

        PreviewItem = item;
        IsPreviewOpen = true;
        NotifyPreviewNavigation();
        await LoadPreviewImageAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PreviousPreviewAsync()
    {
        if (!CanGoPrevious)
        {
            return;
        }

        PreviewItem = Items[PreviewIndex - 1];
        NotifyPreviewNavigation();
        await LoadPreviewImageAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NextPreviewAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        PreviewItem = Items[PreviewIndex + 1];
        NotifyPreviewNavigation();
        await LoadPreviewImageAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ClosePreview()
    {
        _previewRequest++;
        IsPreviewOpen = false;
        IsPreviewLoading = false;
        PreviewImage = null;
        PreviewError = string.Empty;
        PreviewItem = null;
        NotifyPreviewNavigation();
    }

    private async Task LoadPreviewImageAsync()
    {
        var item = PreviewItem;
        if (item is null)
        {
            return;
        }

        var request = ++_previewRequest;
        PreviewImage = null;
        PreviewError = string.Empty;
        IsPreviewLoading = true;
        try
        {
            var result = await _screenshots.ReadImageAsync(_instanceId, item.RelativePath).ConfigureAwait(true);
            if (IsDisposed || request != _previewRequest || !IsPreviewOpen)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                PreviewError = result.Error?.ToString() ?? PreviewFailedText;
                return;
            }

            var image = _images.Load(result.Value!, decodePixelWidth: 0);
            if (image is null)
            {
                PreviewError = PreviewFailedText;
                return;
            }

            PreviewImage = image;
        }
        finally
        {
            if (!IsDisposed && request == _previewRequest)
            {
                IsPreviewLoading = false;
            }
        }
    }

    private async Task ReloadAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        WarningMessage = string.Empty;
        try
        {
            var result = await _screenshots.ListAsync(_instanceId).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            ClearItems();
            if (!result.IsSuccess)
            {
                ErrorMessage = result.Error?.ToString() ?? LoadFailedText;
                return;
            }

            var list = result.Value!;
            foreach (var entry in list.Items)
            {
                AddItem(new ScreenshotItemViewModel(entry));
            }

            WarningMessage = list.Warning ?? string.Empty;
            ApplySort();
        }
        finally
        {
            if (!IsDisposed)
            {
                IsLoading = false;
                NotifyCollectionState();
                DeleteSelectedCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void ApplySort()
    {
        if (Items.Count <= 1)
        {
            return;
        }

        var sorted = (IsNewestFirst
                ? Items.OrderByDescending(item => item.Entry.ModifiedAt)
                : Items.OrderBy(item => item.Entry.ModifiedAt))
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Items.Clear();
        foreach (var item in sorted)
        {
            Items.Add(item);
        }

        NotifyCollectionState();
        NotifyPreviewNavigation();
    }

    private void AddItem(ScreenshotItemViewModel item)
    {
        item.PropertyChanged += OnItemPropertyChanged;
        Items.Add(item);
    }

    private void ClearItems()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        Items.Clear();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ScreenshotItemViewModel.IsSelected))
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedSizeText));
        OnPropertyChanged(nameof(DeleteButtonText));
        OnPropertyChanged(nameof(SelectionSummary));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCollectionState()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(TotalSizeText));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PreviewOfText));
    }

    private void NotifyPreviewNavigation()
    {
        OnPropertyChanged(nameof(PreviewIndex));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PreviewOfText));
    }

    partial void OnViewModeChanged(ScreenshotViewMode value)
    {
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsGridView));
        OnPropertyChanged(nameof(IsMinimalView));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnPreviewItemChanged(ScreenshotItemViewModel? value) =>
        OnPropertyChanged(nameof(PreviewOfText));

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        ClearItems();
        PreviewImage = null;
        PreviewItem = null;
    }
}
