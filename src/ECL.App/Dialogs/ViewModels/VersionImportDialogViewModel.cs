// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Ecl.Operations;

namespace Ecl.App.Dialogs.ViewModels;

/// <summary>
/// 版本导入弹窗内容（ADR 0052、迁移规划 §6.2 #15）：把游戏目录 <c>versions</c> 中其它启动器
/// 安装的版本多选导入为实例。只建立引用、不复制文件；批量执行登记为统一 Operation（Kind=Import），
/// 因此进度与结果进入 Output，而不是页面私有的任务列表。
///
/// 取消只发生在两个版本之间：领域导入调用接收取消令牌，已完成的版本保持已导入状态，可稍后重试其余项。
/// </summary>
public sealed partial class VersionImportDialogViewModel : ObservableObject, IDialogSessionAware
{
    private readonly IInstanceService _instances;
    private readonly IOperationService _operations;
    private readonly ILocalizer _localizer;
    private IDialogSession? _session;

    public VersionImportDialogViewModel(
        IInstanceService instances,
        IOperationService operations,
        ILocalizer localizer,
        IReadOnlyList<DetectedVersion> detected)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(detected);

        _instances = instances;
        _operations = operations;
        _localizer = localizer;

        foreach (var version in detected)
        {
            Items.Add(new VersionImportItemViewModel(version, localizer));
        }
    }

    public ObservableCollection<VersionImportItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public string Hint => _localizer.Get("instances.importHint");

    public string ImportText => _localizer.Get("instances.importSelected");

    public string CancelText => _localizer.Get("authlib.cancel");

    public int ImportedCount { get; private set; }

    void IDialogSessionAware.Attach(IDialogSession session) => _session = session;

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var selected = Items.Where(item => item.IsChecked && item.CanImport).ToArray();
        if (selected.Length == 0)
        {
            StatusText = _localizer.Get("instances.importSelectAtLeastOne");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var started = _operations.Begin(new OperationStart
        {
            Kind = OperationKind.Import,
            Title = _localizer.Get("instances.importTitle"),
            StageText = _localizer.Get("instances.importWorking"),
            Total = selected.Length,
            CanCancel = true,
        });
        _operations.AttachControl(started.Id, new CancellableControl(cancellation));

        SetBusy(true);
        var completed = 0;
        var failures = new List<string>();
        var success = false;
        try
        {
            foreach (var item in selected)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                started.Report(new OperationProgressUpdate
                {
                    Completed = completed,
                    Total = selected.Length,
                    CurrentItem = item.Title,
                    StageText = _localizer.Get("instances.importWorking"),
                });

                var result = await _instances
                    .ImportAsync(item.Version.VersionId, cancellationToken: cancellation.Token)
                    .ConfigureAwait(true);
                if (result.IsSuccess)
                {
                    ImportedCount++;
                    item.MarkImported();
                }
                else
                {
                    failures.Add($"{item.Version.VersionId}: {result.Error?.Message}");
                }

                completed++;
            }

            if (failures.Count > 0)
            {
                var message = _localizer.Get(
                    "instances.importFailed",
                    failures.Count,
                    string.Join("; ", failures));
                StatusText = message;
                started.Fail(new OperationError(
                    OperationErrorKind.Configuration,
                    "instance.import_partial",
                    message));
                return;
            }

            started.Complete(new OperationProgressUpdate
            {
                Completed = selected.Length,
                Total = selected.Length,
                StageText = _localizer.Get("instances.importCompleted"),
            });
            StatusText = _localizer.Get("instances.imported", ImportedCount);
            success = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            started.Cancel();
            StatusText = _localizer.Get("instances.importCancelled", ImportedCount);
        }
        finally
        {
            SetBusy(false);
        }

        if (success)
        {
            _session?.TryClose(DialogOutcome.Primary, new VersionImportResult(ImportedCount));
        }
    }

    [RelayCommand]
    private void Cancel() => _session?.TryClose(DialogOutcome.Cancel);

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        _session?.SetBusy(busy);
    }

    private sealed class CancellableControl(CancellationTokenSource cancellation) : IOperationControl
    {
        public Func<CancellationToken, Task>? Retry => null;

        public bool TryCancel()
        {
            if (cancellation.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                cancellation.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }
}

/// <summary>导入弹窗的最终结果；只携带成功导入的数量，不携带版本对象或路径。</summary>
public sealed record VersionImportResult(int ImportedCount);

/// <summary>版本导入列表的一行：标题、真实隔离方式与导入后状态。</summary>
public sealed partial class VersionImportItemViewModel : ObservableObject
{
    public VersionImportItemViewModel(DetectedVersion version, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(localizer);

        Version = version;
        Title = version.VersionId;
        CanImport = version.CanImport;

        var isolation = version.Isolated
            ? localizer.Get("instances.importIsolated")
            : localizer.Get("instances.importShared");
        Detail = version.AlreadyImported
            ? $"{version.Loader} · {isolation} · {localizer.Get("instances.importAlready")}"
            : version.Problem == DetectedVersionProblem.None
                ? $"{version.Loader} · {isolation} · {version.GameDirectory}"
                : $"{version.Loader} · {isolation} · {localizer.Get("instances.importUnusable")}：{version.ProblemDetail}";
        _isChecked = CanImport;
    }

    public DetectedVersion Version { get; }

    public string Title { get; }

    public bool CanImport { get; private set; }

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private string _detail = string.Empty;

    public void MarkImported()
    {
        IsChecked = false;
        CanImport = false;
        Detail = Version.GameDirectory;
        OnPropertyChanged(nameof(CanImport));
    }
}
