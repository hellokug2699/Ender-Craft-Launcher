// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Java;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// Java 方块的兼容性状态（`instance-workspace.md` §2.3）：
/// 紫/✓ 兼容、红/X 不兼容、黄/⚠ 不兼容但已按实例偏好忽略常规警告、未知状态不得伪装成兼容。
/// </summary>
public enum JavaCompatibilityBadge
{
    Unknown,
    Compatible,
    Incompatible,
    IncompatibleAcknowledged,
}

/// <summary>Java 方块要显示的两行文本、状态与状态说明。</summary>
public sealed record JavaTilePresentation(
    string Title,
    string Detail,
    JavaCompatibilityBadge Badge,
    string StateText)
{
    public static JavaTilePresentation Unknown(ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return new JavaTilePresentation(
            localizer.Get("instance.java.tile.none"),
            localizer.Get("instance.java.tile.noneDetail"),
            JavaCompatibilityBadge.Unknown,
            localizer.Get("instance.java.tile.unknown"));
    }
}

/// <summary>
/// 把一次 Java 选择结果映射为 Java 方块呈现（B2 步骤 3）。
///
/// 只读取领域层的真实兼容性结论；「忽略警告」只改变方块的呈现（黄/⚠），
/// 不改变兼容性结论，也不影响启动准入（§14.5 / ADR 0014）。
/// </summary>
public static class JavaStatusPresentation
{
    public static JavaTilePresentation Describe(
        JavaSelectionResult selection,
        bool suppressCompatibilityWarnings,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(localizer);

        if (selection.Runtime is not { } runtime)
        {
            return JavaTilePresentation.Unknown(localizer);
        }

        var title = runtime.Version is { } version
            ? localizer.Get("instance.java.tile.title", version.Major)
            : runtime.DisplayName;
        var detail = runtime.Version is { } fullVersion
            ? $"{runtime.Vendor ?? "Java"} {fullVersion.Raw}"
            : runtime.DisplayName;
        var requirement = selection.Requirement?.Describe();

        if (selection.Compatibility is not { } compatibility)
        {
            return new JavaTilePresentation(
                title,
                detail,
                JavaCompatibilityBadge.Unknown,
                localizer.Get("instance.java.tile.unknown"));
        }

        var status = compatibility.Status;
        if (status is JavaCompatibilityStatus.Unknown or JavaCompatibilityStatus.UnknownRequirement)
        {
            return new JavaTilePresentation(
                title,
                detail,
                JavaCompatibilityBadge.Unknown,
                localizer.Get("instance.java.tile.unknown"));
        }

        if (selection.IsBlocked || status != JavaCompatibilityStatus.Compatible)
        {
            var badge = suppressCompatibilityWarnings
                ? JavaCompatibilityBadge.IncompatibleAcknowledged
                : JavaCompatibilityBadge.Incompatible;

            return new JavaTilePresentation(
                title,
                detail,
                badge,
                DescribeIncompatible(badge, requirement, localizer));
        }

        return new JavaTilePresentation(
            title,
            detail,
            JavaCompatibilityBadge.Compatible,
            localizer.Get("instance.java.tile.compatible"));
    }

    private static string DescribeIncompatible(
        JavaCompatibilityBadge badge,
        string? requirement,
        ILocalizer localizer)
    {
        if (string.IsNullOrWhiteSpace(requirement))
        {
            return localizer.Get(badge == JavaCompatibilityBadge.IncompatibleAcknowledged
                ? "instance.java.tile.acknowledgedNoRequirement"
                : "instance.java.tile.incompatibleNoRequirement");
        }

        return localizer.Get(
            badge == JavaCompatibilityBadge.IncompatibleAcknowledged
                ? "instance.java.tile.acknowledged"
                : "instance.java.tile.incompatible",
            requirement);
    }
}
