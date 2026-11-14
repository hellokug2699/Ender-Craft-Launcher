// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.ViewModels.LogSessions;
using Ecl.Core.Localization;
using Ecl.Sessions;

namespace Ecl.App.Dialogs.ViewModels;

/// <summary>
/// 单会话诊断导出确认（logs.md §5、ADR 0052）。只负责展示范围、隐私提示与脱敏选项；
/// 真正的 ZIP 采集由 <see cref="ISessionExportService"/> 执行，页面不拼接证据。
/// </summary>
public sealed partial class LogExportDialogViewModel : ObservableObject, IDialogSessionAware
{
    private readonly ILocalizer _localizer;
    private IDialogSession? _dialog;

    public LogExportDialogViewModel(
        LogSessionRowViewModel session,
        IReadOnlyList<LogEvidenceRowViewModel> evidence,
        string outputPath,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(evidence);

        _localizer = localizer;
        OutputPath = outputPath;

        foreach (var item in evidence)
        {
            Evidence.Add(item);
        }

        var presentKinds = evidence
            .Select(item => item.KindText)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        ScopeText = presentKinds.Length == 0
            ? localizer.Get("logs.export.scopeEmpty")
            : localizer.Get("logs.export.scope", string.Join(localizer.Get("logs.listSeparator"), presentKinds));
        Title = localizer.Get("logs.export.sessionTitle", session.InstanceName, session.StartedText);
        var running = session.State is GameSessionState.Starting
            or GameSessionState.Running
            or GameSessionState.GameExitedDraining;
        SnapshotNotice = running
            ? localizer.Get("logs.export.runningNotice")
            : string.Empty;
    }

    public string Title { get; }

    public string ScopeText { get; }

    public string OutputPath { get; }

    public string SnapshotNotice { get; }

    public ObservableCollection<LogEvidenceRowViewModel> Evidence { get; } = [];

    public string PrivacyNotice => _localizer.Get("logs.export.privacy");

    public string WarningNotice => _localizer.Get("logs.export.warning");

    public string ExportText => _localizer.Get("logs.export.confirm");

    public string CancelText => _localizer.Get("dialog.cancel");

    public string RedactText => _localizer.Get("logs.export.redact");

    public string RedactHint => _localizer.Get("logs.export.redactHint");

    [ObservableProperty]
    private bool _redact = true;

    public void Attach(IDialogSession session) => _dialog = session;

    [RelayCommand]
    private void Confirm()
    {
        if (_dialog is null)
        {
            return;
        }

        _dialog.TryClose(DialogOutcome.Primary);
    }

    [RelayCommand]
    private void Cancel()
    {
        _dialog?.TryClose(DialogOutcome.Cancel);
    }
}
