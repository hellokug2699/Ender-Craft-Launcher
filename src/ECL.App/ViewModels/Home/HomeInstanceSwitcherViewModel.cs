// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;

namespace Ecl.App.ViewModels.Home;

/// <summary>
/// 主页中央实例切换栏（home.md §4）：折叠态显示主页默认的前四个实例，
/// 展开态显示全部已识别实例形成的统一可整理布局，并提供编辑/删除模式。
///
/// 边界（§4.5）：本栏只负责快速选择与排列整理，**不**提供实例创建、复制或导入；
/// 这些仍属于实例页与下载中心。排列只是主页布局偏好，由
/// <see cref="IHomeLayoutStore"/> 保存，不写入实例记录。
///
/// 空位语义（§4.4）：删除实例后该位置保持为空位，其它实例不会自动补位；
/// 新识别到的实例追加到布局末尾，用户可以再拖入默认位置。
/// </summary>
public sealed partial class HomeInstanceSwitcherViewModel : ObservableObject, IDisposable
{
    /// <summary>主页默认实例位置数量（§4.1）。</summary>
    public const int DefaultSlotCount = 4;

    private readonly IHomeInstanceSource _source;
    private readonly IHomeLayoutStore _layoutStore;
    private readonly ILocalizer _localizer;
    private List<string?> _slots = [];
    private bool _loaded;
    private bool _disposed;

    public HomeInstanceSwitcherViewModel(
        IHomeInstanceSource source,
        IHomeLayoutStore layoutStore,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(layoutStore);
        ArgumentNullException.ThrowIfNull(localizer);

        _source = source;
        _layoutStore = layoutStore;
        _localizer = localizer;
        EmptyHint = localizer.Get("home.switcher.empty");

        _source.InstancesChanged += OnInstancesChanged;
        _source.PropertyChanged += OnLauncherPropertyChanged;
    }

    /// <summary>折叠态：主页默认位置中的前四个实例（空位不显示）。</summary>
    public ObservableCollection<HomeInstanceSlotViewModel> Collapsed { get; } = [];

    /// <summary>展开态：全部位置（含空位）。</summary>
    public ObservableCollection<HomeInstanceSlotViewModel> Layout { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private bool _isDeleteMode;

    /// <summary>启动进行中时禁止切换实例：主页只呈现一个实例的启动状态（home.md §3.4）。</summary>
    [ObservableProperty]
    private bool _canSwitch = true;

    [ObservableProperty]
    private bool _hasInstances;

    public string EmptyHint { get; }

    public string ExpandText => _localizer.Get(IsExpanded ? "home.switcher.collapse" : "home.switcher.expand");

    public string EditModeText => _localizer.Get(IsEditMode ? "home.switcher.editDone" : "home.switcher.edit");

    public string DeleteModeText => _localizer.Get(IsDeleteMode ? "home.switcher.deleteDone" : "home.switcher.delete");

    /// <summary>维护「前四个默认位置」的可见性提示。</summary>
    public string EditHint => _localizer.Get("home.switcher.editHint");

    public string DeleteHint => _localizer.Get("home.switcher.deleteHint");

    /// <summary>加载保存的排列并与当前实例列表对齐；可重复调用。</summary>
    public void Load()
    {
        if (!_loaded)
        {
            _slots = [.. _layoutStore.Load()];
            _loaded = true;
        }

        Reconcile();
    }

    /// <summary>由外部（删除流程）调用：把该实例的位置标记为空位后重建。</summary>
    public void NotifyInstanceRemoved(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        var changed = false;
        for (var index = 0; index < _slots.Count; index++)
        {
            if (string.Equals(_slots[index], instanceId, StringComparison.Ordinal))
            {
                _slots[index] = null;
                changed = true;
            }
        }

        if (changed)
        {
            Persist();
            Rebuild();
        }
    }

    /// <summary>
    /// 编辑模式的拖动结果：两个位置互换内容（§4.3）。拖到空位即移动过去，原位置变为空位；
    /// 拖到已有实例的位置即互换。只改变排列，不改变实例本身。
    /// </summary>
    public bool MoveSlot(int sourceIndex, int targetIndex)
    {
        if (!IsExpanded || !IsEditMode)
        {
            return false;
        }

        var displayCount = Math.Max(DefaultSlotCount, _slots.Count);
        if (sourceIndex < 0 || targetIndex < 0
            || sourceIndex >= displayCount || targetIndex >= displayCount
            || sourceIndex == targetIndex)
        {
            return false;
        }

        // 拖到“展示用的空位”时先把位置落实到排列记录，再互换内容。
        while (_slots.Count <= Math.Max(sourceIndex, targetIndex))
        {
            _slots.Add(null);
        }

        (_slots[sourceIndex], _slots[targetIndex]) = (_slots[targetIndex], _slots[sourceIndex]);
        Persist();
        Rebuild();
        return true;
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        if (!IsExpanded)
        {
            IsEditMode = false;
            IsDeleteMode = false;
        }

        OnPropertyChanged(nameof(ExpandText));
    }

    [RelayCommand]
    private void ToggleEditMode()
    {
        if (!IsExpanded)
        {
            IsExpanded = true;
            OnPropertyChanged(nameof(ExpandText));
        }

        IsEditMode = !IsEditMode;
        if (IsEditMode)
        {
            // 编辑与删除是两种独立模式（§4.4）：同一时间只允许一种。
            IsDeleteMode = false;
        }

        OnPropertyChanged(nameof(EditModeText));
        OnPropertyChanged(nameof(DeleteModeText));
    }

    [RelayCommand]
    private void ToggleDeleteMode()
    {
        if (!IsExpanded)
        {
            IsExpanded = true;
            OnPropertyChanged(nameof(ExpandText));
        }

        IsDeleteMode = !IsDeleteMode;
        if (IsDeleteMode)
        {
            IsEditMode = false;
        }

        OnPropertyChanged(nameof(DeleteModeText));
        OnPropertyChanged(nameof(EditModeText));
    }

    /// <summary>选择某个位置上的实例作为主页当前实例。</summary>
    [RelayCommand]
    private void SelectInstance(HomeInstanceSlotViewModel? slot)
    {
        if (!CanSwitch || slot?.Instance is not { } instance)
        {
            return;
        }

        _source.SelectedInstance = instance;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.InstancesChanged -= OnInstancesChanged;
        _source.PropertyChanged -= OnLauncherPropertyChanged;
    }

    private void OnInstancesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Reconcile();

    private void OnLauncherPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IHomeInstanceSource.SelectedInstance))
        {
            UpdateSelection();
        }
        else if (e.PropertyName == nameof(IHomeInstanceSource.HasInstanceLoadError))
        {
            Reconcile();
        }
    }

    private void Reconcile()
    {
        if (!_loaded || _disposed)
        {
            return;
        }

        var instances = _source.Instances;
        var ids = instances.Select(item => item.Model.Id).ToHashSet(StringComparer.Ordinal);
        // 读取失败时保留已有位置，避免一次读取失败就把用户的排列清空。
        var pruneUnknown = !_source.HasInstanceLoadError && instances.Count > 0;
        var changed = false;

        for (var index = 0; index < _slots.Count; index++)
        {
            if (_slots[index] is { } id && !ids.Contains(id) && pruneUnknown)
            {
                _slots[index] = null;
                changed = true;
            }
        }

        var present = _slots.Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            if (present.Add(instance.Model.Id))
            {
                _slots.Add(instance.Model.Id);
                changed = true;
            }
        }

        // 排列只保存真实位置（含删除留下的空位）；四个默认位置在展示时补齐，
        // 不写入记录，避免把“展示用的空位”变成后续实例追加的新位置。
        if (changed)
        {
            Persist();
        }

        Rebuild();
    }

    private void Rebuild()
    {
        var byId = _source.Instances.ToDictionary(
            item => item.Model.Id,
            item => item,
            StringComparer.Ordinal);
        var selectedId = _source.SelectedInstance?.Model.Id;
        var displayCount = Math.Max(DefaultSlotCount, _slots.Count);

        Layout.Clear();
        for (var index = 0; index < displayCount; index++)
        {
            var id = index < _slots.Count ? _slots[index] : null;
            byId.TryGetValue(id ?? string.Empty, out var instance);
            Layout.Add(new HomeInstanceSlotViewModel(index, instance, _localizer)
            {
                IsSelected = instance is not null && instance.Model.Id == selectedId,
            });
        }

        Collapsed.Clear();
        foreach (var slot in Layout.Where(slot => !slot.IsEmpty).Take(DefaultSlotCount))
        {
            Collapsed.Add(slot);
        }

        HasInstances = Collapsed.Count > 0;
    }

    private void UpdateSelection()
    {
        var selectedId = _source.SelectedInstance?.Model.Id;
        foreach (var slot in Layout)
        {
            var selected = slot.Instance is not null && slot.Instance.Model.Id == selectedId;
            if (slot.IsSelected != selected)
            {
                slot.IsSelected = selected;
            }
        }
    }

    private void Persist() => _layoutStore.Save(_slots);
}
