// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Security;
using Ecl.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.App.ViewModels.Security;

/// <summary>安全中心顶部状态的语义；颜色映射见 <see cref="SecurityCenterViewModel.StatusKey"/>。</summary>
public enum SecurityOverallState
{
    NotScanned,
    Scanning,
    Incomplete,
    Clean,
    Warning,
    Critical,
}

/// <summary>
/// 安全中心（hints/ui/pages/security-center.md）。
///
/// 数据与操作边界：
/// - 扫描用手动触发，逐实例串行调用既有 <see cref="IThreatProtectionService.ScanAsync"/>；
///   不读取历史扫描记录，因此启动器重启后页面回到“未扫描”。
/// - 覆盖范围、失败实例与不完全结果如实显示；部分结果绝不显示为绿色完整扫描。
/// - 忽略写入 ADR 0043 的风险接受记录（现有领域 API），不把检测结论改成 Clean；
///   删除使用领域已有的隔离操作（可恢复），不建立任意路径删除 API，启动参数类发现没有文件目标时保持不可用。
/// - 页面不显示具体文件路径，只显示实例名、分类、等级与数量。
/// </summary>
public sealed partial class SecurityCenterViewModel : ObservableObject
{
    private static readonly (string Key, string TitleKey)[] CategoryOrder =
    [
        ("mods", "security.category.mods"),
        ("resourcepacks", "security.category.resourcePacks"),
        ("shaderpacks", "security.category.shaderPacks"),
        ("config", "security.category.config"),
        ("scripts", "security.category.scripts"),
        ("launch-arguments", "security.category.launchArguments"),
        ("other", "security.category.other"),
    ];

    private readonly IInstanceService _instances;
    private readonly IThreatProtectionService _security;
    private readonly IDialogEngine _dialogs;
    private readonly IOperationService _operations;
    private readonly ILocalizer _localizer;
    private readonly ILogger<SecurityCenterViewModel> _logger;
    private readonly HashSet<string> _ignoredKeys = new(StringComparer.Ordinal);
    private CancellationTokenSource? _scanCancellation;
    private bool _isScanComplete;
    private int _instanceTotal;
    private List<string> _lastFailures = [];

    public SecurityCenterViewModel(
        IInstanceService instances,
        IThreatProtectionService security,
        IDialogEngine dialogs,
        IOperationService operations,
        ILocalizer localizer,
        ILogger<SecurityCenterViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(localizer);

        _instances = instances;
        _security = security;
        _dialogs = dialogs;
        _operations = operations;
        _localizer = localizer;
        _logger = logger ?? NullLogger<SecurityCenterViewModel>.Instance;

        StatusText = _localizer.Get("security.status.notScanned");
        DetailText = _localizer.Get("security.status.notScannedDetail");
    }

    /// <summary>用户点击“解决问题”时请求视图把分类结果滚入视野；页面不新增通用删除流程。</summary>
    public event EventHandler? FindingsFocusRequested;

    public ObservableCollection<SecurityCategoryViewModel> Categories { get; } = [];

    [ObservableProperty]
    private SecurityOverallState _overallState = SecurityOverallState.NotScanned;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string _coverageText = string.Empty;

    [ObservableProperty]
    private string _actionHintText = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _hasScanned;

    public string StatusKey => OverallState switch
    {
        SecurityOverallState.Clean => "green",
        SecurityOverallState.Warning => "yellow",
        SecurityOverallState.Critical => "red",
        _ => "blue",
    };

    /// <summary>安全中心只显示盾牌轮廓，不在盾牌内放符号。</summary>
    public string Glyph => string.Empty;

    public string PrimaryText => _localizer.Get("security.scan.start");

    public string CancelScanText => _localizer.Get("security.scan.cancel");

    public string SolveText => _localizer.Get("security.scan.solve");

    public bool ShowScanButton => true;

    public bool CanStartScan => !IsScanning;

    public bool CanCancelScan => IsScanning;

    public bool ShowCancelScan => IsScanning;

    public bool ShowSolveButton => OverallState is SecurityOverallState.Warning or SecurityOverallState.Critical;

    public bool CanModifyFindings => !IsScanning;

    public bool HasCategories => Categories.Count > 0;

    public bool ShowNotScannedHint => !HasScanned && !IsScanning;

    public bool HasCoverage => HasScanned;

    public bool ShowActionHint => !string.IsNullOrWhiteSpace(ActionHintText);

    /// <summary>首次进入与切回安全中心时调用；扫描结果按页面对象存活，不因重新进入而刷新。</summary>
    public void Activate()
    {
        if (!HasScanned && !IsScanning)
        {
            StatusText = _localizer.Get("security.status.notScanned");
            DetailText = _localizer.Get("security.status.notScannedDetail");
        }
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        _scanCancellation = new CancellationTokenSource();
        var cancellation = _scanCancellation;
        var operation = _operations.Begin(new OperationStart
        {
            Kind = OperationKind.Verification,
            Title = _localizer.Get("security.scan.operationTitle"),
            StageText = _localizer.Get("security.status.scanning"),
            CanCancel = true,
        });
        _operations.AttachControl(operation.Id, new ScanControl(cancellation));

        Categories.Clear();
        NotifyCategoriesChanged();
        HasScanned = false;
        _isScanComplete = false;
        ActionHintText = string.Empty;
        OverallState = SecurityOverallState.Scanning;
        IsScanning = true;
        StatusText = _localizer.Get("security.status.scanning");
        DetailText = _localizer.Get("security.status.scanningDetail");
        CoverageText = string.Empty;

        var reports = new List<ThreatScanReport>();
        var instanceNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<string>();
        try
        {
            var instances = await _instances.GetInstancesAsync(cancellation.Token).ConfigureAwait(true);
            _instanceTotal = instances.Count;
            _logger.LogInformation("安全扫描开始：{Count} 个实例。", instances.Count);
            if (instances.Count == 0)
            {
                operation.Complete();
                StatusText = _localizer.Get("security.status.noInstances");
                DetailText = _localizer.Get("security.status.noInstancesDetail");
                OverallState = SecurityOverallState.NotScanned;
                return;
            }

            foreach (var instance in instances)
            {
                instanceNames[instance.Id] = instance.Name;
            }

            var total = instances.Count;
            var completed = 0;
            foreach (var instance in instances)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                operation.Report(new OperationProgressUpdate
                {
                    Completed = completed,
                    Total = total,
                    CurrentItem = instance.Name,
                    StageText = _localizer.Get("security.status.scanning"),
                });

                try
                {
                    var scan = await _security
                        .ScanAsync(instance.Id, options: null, progress: null, cancellation.Token)
                        .ConfigureAwait(true);
                    if (scan.IsSuccess)
                    {
                        reports.Add(scan.Value!);
                    }
                    else
                    {
                        failures.Add(_localizer.Get(
                            "security.scan.instanceFailed",
                            instance.Name,
                            DescribeScanError(scan.Error)));
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "实例 {Instance} 的安全扫描失败。", instance.Name);
                    failures.Add(_localizer.Get("security.scan.instanceUnreadable", instance.Name));
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "实例 {Instance} 的安全扫描失败。", instance.Name);
                    failures.Add(_localizer.Get(
                        "security.scan.instanceUnreadable",
                        instance.Name));
                }

                completed++;
            }

            operation.Complete(new OperationProgressUpdate
            {
                Completed = total,
                Total = total,
                StageText = _localizer.Get("security.scan.completed"),
            });

            _isScanComplete = failures.Count == 0 && reports.All(report => report.IsComplete);
            HasScanned = true;
            _lastFailures = failures.ToList();
            BuildCategories(reports, instanceNames, failures);
            UpdateOverallState(failures);
            _logger.LogInformation(
                "安全扫描完成：实例 {Scanned}/{Total}，覆盖问题 {Failures}，结果 {State}。",
                reports.Count,
                _instanceTotal,
                failures.Count,
                OverallState);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            operation.Cancel();
            _isScanComplete = false;
            HasScanned = reports.Count > 0;
            _lastFailures = failures.Append(_localizer.Get("security.status.cancelled")).ToList();
            if (HasScanned)
            {
                BuildCategories(reports, instanceNames, failures);
            }

            OverallState = SecurityOverallState.Incomplete;
            StatusText = _localizer.Get("security.status.cancelled");
            DetailText = _localizer.Get("security.status.cancelledDetail");
            _logger.LogInformation("安全扫描已取消：已完成 {Completed}/{Total} 个实例。", reports.Count, _instanceTotal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "安全扫描未能开始或中断。");
            operation.Fail(new OperationError(
                OperationErrorKind.LocalIO,
                "security.scan_failed",
                exception.Message));
            _isScanComplete = false;
            OverallState = SecurityOverallState.Incomplete;
            StatusText = _localizer.Get("security.status.failed");
            DetailText = _localizer.Get("security.status.failedDetail");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "安全扫描未能开始或中断。");
            operation.Fail(new OperationError(
                OperationErrorKind.EclInternal,
                "security.scan_failed",
                exception.Message));
            _isScanComplete = false;
            OverallState = SecurityOverallState.Incomplete;
            StatusText = _localizer.Get("security.status.failed");
            DetailText = _localizer.Get("security.status.failedDetail");
        }
        finally
        {
            IsScanning = false;
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            operation.Dispose();
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCancellation?.Cancel();

    [RelayCommand]
    private void Solve()
    {
        if (!ShowSolveButton)
        {
            return;
        }

        ActionHintText = _localizer.Get("security.solve.hint");
        FindingsFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task IgnoreAsync(SecurityFindingItemViewModel? row)
    {
        if (row is null || row.IsIgnored || IsScanning)
        {
            return;
        }

        try
        {
            var (severity, warningKey) = row.LevelKey switch
            {
                "high" or "confirmed" => (DialogSeverity.Error, "security.ignore.warning.critical"),
                "suspicious" or "unknown" => (DialogSeverity.Warning, "security.ignore.warning.suspicious"),
                _ => (DialogSeverity.Information, "security.ignore.warning.notice"),
            };

            var message = _localizer.Get("security.ignore.message", row.InstanceName, row.LevelText) +
                          Environment.NewLine + Environment.NewLine +
                          _localizer.Get(warningKey);
            var result = await _dialogs.ShowAsync(new DialogRequest
            {
                Title = _localizer.Get("security.ignore.title"),
                Content = new MessageDialogViewModel(
                    message,
                    _localizer.Get("security.ignore.confirm"),
                    cancelText: _localizer.Get("dialog.cancel"),
                    severity: severity),
                InitialFocus = DialogInitialFocus.Cancel,
                Severity = severity,
                MaxWidth = 560,
            }).ConfigureAwait(true);

            if (!result.IsPrimary)
            {
                return;
            }

            var accepted = await _security
                .AcceptRiskAsync(row.InstanceId, [row.Finding])
                .ConfigureAwait(true);
            if (!accepted.IsSuccess)
            {
                StatusText = _localizer.Get("security.ignore.failed", accepted.Error?.Message ?? string.Empty);
                return;
            }

            row.IsIgnored = true;
            _ignoredKeys.Add(row.IdentityKey);
            RecomputeCategories();
            StatusText = _localizer.Get("security.ignore.done");
        }
        catch (DialogHostUnavailableException)
        {
            StatusText = _localizer.Get("security.dialogUnavailable");
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SecurityFindingItemViewModel? row)
    {
        if (row is null || !row.CanDelete || row.IsDeleted || IsScanning)
        {
            return;
        }

        try
        {
            var message = _localizer.Get("security.delete.message", row.InstanceName, row.LevelText) +
                          Environment.NewLine + Environment.NewLine +
                          _localizer.Get("security.delete.recoverable");
            var result = await _dialogs.ShowAsync(new DialogRequest
            {
                Title = _localizer.Get("security.delete.title"),
                Content = new MessageDialogViewModel(
                    message,
                    _localizer.Get("security.delete.confirm"),
                    cancelText: _localizer.Get("dialog.cancel"),
                    severity: DialogSeverity.Warning,
                    isDestructive: true),
                InitialFocus = DialogInitialFocus.Cancel,
                Severity = DialogSeverity.Warning,
                MaxWidth = 560,
            }).ConfigureAwait(true);

            if (!result.IsPrimary)
            {
                return;
            }

            var quarantined = await _security
                .QuarantineAsync(row.InstanceId, row.RelativePath)
                .ConfigureAwait(true);
            if (!quarantined.IsSuccess)
            {
                StatusText = _localizer.Get("security.delete.failed", quarantined.Error?.Message ?? string.Empty);
                return;
            }

            row.MarkDeleted();
            RemoveRow(row);
            StatusText = _localizer.Get("security.delete.done");
        }
        catch (DialogHostUnavailableException)
        {
            StatusText = _localizer.Get("security.dialogUnavailable");
        }
    }

    partial void OnOverallStateChanged(SecurityOverallState value)
    {
        OnPropertyChanged(nameof(StatusKey));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(ShowSolveButton));
    }

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartScan));
        OnPropertyChanged(nameof(CanCancelScan));
        OnPropertyChanged(nameof(ShowCancelScan));
        OnPropertyChanged(nameof(CanModifyFindings));
        OnPropertyChanged(nameof(ShowNotScannedHint));
    }

    partial void OnHasScannedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNotScannedHint));
        OnPropertyChanged(nameof(HasCoverage));
    }

    partial void OnActionHintTextChanged(string value) => OnPropertyChanged(nameof(ShowActionHint));

    private void UpdateOverallState(IReadOnlyList<string> failures)
    {
        var active = Categories
            .SelectMany(category => category.Findings)
            .Where(item => !item.IsIgnored && !item.IsDeleted)
            .ToArray();
        var red = active.Any(item => item.LevelKey is "high" or "confirmed");
        var yellow = active.Any(item => item.LevelKey is "notice" or "suspicious" or "unknown");

        OverallState = red
            ? SecurityOverallState.Critical
            : yellow
                ? SecurityOverallState.Warning
                : _isScanComplete
                    ? SecurityOverallState.Clean
                    : SecurityOverallState.Incomplete;

        StatusText = OverallState switch
        {
            SecurityOverallState.Critical => _localizer.Get("security.status.critical"),
            SecurityOverallState.Warning => _localizer.Get("security.status.warning"),
            SecurityOverallState.Clean => _localizer.Get("security.status.clean"),
            _ => _localizer.Get("security.status.incomplete"),
        };

        var detail = OverallState switch
        {
            SecurityOverallState.Critical => _localizer.Get("security.status.criticalDetail"),
            SecurityOverallState.Warning => _localizer.Get("security.status.warningDetail"),
            SecurityOverallState.Clean => _localizer.Get("security.status.cleanDetail"),
            _ => _localizer.Get("security.status.incompleteDetail"),
        };
        if (failures.Count > 0)
        {
            detail = string.Join(Environment.NewLine, new[] { detail }.Concat(failures));
        }

        DetailText = detail;
        CoverageText = BuildCoverageText(failures);
    }

    private string BuildCoverageText(IReadOnlyList<string> failures)
    {
        var scanned = ScannedReports?.Count ?? 0;
        var files = 0;
        var archives = 0;
        if (ScannedReports is { Count: > 0 } scannedReports)
        {
            files = scannedReports.Sum(report => report.FilesScanned);
            archives = scannedReports.Sum(report => report.ArchivesScanned);
        }

        var text = _localizer.Get("security.scan.coverage", scanned, _instanceTotal, files, archives);
        if (!_isScanComplete)
        {
            text += Environment.NewLine + _localizer.Get("security.scan.coverageIncomplete", failures.Count);
        }

        return text;
    }

    private List<ThreatScanReport>? ScannedReports { get; set; }

    private void BuildCategories(
        IReadOnlyList<ThreatScanReport> reports,
        Dictionary<string, string> instanceNames,
        IReadOnlyList<string> failures)
    {
        ScannedReports = reports.ToList();
        var categories = new Dictionary<string, SecurityCategoryViewModel>(StringComparer.Ordinal);
        foreach (var report in reports)
        {
            var instanceName = instanceNames.TryGetValue(report.InstanceId, out var name)
                ? name
                : report.InstanceId;
            foreach (var finding in report.Findings)
            {
                var key = CategoryKey(finding.RelativePath);
                if (!categories.TryGetValue(key, out var category))
                {
                    category = new SecurityCategoryViewModel(
                        key,
                        _localizer.Get(CategoryOrder.First(item => item.Key == key).TitleKey),
                        _localizer);
                    categories[key] = category;
                }

                var identity = IdentityKey(report.InstanceId, finding);
                category.Findings.Add(new SecurityFindingItemViewModel(
                    report.InstanceId,
                    instanceName,
                    finding,
                    key,
                    identity,
                    _ignoredKeys.Contains(identity),
                    _localizer));
            }
        }

        foreach (var (key, titleKey) in CategoryOrder)
        {
            if (!categories.TryGetValue(key, out var category))
            {
                category = new SecurityCategoryViewModel(key, _localizer.Get(titleKey), _localizer);
                categories[key] = category;
            }

            category.Recompute();
            Categories.Add(category);
        }

        NotifyCategoriesChanged();
        UpdateOverallState(failures);
    }

    private void RecomputeCategories()
    {
        foreach (var category in Categories)
        {
            category.Recompute();
        }

        UpdateOverallState(_lastFailures);
        OnPropertyChanged(nameof(HasCategories));
        OnPropertyChanged(nameof(StatusKey));
    }

    private void RemoveRow(SecurityFindingItemViewModel row)
    {
        if (Categories.FirstOrDefault(category => category.Key == row.CategoryKey) is not { } category)
        {
            return;
        }

        category.Findings.Remove(row);
        category.Recompute();
        RecomputeCategories();
    }

    private void NotifyCategoriesChanged() => OnPropertyChanged(nameof(HasCategories));

    private static string CategoryKey(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
        {
            return "mods";
        }

        if (normalized.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase))
        {
            return "resourcepacks";
        }

        if (normalized.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase))
        {
            return "shaderpacks";
        }

        if (normalized.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
        {
            return "config";
        }

        if (normalized.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
        {
            return "scripts";
        }

        if (string.Equals(normalized, "launch-arguments", StringComparison.OrdinalIgnoreCase))
        {
            return "launch-arguments";
        }

        return "other";
    }

    private static string IdentityKey(string instanceId, ThreatFinding finding) =>
        string.Join(
            '|',
            instanceId,
            finding.RelativePath,
            finding.Sha256 ?? string.Empty,
            finding.Evidence.FirstOrDefault()?.RuleId ?? string.Empty,
            finding.Level);

    private string DescribeScanError(EclError? error) => error?.Code switch
    {
        "security.root_missing" => _localizer.Get("security.scan.rootMissing"),
        "instance.not_found" => _localizer.Get("security.scan.instanceMissing"),
        "security.invalid_instance" => _localizer.Get("security.scan.instanceInvalid"),
        _ => _localizer.Get("security.scan.unreadable"),
    };

    private sealed class ScanControl(CancellationTokenSource cancellation) : IOperationControl
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

/// <summary>一个扫描分类：固定顺序、当前未忽略计数与最高等级标记。</summary>
public sealed class SecurityCategoryViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private readonly ILocalizer _localizer;

    public SecurityCategoryViewModel(string key, string title, ILocalizer localizer)
    {
        Key = key;
        Title = title;
        _localizer = localizer;
    }

    public string Key { get; }

    public string Title { get; }

    public ObservableCollection<SecurityFindingItemViewModel> Findings { get; } = [];

    public int ActiveCount { get; private set; }

    public bool HasAny => Findings.Count > 0;

    public bool HasActive => ActiveCount > 0;

    public string MarkerKey { get; private set; } = "none";

    public string MarkerText => MarkerKey switch
    {
        "red" => "X",
        "yellow" => "！",
        _ => string.Empty,
    };

    public string CountText { get; private set; } = string.Empty;

    public void Recompute()
    {
        var active = Findings.Where(item => !item.IsIgnored && !item.IsDeleted).ToArray();
        ActiveCount = active.Length;
        MarkerKey = active.Any(item => item.LevelKey is "high" or "confirmed")
            ? "red"
            : active.Any(item => item.LevelKey is "notice" or "suspicious" or "unknown")
                ? "yellow"
                : "none";
        CountText = ActiveCount > 0
            ? _localizer.Get("security.category.count", ActiveCount)
            : _localizer.Get("security.category.none");

        OnPropertyChanged(nameof(Findings));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(HasAny));
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(MarkerKey));
        OnPropertyChanged(nameof(MarkerText));
        OnPropertyChanged(nameof(CountText));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>一条安全发现。不暴露文件路径给视图；命令参数只携带领域发现与实例身份。</summary>
public sealed partial class SecurityFindingItemViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;

    public SecurityFindingItemViewModel(
        string instanceId,
        string instanceName,
        ThreatFinding finding,
        string categoryKey,
        string identityKey,
        bool isIgnored,
        ILocalizer localizer)
    {
        InstanceId = instanceId;
        InstanceName = instanceName;
        Finding = finding;
        CategoryKey = categoryKey;
        IdentityKey = identityKey;
        _isIgnored = isIgnored;
        _localizer = localizer;
        CanDelete = !string.IsNullOrWhiteSpace(finding.RelativePath) &&
                    !string.Equals(finding.RelativePath, "launch-arguments", StringComparison.OrdinalIgnoreCase);
        DeleteUnavailableReason = CanDelete
            ? null
            : _localizer.Get("security.delete.unavailable.noFile");
    }

    public string InstanceId { get; }

    public string InstanceName { get; }

    public ThreatFinding Finding { get; }

    public string CategoryKey { get; }

    /// <summary>页面内的稳定忽略身份：实例 + 路径 + 哈希 + 规则 + 等级。</summary>
    public string IdentityKey { get; }

    public string RelativePath => Finding.RelativePath;

    public bool CanDelete { get; }

    public string? DeleteUnavailableReason { get; }

    public string LevelKey => Finding.Level switch
    {
        ThreatLevel.ConfirmedThreat => "confirmed",
        ThreatLevel.HighRisk => "high",
        ThreatLevel.Suspicious => "suspicious",
        ThreatLevel.Notice => "notice",
        ThreatLevel.Unknown => "unknown",
        _ => "clean",
    };

    public string LevelText => _localizer.Get($"security.finding.level.{LevelKey}");

    public string MarkerText => LevelKey switch
    {
        "confirmed" or "high" => "X",
        "suspicious" or "notice" or "unknown" => "！",
        _ => string.Empty,
    };

    [ObservableProperty]
    private bool _isIgnored;

    [ObservableProperty]
    private bool _isDeleted;

    public bool CanIgnore => !IsIgnored && !IsDeleted;

    public string StateText => IsIgnored ? _localizer.Get("security.finding.ignored") : string.Empty;

    public bool HasStateText => IsIgnored;

    partial void OnIsIgnoredChanged(bool value)
    {
        OnPropertyChanged(nameof(CanIgnore));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasStateText));
    }

    public void MarkDeleted() => IsDeleted = true;

    partial void OnIsDeletedChanged(bool value) => OnPropertyChanged(nameof(CanIgnore));
}
