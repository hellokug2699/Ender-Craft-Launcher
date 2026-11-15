// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;

namespace Ecl.App.Dialogs.ViewModels;

/// <summary>
/// 整合包模组数量选项的预览列表（`download-modpacks.md` §8.1）。
/// 只展示清单声明的文件，不能修改清单；具体布局/字段/操作由本页最简实现承担，
/// 不添加批量操作、搜索或安装动作。
/// </summary>
public sealed partial class ModpackModListDialogViewModel : ObservableObject, IDialogSessionAware
{
    private IDialogSession? _session;

    public ModpackModListDialogViewModel(
        string modpackName,
        string countText,
        string hint,
        IReadOnlyList<string> entries,
        ILocalizer localizer)
    {
        ModpackName = modpackName;
        CountText = countText;
        Hint = hint;
        Entries = new ObservableCollection<string>(entries);
        EmptyText = localizer.Get("downloads.deploy.modpack.previewEmpty");
        CloseText = localizer.Get("dialog.close");
    }

    public string ModpackName { get; }

    public string CountText { get; }

    public string Hint { get; }

    public string EmptyText { get; }

    public string CloseText { get; }

    public ObservableCollection<string> Entries { get; }

    public bool HasEntries => Entries.Count > 0;

    void IDialogSessionAware.Attach(IDialogSession session) => _session = session;

    [RelayCommand]
    private void Close() => _session?.TryClose(DialogOutcome.Cancel);
}
