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
/// 主页中央实例切换栏（home.md §4）：折叠态显示排列中的前四个实例，
/// 展开态显示全部已识别实例的可整理布局，并提供编辑/删除模式。
///
/// 边界（§4.5）：本栏只负责快速选择与排列整理，**不**提供实例创建、复制或导入；
/// 这些仍属于实例页与下载中心。排列只是主页布局偏好，由
/// <see cref="IHomeLayoutStore"/> 保存，不写入实例记录。
///
/// 格子数量（2026-11-21 维护者决定）：一格一实例。不再固定补齐四个位置、也不再保留
/// 删除留下的空位——删除实例后其余实例顺次补位，排列在每次变更后立即保存。
/// </summary>
public sealed partial class HomeInstanceSwitcherViewModel : ObservableObject, IDisposable
{
    /// <summary>折叠态显示的实例数量（§4.1）；实例不足时按实际数量显示。</summary>
    public const int DefaultSlotCount = 4;

    private readonly IHomeInstanceSource _source;
    private readonly IHomeLayoutStore _layoutStore;
    private readonly ILocalizer _localizer;
    /// <summary>排列：按显示顺序保存的实例 ID；不包含空位。</summary>
    private List<string> _slots = [];
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

    /// <summary>折叠态：排列中的前四个实例（不足四个时按实际数量）。</summary>
    public ObservableCollection<HomeInstanceSlotViewModel> Collapsed { get; } = [];

    /// <summary>展开态：每个已识别实例一格，顺序与保存的排列一致。</summary>
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

    /// <summary>编辑模式说明：顺序整理与“立即保存”的语义。</summary>
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

    /// <summary>由外部（删除流程）调用：把该实例从排列中移除，其余实例顺次补位。</summary>
    public void NotifyInstanceRemoved(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || !_loaded)
        {
            return;
        }

        if (_slots.Remove(instanceId))
        {
            Persist();
            Rebuild();
        }
    }

    /// <summary>
    /// 编辑模式的拖动结果：两个位置互换内容（§4.3）。一格一实例，因此不存在“拖到空位”，
    /// 也不允许把位置扩展到实例数量之外。只改变排列，不改变实例本身。
    /// </summary>
    public bool MoveSlot(int sourceIndex, int targetIndex)
    {
        if (!IsExpanded || !IsEditMode)
        {
            return false;
        }

        if (sourceIndex < 0 || targetIndex < 0
            || sourceIndex >= _slots.Count || targetIndex >= _slots.Count
            || sourceIndex == targetIndex)
        {
            return false;
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
        // 读取失败时保留已有排列，避免一次读取失败就把用户的排列清空。
        var pruneUnknown = !_source.HasInstanceLoadError && instances.Count > 0;
        var changed = false;

        // 防御性去重：排列本身不含重复项，但损坏的文件或旧记录可能带来重复。
        if (_slots.Count != _slots.Distinct(StringComparer.Ordinal).Count())
        {
            _slots = [.. _slots.Distinct(StringComparer.Ordinal)];
            changed = true;
        }

        if (pruneUnknown)
        {
            var removed = _slots.RemoveAll(id => !ids.Contains(id));
            changed |= removed > 0;
        }

        var present = new HashSet<string>(_slots, StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            if (present.Add(instance.Model.Id))
            {
                _slots.Add(instance.Model.Id);
                changed = true;
            }
        }

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

        Layout.Clear();
        for (var index = 0; index < _slots.Count; index++)
        {
            byId.TryGetValue(_slots[index], out var instance);
            if (instance is null)
            {
                // 排列里不该出现未知实例；即便出现也不生成空格子（数量与实例数保持一致）。
                continue;
            }

            Layout.Add(new HomeInstanceSlotViewModel(index, instance, _localizer)
            {
                IsSelected = instance.Model.Id == selectedId,
            });
        }

        Collapsed.Clear();
        foreach (var slot in Layout.Take(DefaultSlotCount))
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
