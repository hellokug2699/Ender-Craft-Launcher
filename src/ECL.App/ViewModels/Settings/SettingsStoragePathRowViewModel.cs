// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 存储与路径页的一行“路径”。只呈现一个真实存在且由 ECL 消费的路径：
/// 名称、完整路径、用途说明、当前可用状态与“打开所在位置”能力。
/// 不可迁移的路径不提供伪编辑控件（settings-storage-paths.md §3/§9）。
/// </summary>
public sealed partial class SettingsStoragePathRowViewModel : ObservableObject
{
    public SettingsStoragePathRowViewModel(
        string key,
        string title,
        string description,
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Key = key;
        Title = title;
        Description = description;
        _path = path;
    }

    /// <summary>稳定的设置项 Key，同时用于设置搜索定位与测试。</summary>
    public string Key { get; }

    public string Title { get; }

    public string Description { get; }

    [ObservableProperty]
    private string _path;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _canOpen;

    [ObservableProperty]
    private bool _isHighlighted;

    /// <summary>UIA/读屏只暴露路径名称，不暴露 record 的默认 ToString。</summary>
    public override string ToString() => Title;
}
