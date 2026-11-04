// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core.Workspace;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 一个已打开工作区的 UI 投影：宿主条目 + 页面 ViewModel。
/// 切换标签只改变活动工作区，不销毁页面与会话状态；关闭标签才会移除该实例。
/// </summary>
public sealed partial class WorkspaceTabViewModel : ObservableObject
{
    public WorkspaceTabViewModel(WorkspaceEntry entry, object page, string toolDisplayName)
    {
        Entry = entry;
        Page = page;
        ToolDisplayName = toolDisplayName;
        _isDirty = entry.IsDirty;
    }

    public WorkspaceEntry Entry { get; }

    public WorkspaceId Id => Entry.Id;

    public string ToolId => Entry.ToolId;

    public string Title => Entry.Title;

    public string? TargetResource => Entry.TargetResource;

    /// <summary>页面 ViewModel；由 App.xaml 的 DataTemplate 映射为视图。</summary>
    public object Page { get; }

    public string ToolDisplayName { get; }

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>激活标签时与宿主条目对齐（工具可能在此前上报了未保存状态）。</summary>
    public void SyncFromEntry() => IsDirty = Entry.IsDirty;
}
