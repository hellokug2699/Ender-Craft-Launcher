// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;

namespace Ecl.App.ViewModels;

/// <summary>
/// 工具中心的一项工具入口（ADR 0050 §3、迁移规划 §6.2 #12：工具入口与工具内容分离）。
/// 条目只描述工具身份、用途、真实可用状态与打开动作；工具界面由 <see cref="Content"/> 指向的
/// 页面自有对象承载，工具中心不复制领域逻辑，也不为未接入的工具制造空入口。
/// </summary>
/// <remarks>
/// G-04（工具中心清单）与 G-10（实例引擎独立工具工作区）页面规格尚未补齐，因此本批只建立
/// 入口清单元数据与布局：工具内容仍在工具中心内以“打开 / 返回”方式呈现，不新造工作区身份。
/// 规格完成后把内容迁移到独立工具工作区时，条目契约保持不变。
/// </remarks>
public sealed class ToolsCenterTool
{
    private readonly Func<Task>? _activateAsync;

    public ToolsCenterTool(
        string key,
        string title,
        string description,
        string statusText,
        string? note,
        object content,
        Func<Task>? activateAsync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);

        Key = key;
        Title = title;
        Description = description;
        StatusText = statusText;
        Note = note;
        Content = content;
        _activateAsync = activateAsync;
    }

    public string Key { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>当前真实可用状态；只陈述已核实的能力，不表示“已实现”之外的承诺。</summary>
    public string StatusText { get; }

    /// <summary>限制、承载位置或后续迁移说明；没有时保持 null，页面不显示空行。</summary>
    public string? Note { get; }

    /// <summary>工具界面的数据上下文；由工具 Entry 自己持有生命周期与订阅。</summary>
    public object Content { get; }

    public Task ActivateAsync() => _activateAsync?.Invoke() ?? Task.CompletedTask;
}

/// <summary>工具中心入口来源。新增工具（含未来由 Addition 提供的工具）只在实现处登记一次。</summary>
public interface IToolsCenterCatalog
{
    IReadOnlyList<ToolsCenterTool> GetTools();
}

/// <summary>
/// ECL 原生工具入口。本批只登记已实现且此前由旧「工具箱」承载的能力：
/// 实例引擎工具（Inventory / 快照 / 安全体检）与本地诊断。
/// </summary>
/// <remarks>
/// 旧工具箱的「知识浏览」「世界工具」占位卡片按迁移规划 §6.2 #12 下线；
/// 扩展、原版工具与 Appearance Enhance 不在此登记——它们由 Extension 状态与官方目录呈现，
/// 未发布时显示 Planned/不可用，不伪装为可用工具。
/// </remarks>
public sealed class NativeToolsCenterCatalog(
    EngineToolsViewModel engine,
    DiagnosticsViewModel diagnostics,
    ILocalizer localizer) : IToolsCenterCatalog
{
    public IReadOnlyList<ToolsCenterTool> GetTools() =>
    [
        new ToolsCenterTool(
            "engine-tools",
            localizer.Get("tools.entry.engine.title"),
            localizer.Get("tools.entry.engine.description"),
            localizer.Get("tools.entry.status.available"),
            localizer.Get("tools.entry.engine.note"),
            engine,
            engine.ActivateAsync),
        new ToolsCenterTool(
            "diagnostics",
            localizer.Get("tools.entry.diagnostics.title"),
            localizer.Get("tools.entry.diagnostics.description"),
            localizer.Get("tools.entry.status.available"),
            localizer.Get("tools.entry.diagnostics.note"),
            diagnostics,
            diagnostics.ActivateAsync),
    ];
}
