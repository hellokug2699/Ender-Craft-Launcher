// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Additions;
using Ecl.Core.Localization;

namespace Ecl.App.ViewModels.Settings;

/// <summary>
/// 扩展管理页的行状态。只包含当前实现能准确判断的状态（settings-additions.md §3）：
/// 启用/禁用来自安装记录，依赖缺失来自安装记录之间的必需依赖，不兼容来自记录的 Core API 范围。
/// 危险等级、发布者可信度、逐包可渲染性等没有数据源的状态不在这里伪造。
/// </summary>
public enum AdditionRowState
{
    Enabled,
    Disabled,
    MissingDependency,
    Incompatible,
}

/// <summary>状态筛选选项；只有页面能准确判断的状态才出现在列表里。</summary>
public sealed record AdditionStatusFilter(string Value, string DisplayName)
{
    /// <summary>下拉条目只应暴露本地化显示名，不暴露 record 的默认 ToString。</summary>
    public override string ToString() => DisplayName;
}

public static class AdditionStatusFilterValues
{
    public const string All = "all";
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string MissingDependency = "missing-dependency";
    public const string Incompatible = "incompatible";
    public const string UpdateAvailable = "update-available";
}

/// <summary>
/// 一个已安装扩展的只读投影。名称与用途只在官方目录提供条目时才有来源；
/// 没有目录时只显示扩展 ID，不编造显示名称。
/// </summary>
public sealed class InstalledAdditionRowViewModel(
    InstalledAddition record,
    AdditionRowState state,
    string statusPrimaryText,
    string statusSecondaryText,
    string name,
    string description,
    string typeText,
    string versionText,
    string installedAtText,
    string dependencyText,
    string compatibilityText,
    string updateText,
    string previousVersionText,
    string toggleText,
    string enableBlockedReason,
    string removeBlockedReason,
    bool isReadOnly = false)
{
    public AdditionId Id { get; } = record.Id;

    public string IdText => Id.Value;

    public InstalledAddition Record { get; } = record;

    public AdditionRowState State { get; } = state;

    /// <summary>状态列第一行：已启用 / 已禁用。</summary>
    public string StatusPrimaryText { get; } = statusPrimaryText;

    /// <summary>状态列第二行：依赖缺失 / 不兼容；没有阻塞时为空，行高仍保持一致。</summary>
    public string StatusSecondaryText { get; } = statusSecondaryText;

    /// <summary>完整状态文本，用于 UIA/读屏与 ToolTip。</summary>
    public string StatusText { get; } = string.IsNullOrEmpty(statusSecondaryText)
        ? statusPrimaryText
        : $"{statusPrimaryText} · {statusSecondaryText}";

    public string Name { get; } = name;

    public string Description { get; } = description;

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    /// <summary>
    /// 名称列固定三行：名称、扩展 ID、一行说明。说明优先显示阻塞原因（依赖缺失/Core API），
    /// 其次才是目录提供的用途说明；三者只显示一行，保证每行等高且表格不被撑开。
    /// </summary>
    public bool ShowDescription =>
        HasDescription && !HasDependencyText && !HasCompatibilityText;

    public string TypeText { get; } = typeText;

    public string VersionText { get; } = versionText;

    /// <summary>版本列首行：版本 + 类型。窄窗口下类型并入版本列，避免挤掉可读的名称列。</summary>
    public string VersionTypeText => $"{VersionText} · {TypeText}";

    public string InstalledAtText { get; } = installedAtText;

    /// <summary>缺失的必需依赖（已本地化文本）；没有缺失时为空。</summary>
    public string DependencyText { get; } = dependencyText;

    public bool HasDependencyText => !string.IsNullOrEmpty(DependencyText);

    /// <summary>Core API 不兼容时的真实范围说明；兼容或未声明范围时为空。</summary>
    public string CompatibilityText { get; } = compatibilityText;

    public bool HasCompatibilityText => !string.IsNullOrEmpty(CompatibilityText);

    public string UpdateText { get; } = updateText;

    public bool HasUpdate => !string.IsNullOrEmpty(UpdateText);

    public string PreviousVersionText { get; } = previousVersionText;

    public string ToggleText { get; } = toggleText;

    public bool IsEnabled { get; } = record.Enabled;

    /// <summary>“启用/禁用”是否可执行：状态文件损坏时整页只读；否则禁用永远可用，启用需要依赖与 Core API 都满足。</summary>
    public bool CanToggle { get; } = !isReadOnly && (record.Enabled || string.IsNullOrEmpty(enableBlockedReason));

    public bool CanUpdate { get; } = !isReadOnly && !string.IsNullOrEmpty(updateText);

    public bool CanRollback { get; } = !isReadOnly && record.PreviousVersion is not null;

    public bool CanRemove { get; } = !isReadOnly && string.IsNullOrEmpty(removeBlockedReason);

    public bool IsReadOnly { get; } = isReadOnly;

    public string EnableBlockedReason { get; } = enableBlockedReason;

    public string RemoveBlockedReason { get; } = removeBlockedReason;

    /// <summary>UIA/读屏名称：扩展 ID 是稳定身份，显示名称只在目录提供时才有。</summary>
    public string AutomationDescription =>
        $"{IdText} {VersionText} {StatusText}";
}

/// <summary>官方目录中一条可安装（或可更新）的扩展；内容全部来自通过签名验证的目录。</summary>
public sealed class AvailableAdditionRowViewModel(
    AdditionEntry entry,
    string versionText,
    string sizeText,
    string dependenciesText,
    string compatibilityText,
    string platformsText)
{
    public AdditionEntry Entry { get; } = entry;

    public AdditionId Id => Entry.Id;

    public string IdText => Id.Value;

    public string Name => string.IsNullOrWhiteSpace(Entry.Name) ? Entry.Id.Value : Entry.Name;

    public string Description => Entry.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string VersionText { get; } = versionText;

    public string SizeText { get; } = sizeText;

    public string DependenciesText { get; } = dependenciesText;

    public string CompatibilityText { get; } = compatibilityText;

    public string PlatformsText { get; } = platformsText;
}

/// <summary>安装计划中的一项（安装或更新）；字段全部来自目录条目，不额外补默认值。</summary>
public sealed record AdditionPlanItemViewModel(
    string KindText,
    string Title,
    string VersionText,
    string SizeText,
    string DependenciesText,
    string CompatibilityText)
{
    /// <summary>UIA/读屏只显示标题，不暴露 record 的默认 ToString。</summary>
    public override string ToString() => Title;
}

/// <summary>安装计划预览（settings-additions.md §4 的“查看安装计划”阶段）。</summary>
public sealed class AdditionPlanViewModel(
    IReadOnlyList<AdditionPlanItemViewModel> items,
    IReadOnlyList<string> warnings,
    string summaryText,
    string restartNote)
{
    public IReadOnlyList<AdditionPlanItemViewModel> Items { get; } = items;

    public IReadOnlyList<string> Warnings { get; } = warnings;

    public string SummaryText { get; } = summaryText;

    /// <summary>安装后的生效方式说明（扩展按需加载，不需要重启 ECL）。</summary>
    public string RestartNote { get; } = restartNote;

    public bool HasItems => Items.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>扩展管理页的本地化文本入口；页面与行模型共用，避免两套措辞。</summary>
internal sealed class AdditionPageText(ILocalizer localizer)
{
    public ILocalizer Localizer { get; } = localizer;

    public string Get(string key) => Localizer.Get($"settings.page.additions.{key}");

    public string Get(string key, params object?[] arguments) =>
        Localizer.Get($"settings.page.additions.{key}", arguments);
}
