// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Core;
using Ecl.Diagnostics;
using Ecl.Minecraft.Instances;

namespace Ecl.App.ViewModels;

/// <summary>
/// 本地诊断工具（旧「工具箱」的「诊断」页内容）。只读取实例 <c>latest.log</c> 与
/// <c>crash-reports</c>，默认不上传日志、存档或服务器地址；未安装诊断知识库时只做本地结构化解析，
/// 不输出猜测性根因。
/// </summary>
/// <remarks>
/// 承载位置仍待迁移规划 §6.2 #12 的「诊断 → 日志/诊断页面（G-01）」。G-01 已由 Approved
/// <c>pages/logs.md</c> 解除，但把本地诊断分析并入 Logs Workspace 属于 B6 之后的独立工作，
/// 本批不复制第二套日志或证据界面，也不为此新增占位入口。
/// </remarks>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IInstanceService _instances;
    private readonly IAppPaths _paths;
    private readonly DiagnosticEngine _engine = new();
    private bool _activated;

    public DiagnosticsViewModel(IInstanceService instances, IAppPaths paths)
    {
        _instances = instances;
        _paths = paths;
    }

    public ObservableCollection<InstanceChoice> Instances { get; } = [];

    [ObservableProperty]
    private InstanceChoice? _selectedInstance;

    [ObservableProperty]
    private string _status = "选择实例后分析本地 latest.log 与崩溃报告。";

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _uncertainty = string.Empty;

    [ObservableProperty]
    private string _knowledgeNote = "未安装诊断知识库（Data Addition）：当前只做本地日志/崩溃结构化解析，不进行规则匹配，也不会给出确定根因。";

    [ObservableProperty]
    private bool _isBusy;

    public async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        var instances = await _instances.GetInstancesAsync().ConfigureAwait(true);
        Instances.Clear();
        foreach (var instance in instances)
        {
            Instances.Add(new InstanceChoice(instance.Id, $"{instance.Name} · {instance.GameVersion}"));
        }

        SelectedInstance = Instances.FirstOrDefault();
        Status = Instances.Count == 0 ? "尚未创建实例。" : "选择实例后开始分析。";
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var instance = await _instances.GetAsync(SelectedInstance.Id).ConfigureAwait(true);
            if (instance is null)
            {
                Status = "实例不存在。";
                return;
            }

            var gameDirectory = InstancePaths.GetGameDirectory(_paths, instance);
            var logFiles = new List<string>();
            var latest = Path.Combine(gameDirectory, "logs", "latest.log");
            if (File.Exists(latest))
            {
                logFiles.Add(latest);
            }

            var crashFiles = new List<string>();
            var crashDirectory = Path.Combine(gameDirectory, "crash-reports");
            if (Directory.Exists(crashDirectory))
            {
                crashFiles.AddRange(Directory.EnumerateFiles(crashDirectory, "*.txt").Take(5));
            }

            var input = new DiagnosticInputLoader().Load(logFiles, crashFiles);
            var report = _engine.Analyze(new DiagnosticAnalysisRequest
            {
                Events = input.Events,
                CrashReports = input.CrashReports,
                InputIssues = input.Issues,
                Environment = new DiagnosticEnvironment
                {
                    MinecraftVersion = instance.GameVersion,
                    Loader = instance.Loader,
                    LoaderVersion = instance.LoaderVersion,
                },
            });

            Summary = $"事件 {input.Events.Count} 条 · 崩溃报告 {input.CrashReports.Count} 份 · 结论 {report.Findings.Count} 条 · 置信度 {report.OverallConfidence}";
            Uncertainty = report.UncertaintyNote ?? string.Empty;
            Status = report.Findings.Count == 0
                ? "已完成本地解析；没有安装知识库，因此不输出猜测性根因。"
                : "分析完成。请结合证据链逐条核对。";
            KnowledgeNote = report.KnowledgeBase is null
                ? "未安装诊断知识库（Data Addition）：当前只做本地日志/崩溃结构化解析，不进行规则匹配。"
                : $"知识库 {report.KnowledgeVersion} 已参与分析。";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
