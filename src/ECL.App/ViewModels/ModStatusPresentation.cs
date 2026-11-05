// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Minecraft.Mods;

namespace Ecl.App.ViewModels;

/// <summary>
/// 模组表格的七个状态分类（`hints/ui/pages/mods.md` §6）。
/// 同一模组可以同时具有多个分类；分类之间没有“最高优先级”覆盖关系。
/// </summary>
public enum ModStatusKind
{
    Available,
    PossiblyAbnormal,
    Unknown,
    Dangerous,
    Conflict,
    MissingDependency,
    Disabled,
}

/// <summary>状态筛选（mods.md §3.2）；<c>null</c> 表示不筛选，每次只能选中一个。</summary>
public enum ModStatusFilter
{
    Available,
    Disabled,
    Dangerous,
    Abnormal,
    Updatable,
}

/// <summary>排序方式（mods.md §3.1）；默认字母正序。</summary>
public enum ModSortOrder
{
    NameAscending,
    NameDescending,
    TimeAscending,
    TimeDescending,
}

/// <summary>表格状态列中的一个图标；<see cref="Description"/> 供 Tooltip 与 UIA 名称使用。</summary>
public sealed record ModStatusBadge(ModStatusKind Kind, string Description);

/// <summary>筛选条目；能力缺口（危险/可更新）通过 <see cref="CapabilityHint"/> 如实说明。</summary>
public sealed record ModStatusFilterChoice(ModStatusFilter Filter, string Title, string? CapabilityHint = null);

public sealed record ModSortChoice(ModSortOrder Order, string Title);

/// <summary>
/// 把领域问题映射为页面状态分类（mods.md §6.1/§6.2）。
///
/// 映射规则（页面规格未定义判定算法，这里只做领域结果到状态分类的呈现）：
/// - 危险：ADR 0043 的威胁判定尚未接入模组页，当前没有数据源会产生命中；
///   枚举与渲染保留，但不得凭猜测伪造。
/// - 可能异常或版本不符合：重复 Mod ID、加载器不匹配。
/// - 未知模组：无法读取任何模组信息（没有元数据）。
/// - 冲突：模组声明的 breaks 与已启用模组重叠。
/// - 缺少依赖：缺少必需依赖，或必需依赖已存在但被禁用。
/// - 禁用：文件处于 <c>.disabled</c> 状态。
/// - 可用：已启用、元数据完整且没有任何问题分类。
///
/// 同一分类下的多个问题合并为一个图标并在 Tooltip 中逐条说明，因此最多同时出现四个分类。
/// </summary>
public static class ModStatusPresentation
{
    public const int MaxBadges = 4;

    public static IReadOnlyList<ModStatusBadge> Create(ModEntry entry, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localizer);

        var issues = entry.Issues;
        var badges = new List<ModStatusBadge>(MaxBadges);

        var abnormal = issues
            .Where(issue => issue.Kind is ModIssueKind.DuplicateModId or ModIssueKind.LoaderMismatch)
            .Select(issue => Describe(issue, localizer))
            .ToArray();
        if (abnormal.Length > 0)
        {
            badges.Add(new ModStatusBadge(
                ModStatusKind.PossiblyAbnormal,
                Compose(localizer.Get("mods.status.possiblyAbnormal"), abnormal)));
        }

        if (entry.Metadata is null)
        {
            badges.Add(new ModStatusBadge(
                ModStatusKind.Unknown,
                Compose(
                    localizer.Get("mods.status.unknown"),
                    string.IsNullOrWhiteSpace(entry.Error) ? [] : [entry.Error])));
        }

        var conflicts = issues
            .Where(issue => issue.Kind == ModIssueKind.Conflict)
            .Select(issue => Describe(issue, localizer))
            .ToArray();
        if (conflicts.Length > 0)
        {
            badges.Add(new ModStatusBadge(
                ModStatusKind.Conflict,
                Compose(localizer.Get("mods.status.conflict"), conflicts)));
        }

        var missing = issues
            .Where(issue => issue.Kind is ModIssueKind.MissingDependency or ModIssueKind.DisabledDependency)
            .Select(issue => Describe(issue, localizer))
            .ToArray();
        if (missing.Length > 0)
        {
            badges.Add(new ModStatusBadge(
                ModStatusKind.MissingDependency,
                Compose(localizer.Get("mods.status.missingDependency"), missing)));
        }

        if (!entry.Enabled)
        {
            badges.Add(new ModStatusBadge(
                ModStatusKind.Disabled,
                localizer.Get("mods.status.disabled")));
        }

        if (badges.Count == 0 && entry.Enabled)
        {
            badges.Add(new ModStatusBadge(
                ModStatusKind.Available,
                localizer.Get("mods.status.available")));
        }

        return badges;
    }

    /// <summary>把一条领域问题翻译成可读文本；与 Tooltip 共用，避免出现第二套描述。</summary>
    public static string Describe(ModIssue issue, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(localizer);

        return issue.Kind switch
        {
            ModIssueKind.DuplicateModId => localizer.Get("mod.issue.duplicate", issue.ModId ?? "?", issue.RelatedModId ?? "?"),
            ModIssueKind.MissingDependency => localizer.Get("mod.issue.missingDependency", issue.ModId ?? "?", issue.RelatedModId ?? "?"),
            ModIssueKind.DisabledDependency => localizer.Get("mod.issue.disabledDependency", issue.ModId ?? "?", issue.RelatedModId ?? "?"),
            ModIssueKind.Conflict => localizer.Get("mod.issue.conflict", issue.ModId ?? "?", issue.RelatedModId ?? "?"),
            ModIssueKind.LoaderMismatch => localizer.Get("mod.issue.loaderMismatch", issue.ModId ?? "?", issue.Detail ?? "?"),
            ModIssueKind.InvalidMetadata => localizer.Get("mod.issue.invalidMetadata", issue.FileName, issue.Detail ?? "?"),
            _ => issue.Kind.ToString(),
        };
    }

    private static string Compose(string title, IReadOnlyList<string> details) =>
        details.Count == 0 ? title : $"{title}{Environment.NewLine}{string.Join(Environment.NewLine, details)}";
}
