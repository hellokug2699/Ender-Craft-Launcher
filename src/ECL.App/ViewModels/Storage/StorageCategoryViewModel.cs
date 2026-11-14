// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Storage;

namespace Ecl.App.ViewModels.Storage;

/// <summary>
/// 一个固定预设分类的表格。分类标题与顺序来自 <see cref="StorageCategorySpec.All"/>，
/// 无数据时仍保留标题与空状态；勾选、预计释放空间与删除按钮只作用于本分类。
/// </summary>
public sealed partial class StorageCategoryViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly Action<StorageCategoryViewModel> _requestDelete;
    private readonly List<StorageFileRowViewModel> _allRows = [];

    public StorageCategoryViewModel(
        StorageCategory category,
        ILocalizer localizer,
        Action<StorageCategoryViewModel> requestDelete)
    {
        Category = category;
        _localizer = localizer;
        _requestDelete = requestDelete;
        Title = localizer.Get("storage.category." + category.Id);
        Name = Title;
        foreach (var entry in category.Entries)
        {
            var row = new StorageFileRowViewModel(entry, localizer);
            row.PropertyChanged += OnRowPropertyChanged;
            _allRows.Add(row);
        }

        ApplyFilter(null, null);
    }

    public StorageCategory Category { get; }

    public string Id => Category.Id;

    public StorageGroup Group => Category.Group;

    public string Title { get; }

    /// <summary>供 ItemsControl 显示的分组标题。</summary>
    public string Name { get; }

    public ObservableCollection<StorageFileRowViewModel> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyText => _localizer.Get("storage.category.empty");

    public int KnownFileCount => Category.KnownFileCount;

    public string TotalSizeText => Category.KnownBytes > 0
        ? StorageSizeFormatter.FormatBytes(Category.KnownBytes)
        : Category.KnownFileCount == 0
            ? string.Empty
            : _localizer.Get("storage.sizeUnknown");

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private long _selectedBytes;

    [ObservableProperty]
    private int _selectedUnknownCount;

    [ObservableProperty]
    private string _deleteText = string.Empty;

    [ObservableProperty]
    private bool _canDelete;

    /// <summary>重新应用磁盘/实例筛选，保留仍可见行对象上的勾选状态。</summary>
    /// <param name="diskRoots">null 表示不过滤；空集合表示没有选中任何磁盘。</param>
    public void ApplyFilter(IReadOnlyCollection<string>? diskRoots, string? instanceId)
    {
        Rows.Clear();
        foreach (var row in _allRows)
        {
            if (diskRoots is not null)
            {
                var root = Path.GetPathRoot(row.Entry.FullPath);
                if (root is null || !diskRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (instanceId is not null &&
                !string.Equals(row.Entry.InstanceId, instanceId, StringComparison.Ordinal))
            {
                continue;
            }

            Rows.Add(row);
        }

        // 被磁盘/实例筛选隐藏的项目不能继续保持勾选：删除按钮只作用于当前可见表格。
        foreach (var row in _allRows.Where(row => !Rows.Contains(row) && row.IsSelected))
        {
            row.IsSelected = false;
        }

        OnPropertyChanged(nameof(IsEmpty));
        UpdateSelection();
    }

    public IReadOnlyList<StorageFileRowViewModel> SelectedRows => Rows
        .Where(row => row.IsSelected && row.IsSelectable)
        .ToArray();

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StorageFileRowViewModel.IsSelected))
        {
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        var selected = SelectedRows;
        SelectedCount = selected.Count;
        SelectedBytes = selected.Where(row => row.Size is not null).Sum(row => row.Size!.Value);
        SelectedUnknownCount = selected.Count(row => row.Size is null);

        if (SelectedCount == 0)
        {
            DeleteText = _localizer.Get("storage.delete.disabled");
            CanDelete = false;
            return;
        }

        var size = StorageSizeFormatter.FormatBytes(SelectedBytes);
        DeleteText = SelectedUnknownCount > 0
            ? _localizer.Get("storage.delete.buttonUnknown", SelectedCount, size)
            : _localizer.Get("storage.delete.button", SelectedCount, size);
        CanDelete = true;
    }

    [RelayCommand]
    private void RequestDelete()
    {
        if (CanDelete)
        {
            _requestDelete(this);
        }
    }
}
