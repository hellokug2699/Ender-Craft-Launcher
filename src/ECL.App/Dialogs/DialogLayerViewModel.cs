// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Dialogs;

/// <summary>
/// 弹窗层：持有当前打开的弹窗栈并暴露宿主需要的状态。
///
/// 线程契约：整个实例（包括 <see cref="Entries"/>、<see cref="Top"/> 与所有通知）
/// 只归创建它的 WPF Dispatcher 所有。任何集合/属性变更都必须在 <see cref="Dispatcher"/> 上执行；
/// 引擎负责在显示、关闭、取消与关停路径上完成封送。绑定视图直接读取这些属性，因此不得跨线程修改。
/// </summary>
public sealed partial class DialogLayerViewModel : ObservableObject
{
    private readonly ILogger<DialogLayerViewModel>? _logger;
    private int _attachedHosts;
    private bool _isShutdown;

    public DialogLayerViewModel(Dispatcher dispatcher, ILogger<DialogLayerViewModel>? logger = null)
    {
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (!dispatcher.CheckAccess())
        {
            throw new DialogThreadAffinityException("DialogLayerViewModel 必须在弹窗所属 UI 线程上创建。");
        }

        _logger = logger;
    }

    /// <summary>唯一允许修改本实例的 Dispatcher。</summary>
    public Dispatcher Dispatcher { get; }

    public ObservableCollection<DialogEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    private DialogEntryViewModel? _top;

    public bool HasDialogs => Top is not null;

    /// <summary>栈中存在模态弹窗时，背景必须暗化并禁止普通输入。</summary>
    public bool HasModal => Entries.Any(entry => entry.IsModal);

    /// <summary>Shell 背景是否可交互；模态弹窗打开时为 false。</summary>
    public bool IsShellInteractive => !HasModal;

    public bool IsHostAttached => _attachedHosts > 0;

    /// <summary>宿主已关停；关闭后不再接受新弹窗，残留弹窗已按 Closed 完成。</summary>
    public bool IsShutdown => _isShutdown;

    public void AttachHost()
    {
        VerifyAccess();
        _attachedHosts++;
        OnPropertyChanged(nameof(IsHostAttached));
    }

    public void DetachHost()
    {
        VerifyAccess();
        _attachedHosts = Math.Max(0, _attachedHosts - 1);
        OnPropertyChanged(nameof(IsHostAttached));
    }

    /// <summary>
    /// 关停弹窗层：以 <see cref="DialogOutcome.Closed"/> 完成所有打开的弹窗，
    /// 让等待中的调用方（包括嵌套消息循环）确定性退出。重复调用无副作用。
    /// </summary>
    public void Shutdown()
    {
        VerifyAccess();
        if (_isShutdown)
        {
            return;
        }

        _isShutdown = true;
        while (Entries.Count > 0)
        {
            var entry = Entries[^1];
            Entries.RemoveAt(Entries.Count - 1);
            CompleteSafely(entry, DialogOutcome.Closed);
        }

        RaiseStateChanged();
    }

    internal void Add(DialogEntryViewModel entry)
    {
        VerifyAccess();
        if (_isShutdown)
        {
            throw new InvalidOperationException("弹窗宿主已关闭，不能再创建弹窗。");
        }

        Entries.Add(entry);
        RaiseStateChanged();
    }

    /// <summary>
    /// 关闭指定弹窗。嵌套弹窗（在它之后打开的条目）先按 Closed 完成，再完成目标弹窗，
    /// 保证父弹窗关闭时不会留下失去所有者的子弹窗。
    /// </summary>
    internal void CloseEntry(DialogEntryViewModel entry, DialogOutcome outcome, object? value)
    {
        VerifyAccess();
        var index = Entries.IndexOf(entry);
        if (index < 0)
        {
            return;
        }

        // 先修正集合结构，再触发内容回调：即使某个 Closed 处理器抛异常，
        // 弹窗层级与 Top 也已处于一致状态，任务结果仍然只完成一次。
        var closing = new List<DialogEntryViewModel>(Entries.Count - index);
        for (var cursor = index; cursor < Entries.Count; cursor++)
        {
            closing.Add(Entries[cursor]);
        }

        for (var cursor = Entries.Count - 1; cursor >= index; cursor--)
        {
            Entries.RemoveAt(cursor);
        }

        RaiseStateChanged();

        // 子先父后；父弹窗关闭时子弹窗已不再可见，但仍然需要完成各自的等待任务。
        for (var cursor = closing.Count - 1; cursor >= 1; cursor--)
        {
            CompleteSafely(closing[cursor], DialogOutcome.Closed);
        }

        CompleteSafely(closing[0], outcome, value);
    }

    internal void VerifyAccess()
    {
        if (!Dispatcher.CheckAccess())
        {
            throw new DialogThreadAffinityException(
                "弹窗状态只能在弹窗所属 UI 线程上修改；请通过 IDialogEngine 的异步入口或 Dispatcher 封送。");
        }
    }

    private void CompleteSafely(DialogEntryViewModel entry, DialogOutcome outcome, object? value = null)
    {
        try
        {
            entry.CompleteClose(outcome, value);
        }
        catch (Exception exception)
        {
            // 关闭事件处理器属于内容代码；异常不能让其他弹窗或调用方任务悬挂。
            _logger?.LogError(exception, "弹窗关闭事件处理失败：{Title}", entry.Title);
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
