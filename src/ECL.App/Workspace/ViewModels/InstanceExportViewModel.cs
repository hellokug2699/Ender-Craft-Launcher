// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Export;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>导出页的呈现状态；进度条颜色与结果区只读它。</summary>
public enum ExportUiState
{
    Idle,
    Scanning,
    Exporting,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// 一个可导出的实例内容类别。文件数、体积与风险提示都来自真实扫描；
/// 空类别仍显示，避免把未知体积当成 0 或隐藏用户数据。
/// </summary>
public sealed partial class ExportCategoryViewModel : ObservableObject
{
    public ExportCategoryViewModel(ExportCategorySummary summary, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(localizer);

        Id = summary.Id;
        Title = localizer.Get($"export.category.{summary.Id}.title");
        Description = localizer.Get($"export.category.{summary.Id}.description");
        RiskText = localizer.Get($"export.risk.{summary.Id}");
        HasRisk = RiskText.Length > 0 && !string.Equals(RiskText, $"export.risk.{summary.Id}", StringComparison.Ordinal);
        Summary = localizer.Get(
            "export.category.summary",
            summary.FileCount,
            StorageSizeFormatter.FormatBytes(summary.TotalBytes));
        FileCount = summary.FileCount;
        TotalBytes = summary.TotalBytes;
        IsSelected = true;
    }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    public string RiskText { get; }

    public bool HasRisk { get; }

    public string Summary { get; }

    public int FileCount { get; }

    public long TotalBytes { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>导出结果中被跳过的文件；原因由领域枚举映射为页面文案。</summary>
public sealed class ExportSkippedViewModel
{
    public ExportSkippedViewModel(ExportSkippedFile skipped, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        ArgumentNullException.ThrowIfNull(localizer);

        RelativePath = string.IsNullOrEmpty(skipped.RelativePath)
            ? localizer.Get("export.skipped.unknownPath")
            : skipped.RelativePath;
        ReasonText = localizer.Get(skipped.Reason switch
        {
            ExportSkipReason.MetadataUnreadable => "export.skipped.metadataUnreadable",
            ExportSkipReason.TooManyFiles => "export.skipped.tooManyFiles",
            ExportSkipReason.SourceChanged => "export.skipped.sourceChanged",
            _ => "export.skipped.unreadable",
        });
        DetailText = skipped.Detail ?? string.Empty;
    }

    public string RelativePath { get; }

    public string ReasonText { get; }

    public string DetailText { get; }

    public bool HasDetail => DetailText.Length > 0;
}

/// <summary>
/// 实例工作区「世界与文件 · 导出」页面（B2c，export.md）。
/// <para>
/// 完整导出 / 自定义导出 / 诊断包共用同一页面与任务体系；导出包固定写入实例目录下的
/// <c>exports/</c>，不提供自定义保存位置。开始前显示分类体积与风险提示，结束后显示
/// 真实路径、大小与跳过项；部分失败不得伪装成完整成功。
/// </para>
/// </summary>
public sealed partial class InstanceExportViewModel : ObservableObject, IDisposable
{
    private readonly string _instanceId;
    private readonly IInstanceExportService _exports;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private readonly Action<string>? _openFolder;
    private InstanceExportPlan? _plan;
    private bool _initialized;

    public InstanceExportViewModel(
        string instanceId,
        IInstanceExportService exports,
        IDialogService dialogs,
        ILocalizer localizer,
        Action<string>? openFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(localizer);

        _instanceId = instanceId;
        _exports = exports;
        _dialogs = dialogs;
        _localizer = localizer;
        _openFolder = openFolder;

        Categories.CollectionChanged += (_, _) => RecomputeEstimate();
    }

    public string InstanceId => _instanceId;

    public ObservableCollection<ExportCategoryViewModel> Categories { get; } = [];

    public ObservableCollection<ExportSkippedViewModel> Skipped { get; } = [];

    public bool IsDisposed { get; private set; }

    [ObservableProperty]
    private InstanceExportMode _mode = InstanceExportMode.Full;

    [ObservableProperty]
    private ExportUiState _state = ExportUiState.Idle;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _planWarning = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _progressIndeterminate = true;

    [ObservableProperty]
    private string _progressStage = string.Empty;

    [ObservableProperty]
    private string _progressDetail = string.Empty;

    [ObservableProperty]
    private InstanceExportReport? _report;

    [ObservableProperty]
    private string _resultPath = string.Empty;

    [ObservableProperty]
    private string _resultSize = string.Empty;

    [ObservableProperty]
    private string _resultFiles = string.Empty;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    public string Title => _localizer.Get("instance.nav.export");

    public string Subtitle => _localizer.Get("export.subtitle");

    public string FullModeText => _localizer.Get("export.mode.full");

    public string CustomModeText => _localizer.Get("export.mode.custom");

    public string DiagnosticsModeText => _localizer.Get("export.mode.diagnostics");

    public string FullModeHint => _localizer.Get("export.mode.fullHint");

    public string CustomModeHint => _localizer.Get("export.mode.customHint");

    public string DiagnosticsModeHint => _localizer.Get("export.mode.diagnosticsHint");

    public string StartText => _localizer.Get("export.start");

    public string ScanningText => _localizer.Get("export.scanning");

    public string PlanFailedText => _localizer.Get("export.planFailed");

    public string PermanentExclusionNote => _localizer.Get("export.permanentExclusionNote");

    public string DiagnosticsNote => _localizer.Get("export.diagnostics.note");

    public string ResultTitle => _localizer.Get("export.result.title");

    public string OpenFolderText => _localizer.Get("export.result.openFolder");

    public string SkippedTitle => _localizer.Get("export.result.skippedTitle");

    public bool IsFullMode => Mode == InstanceExportMode.Full;

    public bool IsCustomMode => Mode == InstanceExportMode.Custom;

    public bool IsDiagnosticsMode => Mode == InstanceExportMode.Diagnostics;

    public bool IsCategorySelectionEnabled => IsCustomMode && !IsBusy;

    public bool ShowCategories => !IsDiagnosticsMode;

    public bool IsScanning => State == ExportUiState.Scanning;

    public bool IsExporting => State == ExportUiState.Exporting;

    public bool IsCompleted => State == ExportUiState.Completed;

    public bool IsFailed => State == ExportUiState.Failed;

    public bool IsCancelled => State == ExportUiState.Cancelled;

    public bool ShowProgress => IsScanning || IsExporting || IsCompleted || IsFailed || IsCancelled;

    public bool CanStart => !IsDisposed && !IsBusy && State != ExportUiState.Scanning && HasSelection;

    public bool HasSelection => Mode switch
    {
        InstanceExportMode.Full => _plan is { FileCount: > 0 },
        InstanceExportMode.Custom => Categories.Any(category => category.IsSelected),
        _ => true,
    };

    public string EstimateText
    {
        get
        {
            if (IsDiagnosticsMode)
            {
                return _localizer.Get("export.estimate.unknown");
            }

            if (_plan is null)
            {
                return _localizer.Get("export.estimate.unknown");
            }

            return _localizer.Get(
                "export.estimate",
                StorageSizeFormatter.FormatBytes(SelectedBytes));
        }
    }

    public string SelectedFilesText
    {
        get
        {
            if (IsDiagnosticsMode || _plan is null)
            {
                return _localizer.Get("export.files.unknown");
            }

            return _localizer.Get("export.files", SelectedFileCount);
        }
    }

    public bool HasResult => Report is not null;

    public bool HasSkipped => Skipped.Count > 0;

    public bool ResultIsComplete => Report?.IsComplete ?? false;

    public bool IsPartialResult => Report is { IsComplete: false };

    public bool CanOpenFolder => _openFolder is not null && Report is not null;

    private long SelectedBytes => Mode == InstanceExportMode.Full
        ? _plan?.TotalBytes ?? 0
        : Categories.Where(category => category.IsSelected).Sum(category => category.TotalBytes);

    private int SelectedFileCount => Mode == InstanceExportMode.Full
        ? _plan?.FileCount ?? 0
        : Categories.Where(category => category.IsSelected).Sum(category => category.FileCount);

    public async Task InitializeAsync()
    {
        if (IsDisposed || _initialized)
        {
            return;
        }

        _initialized = true;
        await LoadPlanAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void UseFullMode() => SetMode(InstanceExportMode.Full);

    [RelayCommand]
    private void UseCustomMode() => SetMode(InstanceExportMode.Custom);

    [RelayCommand]
    private void UseDiagnosticsMode() => SetMode(InstanceExportMode.Diagnostics);

    [RelayCommand]
    private async Task RefreshPlanAsync()
    {
        if (IsDisposed || IsBusy)
        {
            return;
        }

        await LoadPlanAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartExportAsync()
    {
        if (IsDisposed || IsBusy || !CanStart)
        {
            return;
        }

        var categories = IsDiagnosticsMode
            ? null
            : IsFullMode
                ? null
                : new HashSet<string>(
                    Categories.Where(category => category.IsSelected).Select(category => category.Id),
                    StringComparer.Ordinal);
        if (IsCustomMode && categories is { Count: 0 })
        {
            StatusMessage = _localizer.Get("export.noSelection");
            return;
        }

        if (!_dialogs.Confirm(
                _localizer.Get("export.confirmTitle"),
                BuildConfirmMessage()))
        {
            return;
        }

        IsBusy = true;
        State = ExportUiState.Exporting;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        Report = null;
        Skipped.Clear();
        ProgressValue = 0;
        ProgressIndeterminate = true;
        ProgressStage = _localizer.Get("export.stage.preparing");
        ProgressDetail = string.Empty;
        NotifyState();
        try
        {
            var report = new Progress<InstanceExportProgress>(OnProgress);
            var result = await _exports
                .ExportAsync(
                    _instanceId,
                    new ExportRequest(Mode, categories),
                    report)
                .ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            if (result.IsSuccess)
            {
                ApplyReport(result.Value!);
                return;
            }

            if (string.Equals(result.Error?.Code, "export.cancelled", StringComparison.Ordinal))
            {
                State = ExportUiState.Cancelled;
                StatusMessage = _localizer.Get("export.cancelled");
                return;
            }

            State = ExportUiState.Failed;
            ErrorMessage = result.Error?.ToString() ?? _localizer.Get("export.failed");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed)
            {
                State = ExportUiState.Failed;
                ErrorMessage = exception.Message;
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                IsBusy = false;
                NotifyState();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        if (Report is not null && _openFolder is not null)
        {
            _openFolder(Report.OutputDirectory);
        }
    }

    private void SetMode(InstanceExportMode mode)
    {
        if (IsDisposed || IsBusy || Mode == mode)
        {
            return;
        }

        Mode = mode;
        StatusMessage = string.Empty;
        ErrorMessage = string.Empty;
        RecomputeEstimate();
        NotifyMode();
    }

    private async Task LoadPlanAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        State = ExportUiState.Scanning;
        IsBusy = true;
        ErrorMessage = string.Empty;
        PlanWarning = string.Empty;
        NotifyState();
        try
        {
            var result = await _exports.PlanAsync(_instanceId).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            Categories.Clear();
            if (!result.IsSuccess)
            {
                _plan = null;
                ErrorMessage = result.Error?.ToString() ?? PlanFailedText;
                State = ExportUiState.Failed;
                return;
            }

            _plan = result.Value!;
            PlanWarning = _plan.Warning ?? string.Empty;
            foreach (var summary in _plan.Categories)
            {
                var category = new ExportCategoryViewModel(summary, _localizer);
                category.PropertyChanged += OnCategoryPropertyChanged;
                Categories.Add(category);
            }

            State = ExportUiState.Idle;
            RecomputeEstimate();
        }
        finally
        {
            if (!IsDisposed)
            {
                IsBusy = false;
                NotifyState();
            }
        }
    }

    private void ApplyReport(InstanceExportReport report)
    {
        Report = report;
        ResultPath = _localizer.Get("export.result.path", report.OutputPath);
        ResultSize = _localizer.Get("export.result.size", StorageSizeFormatter.FormatBytes(report.Bytes));
        ResultFiles = report.IsDiagnostics
            ? _localizer.Get("export.result.diagnosticsFiles", report.DiagnosticEntries)
            : _localizer.Get("export.result.files", report.FileCount);
        ResultSummary = report.IsComplete
            ? _localizer.Get("export.result.complete")
            : _localizer.Get("export.result.partial", report.Skipped.Count);

        Skipped.Clear();
        foreach (var skipped in report.Skipped)
        {
            Skipped.Add(new ExportSkippedViewModel(skipped, _localizer));
        }

        State = ExportUiState.Completed;
        ProgressValue = 100;
        ProgressIndeterminate = false;
        ProgressStage = string.Empty;
        ProgressDetail = string.Empty;
        NotifyState();
    }

    private void OnProgress(InstanceExportProgress progress)
    {
        if (IsDisposed)
        {
            return;
        }

        var ratio = progress.TotalBytes > 0
            ? (double)progress.BytesCompleted / progress.TotalBytes
            : progress.Total > 0
                ? (double)progress.Completed / progress.Total
                : 0;
        ProgressValue = Math.Clamp(ratio * 100, 0, 100);
        ProgressIndeterminate = progress.Total == 0 && progress.TotalBytes == 0;
        ProgressStage = progress.StageId switch
        {
            "export-scan" => _localizer.Get("export.stage.scan"),
            "export-pack" => _localizer.Get("export.stage.pack", progress.CurrentItem ?? string.Empty),
            "export-diagnostics" => _localizer.Get("export.stage.diagnostics"),
            _ => _localizer.Get("export.stage.finish"),
        };
        ProgressDetail = progress.Total > 0
            ? _localizer.Get("export.stage.progress", progress.Completed, progress.Total)
            : string.Empty;
    }

    private void OnCategoryPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ExportCategoryViewModel.IsSelected))
        {
            return;
        }

        RecomputeEstimate();
    }

    private void RecomputeEstimate()
    {
        // 上一次导出的结果只对应当时的模式与类别；配置变化后不得继续显示为“当前结果”。
        if (Report is not null && !IsBusy)
        {
            ClearResult();
        }

        OnPropertyChanged(nameof(EstimateText));
        OnPropertyChanged(nameof(SelectedFilesText));
        OnPropertyChanged(nameof(HasSelection));
        StartExportCommand.NotifyCanExecuteChanged();
    }

    private void ClearResult()
    {
        Report = null;
        Skipped.Clear();
        ResultPath = string.Empty;
        ResultSize = string.Empty;
        ResultFiles = string.Empty;
        ResultSummary = string.Empty;
        State = ExportUiState.Idle;
        ProgressValue = 0;
        ProgressIndeterminate = true;
        ProgressStage = string.Empty;
        ProgressDetail = string.Empty;
        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(IsExporting));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsCategorySelectionEnabled));
        OnPropertyChanged(nameof(CanOpenFolder));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasSkipped));
        OnPropertyChanged(nameof(ResultIsComplete));
        OnPropertyChanged(nameof(IsPartialResult));
        StartExportCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    private void NotifyMode()
    {
        OnPropertyChanged(nameof(IsFullMode));
        OnPropertyChanged(nameof(IsCustomMode));
        OnPropertyChanged(nameof(IsDiagnosticsMode));
        OnPropertyChanged(nameof(IsCategorySelectionEnabled));
        OnPropertyChanged(nameof(ShowCategories));
        OnPropertyChanged(nameof(EstimateText));
        OnPropertyChanged(nameof(SelectedFilesText));
        OnPropertyChanged(nameof(HasSelection));
        StartExportCommand.NotifyCanExecuteChanged();
    }

    private string BuildConfirmMessage()
    {
        var risks = Categories
            .Where(category => category.IsSelected && category.HasRisk)
            .Select(category => category.RiskText)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var riskText = risks.Length == 0
            ? string.Empty
            : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, risks);
        if (IsDiagnosticsMode)
        {
            return _localizer.Get("export.confirmMessage.diagnostics", riskText);
        }

        return _localizer.Get(
            "export.confirmMessage",
            IsCustomMode ? CustomModeText : FullModeText,
            SelectedFileCount,
            StorageSizeFormatter.FormatBytes(SelectedBytes),
            riskText);
    }

    partial void OnModeChanged(InstanceExportMode value) => NotifyMode();

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        foreach (var category in Categories)
        {
            category.PropertyChanged -= OnCategoryPropertyChanged;
        }

        Categories.Clear();
        Skipped.Clear();
    }
}
