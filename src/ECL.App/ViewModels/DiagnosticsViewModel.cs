// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Core.Localization;
using Ecl.Diagnostics;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Instances;

namespace Ecl.App.ViewModels;

/// <summary>
/// 本地诊断工具（工具中心「本地诊断」条目）。只通过 <see cref="IDiagnosticAnalysisService"/>
/// 读取会话证据，不自行枚举实例目录、也不在 ViewModel 里构造规则引擎。
/// </summary>
/// <remarks>
/// 证据与推断分开展示：每条 <see cref="DiagnosticFindingRow"/> 同时提供证据行、候选原因与缺失证据，
/// 没有知识库时明确显示“只做结构化解析，不产生结论”，不输出猜测性根因。
/// 承载位置仍待迁移规划 §6.2 #12 的「诊断 → 日志/诊断页面（G-01）」；本批不复制第二套日志界面。
/// </remarks>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IInstanceService _instances;
    private readonly IDiagnosticAnalysisService _analysis;
    private readonly ILocalizer _localizer;
    private bool _activated;

    public DiagnosticsViewModel(
        IInstanceService instances,
        IDiagnosticAnalysisService analysis,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(localizer);

        _instances = instances;
        _analysis = analysis;
        _localizer = localizer;

        // 初始状态就是“未加载知识库”：在用户触发分析前也必须如实说明能力边界。
        _status = localizer.Get("diagnostics.status.ready");
        _knowledgeNote = localizer.Get("diagnostics.coverage.noKnowledge");
        _sessionText = localizer.Get("diagnostics.session", localizer.Get("diagnostics.noSession"));
    }

    public ObservableCollection<InstanceChoice> Instances { get; } = [];

    public ObservableCollection<DiagnosticFindingRow> Findings { get; } = [];

    public ObservableCollection<DiagnosticWorldTargetRow> WorldTargets { get; } = [];

    public ObservableCollection<DiagnosticHistoryRow> History { get; } = [];

    [ObservableProperty]
    private InstanceChoice? _selectedInstance;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _uncertainty = string.Empty;

    [ObservableProperty]
    private string _knowledgeNote = string.Empty;

    [ObservableProperty]
    private string _sessionText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasFindings => Findings.Count > 0;

    public bool HasWorldTargets => WorldTargets.Count > 0;

    public bool HasHistory => History.Count > 0;

    public async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        Status = _localizer.Get("diagnostics.status.ready");
        KnowledgeNote = _localizer.Get("diagnostics.coverage.noKnowledge");
        SessionText = _localizer.Get("diagnostics.session", _localizer.Get("diagnostics.noSession"));

        try
        {
            var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);
            Instances.Clear();
            foreach (var instance in instances)
            {
                Instances.Add(new InstanceChoice(instance.Id, $"{instance.Name} · {instance.GameVersion}"));
            }

            SelectedInstance = Instances.FirstOrDefault();
            Status = Instances.Count == 0
                ? _localizer.Get("settings.noInstances")
                : _localizer.Get("diagnostics.status.ready");
            await LoadHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Status = _localizer.Get("diagnostics.status.failed", exception.Message);
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (SelectedInstance is null)
        {
            Status = _localizer.Get("instances.selectFirst");
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _analysis
                .AnalyzeAsync(
                    SelectedInstance.Id,
                    new DiagnosticAnalysisOptions
                    {
                        SaveHistory = true,
                        // 世界定位只读会话证据；地图标记是否允许由领域 SupportsMapHighlight 判定。
                        IncludeWorldEvents = true,
                    })
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                Status = _localizer.Get("diagnostics.status.failed", result.Error?.Message ?? "error");
                return;
            }

            ApplyReport(result.Value!);
            await LoadHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            Status = _localizer.Get("diagnostics.status.failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        await LoadHistoryAsync().ConfigureAwait(true);
    }

    partial void OnSelectedInstanceChanged(InstanceChoice? value)
    {
        // 切换实例必须先清空上一实例的结论，避免把旧结果误认为当前实例的诊断。
        Findings.Clear();
        WorldTargets.Clear();
        History.Clear();
        Summary = string.Empty;
        Uncertainty = string.Empty;
        SessionText = _localizer.Get("diagnostics.session", _localizer.Get("diagnostics.noSession"));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasWorldTargets));
        OnPropertyChanged(nameof(HasHistory));
    }

    private void ApplyReport(DiagnosticReport report)
    {
        Findings.Clear();
        foreach (var finding in report.Findings)
        {
            Findings.Add(ToRow(finding));
        }

        WorldTargets.Clear();
        foreach (var target in report.WorldTargets)
        {
            WorldTargets.Add(new DiagnosticWorldTargetRow(
                target.Describe(),
                target.SupportsMapHighlight
                    ? _localizer.Get("diagnostics.worldTarget.mapAllowed")
                    : _localizer.Get("diagnostics.worldTarget.mapBlocked"),
                target.SupportsMapHighlight));
        }

        Summary = _localizer.Get(
            "diagnostics.summary",
            report.Findings.Count,
            ConfidenceText(report.OverallConfidence));
        Uncertainty = report.UncertaintyNote ?? string.Empty;
        KnowledgeNote = report.KnowledgeBase is null
            ? _localizer.Get("diagnostics.coverage.noKnowledge")
            : _localizer.Get("diagnostics.knowledgeLoaded", report.KnowledgeVersion ?? "?");
        SessionText = _localizer.Get(
            "diagnostics.session",
            report.SessionId ?? _localizer.Get("diagnostics.noSession"));
        Status = report.Findings.Count == 0
            ? _localizer.Get("diagnostics.status.noFindings")
            : _localizer.Get("diagnostics.status.completed");
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasWorldTargets));
    }

    private async Task LoadHistoryAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var result = await _analysis.LoadHistoryAsync(SelectedInstance.Id).ConfigureAwait(true);
        History.Clear();
        if (!result.IsSuccess)
        {
            OnPropertyChanged(nameof(HasHistory));
            return;
        }

        foreach (var report in result.Value!)
        {
            History.Add(new DiagnosticHistoryRow(
                report.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", _localizer.Culture),
                report.Findings.Count,
                ConfidenceText(report.OverallConfidence),
                report.KnowledgeVersion ?? _localizer.Get("diagnostics.cli.noKnowledgeShort"),
                _localizer.Get(
                    "diagnostics.history.entry",
                    report.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", _localizer.Culture),
                    report.Findings.Count,
                    ConfidenceText(report.OverallConfidence),
                    report.KnowledgeVersion ?? _localizer.Get("diagnostics.cli.noKnowledgeShort"))));
        }

        OnPropertyChanged(nameof(HasHistory));
    }

    private DiagnosticFindingRow ToRow(DiagnosticFinding finding)
    {
        var evidence = finding.Evidence
            .Select(item => _localizer.Get("diagnostics.evidence.line", item.File, item.Line, item.Excerpt))
            .ToArray();
        var hypotheses = finding.Hypotheses
            .Select(hypothesis => $"{hypothesis.Statement}（{ConfidenceText(hypothesis.Confidence)}）")
            .ToArray();
        var missingEvidence = finding.Hypotheses
            .SelectMany(hypothesis => hypothesis.MissingEvidence)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new DiagnosticFindingRow(
            finding.Title,
            SeverityText(finding.Severity),
            ConfidenceText(finding.Confidence),
            finding.Category,
            evidence,
            finding.SuggestedActions,
            hypotheses,
            missingEvidence,
            finding.Explanation);
    }

    private string ConfidenceText(DiagnosticConfidence confidence) => confidence switch
    {
        DiagnosticConfidence.DirectEvidence => _localizer.Get("diagnostics.confidence.directEvidence"),
        DiagnosticConfidence.HighlyLikely => _localizer.Get("diagnostics.confidence.highlyLikely"),
        _ => _localizer.Get("diagnostics.confidence.undetermined"),
    };

    private string SeverityText(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Critical => _localizer.Get("diagnostics.severity.critical"),
        DiagnosticSeverity.Error => _localizer.Get("diagnostics.severity.error"),
        DiagnosticSeverity.Warning => _localizer.Get("diagnostics.severity.warning"),
        _ => _localizer.Get("diagnostics.severity.info"),
    };
}

/// <summary>一条结论的可绑定视图：标题/严重度/置信度/分类/证据/建议/候选原因/缺失证据/解释。</summary>
public sealed record DiagnosticFindingRow(
    string Title,
    string SeverityText,
    string ConfidenceText,
    string Category,
    IReadOnlyList<string> EvidenceLines,
    IReadOnlyList<string> SuggestedActions,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<string> MissingEvidence,
    string Explanation)
{
    public bool HasEvidence => EvidenceLines.Count > 0;

    public bool HasHypotheses => Hypotheses.Count > 0;

    public bool HasMissingEvidence => MissingEvidence.Count > 0;

    public bool HasSuggestedActions => SuggestedActions.Count > 0;

    public bool HasExplanation => !string.IsNullOrWhiteSpace(Explanation);
}

/// <summary>一条世界定位：定位摘要与“是否允许地图标记”的事实说明（不把推断维度画成确定区块）。</summary>
public sealed record DiagnosticWorldTargetRow(string Summary, string MapNote, bool CanMap);

/// <summary>一条实例诊断历史：生成时间、结论数、总体置信度与知识库版本。</summary>
public sealed record DiagnosticHistoryRow(
    string GeneratedAt,
    int FindingCount,
    string ConfidenceText,
    string KnowledgeVersion,
    string Summary)
{
    public string TimeText => GeneratedAt;

    public string FindingCountText => FindingCount.ToString();

    public string KnowledgeVersionText => KnowledgeVersion;
}
