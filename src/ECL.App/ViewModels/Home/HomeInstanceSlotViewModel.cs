// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core.Localization;

namespace Ecl.App.ViewModels.Home;

/// <summary>
/// 主页中央实例切换栏中的一个位置（home.md §4）。一格一实例：布局格子数量等于实例数量，
/// 不保留删除留下的空位（2026-11-21 维护者决定），因此 <see cref="IsEmpty"/> 只是
/// XAML 模板的兼容分支，正常状态下始终为 false。
/// </summary>
public sealed partial class HomeInstanceSlotViewModel : ObservableObject
{
    public HomeInstanceSlotViewModel(int index, InstanceItemViewModel? instance, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        Index = index;
        Instance = instance;
        EmptyText = localizer.Get("home.switcher.emptySlot");
    }

    /// <summary>在排列中的真实位置（从 0 开始）。前四个位置同时是折叠态显示的位置。</summary>
    public int Index { get; }

    public InstanceItemViewModel? Instance { get; }

    public bool IsEmpty => Instance is null;

    public bool IsDefaultSlot => Index < HomeInstanceSwitcherViewModel.DefaultSlotCount;

    public string Name => Instance?.Name ?? string.Empty;

    /// <summary>Mod Loader 信息（名称 + 版本，2号层级）。</summary>
    public string LoaderText => Instance?.LoaderShortText ?? string.Empty;

    /// <summary>右上方额外显示的 Mod Loader 名称（2号层级）。</summary>
    public string LoaderName => Instance?.LoaderName ?? string.Empty;

    /// <summary>加载器类型色条使用的键（Forge 红 / NeoForge 橙 / Fabric 蓝 / Cleanroom 绿 / 未知灰）。</summary>
    public string LoaderKind => Instance?.LoaderKind ?? "unknown";

    public string EmptyText { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>删除模式下位置是否显示删除按钮；空位不显示。</summary>
    public bool CanDelete => !IsEmpty;
}
