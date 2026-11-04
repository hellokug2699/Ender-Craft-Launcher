// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ecl.App.Dialogs;

/// <summary>
/// 弹窗层：持有当前打开的弹窗栈并暴露宿主需要的状态。
/// 只负责呈现层；模态输入锁定通过与窗口级遮罩联动，不承载业务判断。
/// </summary>
public sealed partial class DialogLayerViewModel : ObservableObject
{
    private int _attachedHosts;

    public ObservableCollection<DialogEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    private DialogEntryViewModel? _top;

    public bool HasDialogs => Top is not null;

    /// <summary>栈中存在模态弹窗时，背景必须暗化并禁止普通输入。</summary>
    public bool HasModal => Entries.Any(entry => entry.IsModal);

    /// <summary>Shell 背景是否可交互；模态弹窗打开时为 false。</summary>
    public bool IsShellInteractive => !HasModal;

    public bool IsHostAttached => _attachedHosts > 0;

    public void AttachHost()
    {
        _attachedHosts++;
        OnPropertyChanged(nameof(IsHostAttached));
    }

    public void DetachHost()
    {
        _attachedHosts = Math.Max(0, _attachedHosts - 1);
        OnPropertyChanged(nameof(IsHostAttached));
    }

    internal void Add(DialogEntryViewModel entry)
    {
        Entries.Add(entry);
        RaiseStateChanged();
    }

    internal void Remove(DialogEntryViewModel entry)
    {
        if (Entries.Remove(entry))
        {
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged()
    {
        Top = Entries.Count == 0 ? null : Entries[^1];
        OnPropertyChanged(nameof(HasDialogs));
        OnPropertyChanged(nameof(HasModal));
        OnPropertyChanged(nameof(IsShellInteractive));
    }
}
