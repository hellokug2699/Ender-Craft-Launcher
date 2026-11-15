// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Storage;

namespace Ecl.App.ViewModels.Storage;

/// <summary>
/// 一个清理分类。分类标题与顺序来自 <see cref="StorageCategorySpec.All"/>；内容按实例
/// （或 ECL/扩展归属）分组，实例行可全选该实例在本分类下的内容，每个条目行也有
/// 独立复选框。无数据时保留标题与空状态；勾选、预计释放空间与删除按钮只作用于本分类。
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
        Note = localizer.Get("storage.categoryNote." + category.Id);
        if (string.Equals(Note, "storage.categoryNote." + category.Id, StringComparison.Ordinal))
        {
            Note = string.Empty;
        }

        foreach (var entry in category.Entries)
        {
            _allRows.Add(new StorageFileRowViewModel(entry, localizer));
        }

        ApplyFilter(null, null);
    }

    public StorageCategory Category { get; }

    public string Id => Category.Id;

    public StorageGroup Group => Category.Group;

    public string Title { get; }

    /// <summary>供 ItemsControl 显示的分组标题。</summary>
    public string Name { get; }

    /// <summary>分类说明；没有对应文案时为空。</summary>
    public string Note { get; }

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public ObservableCollection<StorageEntryGroupViewModel> Groups { get; } = [];

    public bool IsEmpty => Groups.Count == 0;

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
        var visible = _allRows.Where(row => IsVisible(row, diskRoots, instanceId)).ToArray();

        // 被磁盘/实例筛选隐藏的项目不能继续保持勾选：删除按钮只作用于当前可见内容。
        foreach (var row in _allRows.Where(row => row.IsSelected && !visible.Contains(row)))
        {
            row.IsSelected = false;
        }

        foreach (var group in Groups)
        {
            group.Detach();
        }

        Groups.Clear();
        foreach (var group in visible
                     .GroupBy(row => row.Entry.InstanceId ?? string.Empty, StringComparer.Ordinal)
                     .Select(group => new
                     {
                         InstanceId = group.Key,
                         Title = group.Select(row => row.Entry.InstanceName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                         Rows = group
                             .OrderByDescending(row => row.Size ?? -1)
                             .ThenBy(row => row.FileName, StringComparer.OrdinalIgnoreCase)
                             .ToArray(),
                     })
                     .OrderBy(group => group.InstanceId.Length == 0 ? 0 : 1)
                     .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase))
        {
            Groups.Add(new StorageEntryGroupViewModel(group.Title, group.Rows, _localizer, UpdateSelection));
        }

        OnPropertyChanged(nameof(IsEmpty));
        UpdateSelection();
    }

    public IReadOnlyList<StorageFileRowViewModel> SelectedRows => Groups
        .SelectMany(group => group.SelectedRows)
        .ToArray();

    [RelayCommand]
    private void RequestDelete()
    {
        if (CanDelete)
        {
            _requestDelete(this);
        }
    }

    private static bool IsVisible(
        StorageFileRowViewModel row,
        IReadOnlyCollection<string>? diskRoots,
        string? instanceId)
    {
        if (diskRoots is not null)
        {
            var root = Path.GetPathRoot(row.Entry.FullPath);
            if (root is null || !diskRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return instanceId is null ||
               string.Equals(row.Entry.InstanceId, instanceId, StringComparison.Ordinal);
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
}
