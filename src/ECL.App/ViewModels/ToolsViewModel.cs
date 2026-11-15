// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.Additions;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Ecl.App.ViewModels;

/// <summary>
/// 工具中心（迁移规划 §4.2 / §6.2 #12）：<c>tools</c> 目的地不再是一个大型多 Tab 容器，
/// 而是工具入口清单——名称、用途、真实可用性与打开入口。
///
/// 本批按维护者 2026-11-15 指示只完成页面模板与布局：
/// 原生工具条目来自 <see cref="IToolsCenterCatalog"/>，扩展条目来自本机真实安装记录；
/// 未发布的扩展、尚未补齐的 G-02/G-03/G-10 工具以及 Appearance Enhance 都不制造占位入口。
/// 新增 Extension 发布并安装后会出现在扩展区域，不需要改动本页面。
/// </summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    private readonly IAdditionsService _additions;
    private readonly ILocalizer _localizer;
    private readonly ILogger<ToolsViewModel> _logger;

    public ToolsViewModel(
        IAdditionsService additions,
        IToolsCenterCatalog catalog,
        ILocalizer localizer,
        ILogger<ToolsViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(localizer);

        _additions = additions;
        _localizer = localizer;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolsViewModel>.Instance;
        Tools = catalog.GetTools();
        AdditionsStatus = _localizer.Get("tools.additions.status.loading");
    }

    /// <summary>打开设置中心「扩展管理」的入口；由 Shell 注入，页面不自行导航。</summary>
    public Action? OpenAdditionsManagement { get; set; }

    /// <summary>本机可用的 ECL 原生工具；由实现处登记一次，页面不硬编码清单。</summary>
    public IReadOnlyList<ToolsCenterTool> Tools { get; }

    /// <summary>本机真实安装的扩展记录；没有安装、状态损坏或读取失败都如实呈现。</summary>
    public ObservableCollection<AdditionToolRow> InstalledAdditions { get; } = [];

    [ObservableProperty]
    private ToolsCenterTool? _selectedTool;

    [ObservableProperty]
    private string _additionsStatus;

    [ObservableProperty]
    private bool _hasAdditionsError;

    [ObservableProperty]
    private string _additionsErrorText = string.Empty;

    public bool HasSelectedTool => SelectedTool is not null;

    public object? SelectedToolContent => SelectedTool?.Content;

    public bool HasInstalledAdditions => InstalledAdditions.Count > 0;

    /// <summary>既没有安装记录、也没有读取错误时才显示空态，不把错误伪装成“没有扩展”。</summary>
    public bool ShowAdditionsEmpty => !HasAdditionsError && !HasInstalledAdditions;

    /// <summary>
    /// 进入工具中心时的有限读取：只读本机安装记录，不请求官方目录、不发起网络。
    /// 重复进入会重新读取一次，安装/移除扩展后无需重启即可看到真实状态。
    /// </summary>
    public Task ActivateAsync()
    {
        LoadInstalledAdditions();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task OpenToolAsync(ToolsCenterTool? tool)
    {
        if (tool is null)
        {
            return;
        }

        SelectedTool = tool;
        try
        {
            await tool.ActivateAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // 工具自身的加载失败不应把用户困在空白页：保留工具入口并记录原因，
            // 工具内容仍负责显示自己的真实状态。
            _logger.LogWarning(exception, "工具激活失败：{Tool}", tool.Key);
        }
    }

    [RelayCommand]
    private void BackToTools() => SelectedTool = null;

    [RelayCommand]
    private void ManageAdditions() => OpenAdditionsManagement?.Invoke();

    [RelayCommand]
    private void RefreshAdditions() => LoadInstalledAdditions();

    partial void OnSelectedToolChanged(ToolsCenterTool? value)
    {
        OnPropertyChanged(nameof(HasSelectedTool));
        OnPropertyChanged(nameof(SelectedToolContent));
    }

    private void LoadInstalledAdditions()
    {
        InstalledAdditions.Clear();
        HasAdditionsError = false;
        AdditionsErrorText = string.Empty;

        AdditionStateLoad load;
        try
        {
            load = _additions.LoadState();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "读取扩展安装记录失败。");
            ApplyAdditionsError(_localizer.Get("tools.additions.error.read", exception.Message));
            return;
        }

        if (load.IsCorrupt)
        {
            // 状态文件损坏时如实报告，不猜测已安装列表（ADR 0032 §7）。
            var detail = string.IsNullOrWhiteSpace(load.Error)
                ? string.Empty
                : load.Error;
            AdditionsStatus = _localizer.Get("tools.additions.status.corrupt");
            ApplyAdditionsError(_localizer.Get("tools.additions.error.corrupt", detail));
            return;
        }

        foreach (var addition in load.Document.Additions.OrderBy(
                     addition => addition.Id.Value,
                     StringComparer.Ordinal))
        {
            InstalledAdditions.Add(CreateRow(addition));
        }

        AdditionsStatus = InstalledAdditions.Count == 0
            ? _localizer.Get("tools.additions.status.none")
            : _localizer.Get("tools.additions.status.count", InstalledAdditions.Count);
        OnPropertyChanged(nameof(HasInstalledAdditions));
        OnPropertyChanged(nameof(ShowAdditionsEmpty));
    }

    private void ApplyAdditionsError(string message)
    {
        HasAdditionsError = true;
        AdditionsErrorText = message;
        if (string.IsNullOrEmpty(AdditionsStatus))
        {
            AdditionsStatus = _localizer.Get("tools.additions.status.corrupt");
        }

        OnPropertyChanged(nameof(HasInstalledAdditions));
        OnPropertyChanged(nameof(ShowAdditionsEmpty));
    }

    private AdditionToolRow CreateRow(InstalledAddition addition)
    {
        var typeText = addition.Type == AdditionType.Data
            ? _localizer.Get("tools.additions.type.data")
            : _localizer.Get("tools.additions.type.feature");
        var statusText = addition.Enabled
            ? _localizer.Get("tools.additions.state.enabled")
            : _localizer.Get("tools.additions.state.disabled");
        return new AdditionToolRow(
            addition.Id.Value,
            addition.Version.ToString(),
            typeText,
            statusText,
            _localizer.Get("tools.additions.runtimeUnavailable"));
    }
}

/// <summary>扩展区域的一行：只陈述安装记录里的真实字段，不补造名称、用途或能力。</summary>
public sealed record AdditionToolRow(
    string Id,
    string VersionText,
    string TypeText,
    string StatusText,
    string Note);
