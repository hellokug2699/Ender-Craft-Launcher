// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ecl.App.Services;
using Ecl.Core.Localization;

namespace Ecl.App.ViewModels.Security;

/// <summary>
/// 状态中心（hints/ui/pages/runtime-status.md）。
///
/// 只呈现 ECL 自身主体、功能组件与已安装扩展的真实健康状态；组件健康与是否正在运行分开表达。
/// “进行修复”只在存在异常时出现，但因为 ECL 尚未实现 Bootstrapper 修复交接协议（ADR 0051），
/// 按钮保持不可用并说明原因：不显示“已开始修复”，也不在未确认接管时关闭 ECL。
/// </summary>
public sealed partial class RuntimeStatusViewModel : ObservableObject
{
    private readonly IRuntimeStatusService _status;
    private readonly ILocalizer _localizer;
    private bool _loaded;

    public RuntimeStatusViewModel(IRuntimeStatusService status, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(localizer);

        _status = status;
        _localizer = localizer;
        StatusText = _localizer.Get("runtime.status.unknown");
        DetailText = _localizer.Get("runtime.status.unknownDetail");
    }

    public ObservableCollection<RuntimeComponentRowViewModel> Components { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string _statusKey = "unknown";

    [ObservableProperty]
    private string _glyph = "—";

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _detailText;

    [ObservableProperty]
    private string _coverageText = string.Empty;

    [ObservableProperty]
    private string _checkedAtText = string.Empty;

    [ObservableProperty]
    private string _unavailableReason = string.Empty;

    [ObservableProperty]
    private bool _showRepair;

    public string RepairText => _localizer.Get("runtime.repair.button");

    public string RepairUnavailableText => _localizer.Get("runtime.repair.unavailable");

    public bool HasComponents => Components.Count > 0;

    public bool ShowEmptyHint => !IsLoading && _loaded && IsAvailable && Components.Count == 0;

    /// <summary>每次进入状态中心检查一次；本页没有刷新、行级修复或停止/启动入口。</summary>
    public async Task ActivateAsync()
    {
        if (_loaded || IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var report = await _status.InspectAsync().ConfigureAwait(true);
            Apply(report);
            _loaded = true;
        }
        catch (Exception exception)
        {
            Apply(RuntimeStatusReport.Unavailable(exception.Message));
            _loaded = true;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmptyHint));
        }
    }

    private void Apply(RuntimeStatusReport report)
    {
        Components.Clear();
        CheckedAtText = _localizer.Get("runtime.checkedAt", report.CheckedAt.ToString("yyyy-MM-dd HH:mm"));
        UnavailableReason = report.UnavailableReason ?? string.Empty;

        if (!report.IsAvailable)
        {
            IsAvailable = false;
            StatusKey = "unknown";
            Glyph = "—";
            StatusText = _localizer.Get("runtime.status.unavailable");
            DetailText = report.UnavailableReason ?? _localizer.Get("runtime.reason.unknown");
            CoverageText = string.Empty;
            ShowRepair = false;
            OnPropertyChanged(nameof(HasComponents));
            return;
        }

        IsAvailable = true;
        var overall = report.Components
            .Select(component => component.Health)
            .Aggregate(RuntimeHealth.Healthy, Combine);

        StatusKey = overall switch
        {
            RuntimeHealth.Healthy => "green",
            RuntimeHealth.Warning => "yellow",
            RuntimeHealth.Damaged => "red",
            _ => "unknown",
        };
        Glyph = overall switch
        {
            RuntimeHealth.Healthy => "✓",
            RuntimeHealth.Warning => "?",
            RuntimeHealth.Damaged => "X",
            _ => "—",
        };
        StatusText = overall switch
        {
            RuntimeHealth.Healthy => _localizer.Get("runtime.status.healthy"),
            RuntimeHealth.Warning => _localizer.Get("runtime.status.warning"),
            RuntimeHealth.Damaged => _localizer.Get("runtime.status.damaged"),
            _ => _localizer.Get("runtime.status.unknown"),
        };
        DetailText = overall switch
        {
            RuntimeHealth.Healthy => _localizer.Get("runtime.status.healthyDetail"),
            RuntimeHealth.Warning => _localizer.Get("runtime.status.warningDetail"),
            RuntimeHealth.Damaged => _localizer.Get("runtime.status.damagedDetail"),
            _ => _localizer.Get("runtime.status.unknownDetail"),
        };
        CoverageText = report.CoverageText;
        ShowRepair = overall is RuntimeHealth.Warning or RuntimeHealth.Damaged;

        foreach (var component in report.Components)
        {
            Components.Add(new RuntimeComponentRowViewModel(component, _localizer));
        }

        if (!report.IsComplete)
        {
            DetailText += Environment.NewLine + _localizer.Get("runtime.coverageIncomplete");
        }

        OnPropertyChanged(nameof(HasComponents));
    }

    private static RuntimeHealth Combine(RuntimeHealth current, RuntimeHealth next)
    {
        static int Rank(RuntimeHealth health) => health switch
        {
            RuntimeHealth.Damaged => 3,
            RuntimeHealth.Warning => 2,
            RuntimeHealth.Unknown => 1,
            _ => 0,
        };

        return Rank(next) > Rank(current) ? next : current;
    }
}

/// <summary>状态中心的一行：健康圆点、项目名称、运行状态与异常原因。</summary>
public sealed record RuntimeComponentRowViewModel
{
    public RuntimeComponentRowViewModel(RuntimeComponentStatus component, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(localizer);

        Name = component.Name;
        Health = component.Health;
        RunStateText = component.RunState switch
        {
            RuntimeRunState.Running => localizer.Get("runtime.runState.running"),
            RuntimeRunState.Stopped => localizer.Get("runtime.runState.stopped"),
            _ => localizer.Get("runtime.runState.unknown"),
        };
        Reason = component.Health switch
        {
            RuntimeHealth.Healthy => string.Empty,
            RuntimeHealth.Unknown => localizer.Get("runtime.reason.unknown"),
            _ when string.IsNullOrWhiteSpace(component.Reason) => localizer.Get("runtime.reason.unknown"),
            _ => component.Reason,
        };
    }

    public string Name { get; }

    public RuntimeHealth Health { get; }

    public string HealthKey => Health switch
    {
        RuntimeHealth.Healthy => "healthy",
        RuntimeHealth.Warning => "warning",
        RuntimeHealth.Damaged => "damaged",
        _ => "unknown",
    };

    public string RunStateText { get; }

    public string Reason { get; }

    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);
}
