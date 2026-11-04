// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.Core.Localization;
using Ecl.Core.Workspace;

namespace Ecl.App.Shell.Inspector;

/// <summary>
/// Inspector 只投影当前活动工作区的宿主元数据，不拥有业务数据，也不复制主工作区内容。
/// 没有可检查的上下文时显示明确的空状态。
/// </summary>
public sealed partial class InspectorViewModel(ILocalizer localizer) : ObservableObject
{
    public string HeaderText => localizer.Get("inspector.title");

    public string EmptyTitle => localizer.Get("inspector.empty.title");

    public string EmptyDescription => localizer.Get("inspector.empty.description");

    public string ToolLabel => localizer.Get("inspector.field.tool");

    public string StateLabel => localizer.Get("inspector.field.state");

    public string TargetLabel => localizer.Get("inspector.field.target");

    public string CreatedLabel => localizer.Get("inspector.field.created");

    public string DirtyText => localizer.Get("inspector.dirty");

    public string CloseText => localizer.Get("inspector.action.close");

    /// <summary>由 Shell 注入：关闭当前活动工作区仍走统一 Close Guard 流程。</summary>
    public ICommand? CloseWorkspaceCommand { get; set; }

    [ObservableProperty]
    private bool _hasContext;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _toolName = string.Empty;

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private string _targetText = string.Empty;

    [ObservableProperty]
    private bool _hasTarget;

    [ObservableProperty]
    private string _createdText = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    public void Show(WorkspaceEntry entry, string toolDisplayName)
    {
        Title = entry.Title;
        ToolName = toolDisplayName;
        StateText = localizer.Get($"workspace.state.{entry.State.ToString().ToLowerInvariant()}");
        HasTarget = !string.IsNullOrWhiteSpace(entry.TargetResource);
        TargetText = entry.TargetResource ?? string.Empty;
        CreatedText = entry.Descriptor.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        IsDirty = entry.IsDirty;
        HasContext = true;
    }

    public void Clear()
    {
        HasContext = false;
        Title = string.Empty;
        ToolName = string.Empty;
        StateText = string.Empty;
        TargetText = string.Empty;
        HasTarget = false;
        CreatedText = string.Empty;
        IsDirty = false;
    }
}
