// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.App.Services;
using Ecl.Core.Localization;

namespace Ecl.App.ViewModels.Storage;

/// <summary>
/// 分类下的一个归属分组：Minecraft 实例分类按实例分组，实例行的最右侧复选框
/// 全选该实例在此分类下的内容；ECL 自身分类使用无标题的单一分组。
/// </summary>
public sealed partial class StorageEntryGroupViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly Action _selectionChanged;
    private bool _updating;
    private bool _applying;
    private bool? _state = false;

    public StorageEntryGroupViewModel(
        string? title,
        IEnumerable<StorageFileRowViewModel> rows,
        ILocalizer localizer,
        Action selectionChanged)
    {
        _localizer = localizer;
        _selectionChanged = selectionChanged;
        Title = string.IsNullOrWhiteSpace(title)
            ? localizer.Get("storage.group.all")
            : title;
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }

        UpdateState();
    }

    public string Title { get; }

    public ObservableCollection<StorageFileRowViewModel> Rows { get; } = [];

    /// <summary>分组里没有可清理行（例如全部是会话证据）时，全选框不应可点。</summary>
    public bool CanSelectAll => Rows.Any(row => row.IsSelectable);

    public string SummaryText
    {
        get
        {
            var known = Rows.Where(row => row.Size is not null).Sum(row => row.Size!.Value);
            var size = Rows.Any(row => row.Size is null)
                ? _localizer.Get("storage.sizeUnknown")
                : StorageSizeFormatter.FormatBytes(known);
            return _localizer.Get("storage.group.summary", Rows.Count, size);
        }
    }

    /// <summary>三态：全选为 true、部分选中为 null、未选为 false；点击后全选当前实例。</summary>
    [ObservableProperty]
    private bool? _isSelected;

    partial void OnIsSelectedChanged(bool? value)
    {
        if (_updating || _applying)
        {
            return;
        }

        // 三态复选框的点击顺序是 未选 → 全选 → 部分（null）→ 未选：
        // 已全选时点击取消全选；未选或部分选中时点击全选当前实例。
        ApplySelection(_state is true ? false : true);
    }

    public IReadOnlyList<StorageFileRowViewModel> SelectedRows => Rows
        .Where(row => row.IsSelected && row.IsSelectable)
        .ToArray();

    /// <summary>解除行事件订阅；分类重建分组时必须调用，避免旧分组继续响应选择变化。</summary>
    public void Detach()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }
    }

    private void ApplySelection(bool selected)
    {
        _applying = true;
        try
        {
            foreach (var row in Rows.Where(row => row.IsSelectable))
            {
                row.IsSelected = selected;
            }
        }
        finally
        {
            _applying = false;
        }

        UpdateState();
        _selectionChanged();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StorageFileRowViewModel.IsSelected) || _applying)
        {
            return;
        }

        UpdateState();
        _selectionChanged();
    }

    private void UpdateState()
    {
        var selectable = Rows.Where(row => row.IsSelectable).ToArray();
        bool? state = selectable.Length == 0
            ? false
            : selectable.All(row => row.IsSelected)
                ? true
                : selectable.Any(row => row.IsSelected)
                    ? null
                    : false;

        _updating = true;
        try
        {
            IsSelected = state;
        }
        finally
        {
            _updating = false;
        }

        _state = state;
        OnPropertyChanged(nameof(SummaryText));
    }
}
