// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Java;

namespace Ecl.App.ViewModels;

/// <summary>
/// Java 选择结果的呈现规则唯一实现（B2 步骤 1c）。
///
/// 主页的启动面板与每个实例工作区都会显示“当前实例使用哪个 Java、是否兼容”，
/// 两者必须给出同一套文案与判断顺序；把它们留在各自的 ViewModel 里会形成两套规则。
/// 本类只负责措辞与顺序，不改变领域层的兼容性结论。
/// </summary>
public static class JavaSelectionPresentation
{
    /// <summary>把一次 Java 选择结果描述为页面文案；警告文本统一追加在句末。</summary>
    public static string Describe(JavaSelectionResult selection, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(localizer);

        var text = DescribeCore(selection, localizer);
        return selection.Warnings.Count == 0
            ? text
            : $"{text} {string.Join(' ', selection.Warnings)}";
    }

    public static string DescribeRejectedCandidates(JavaSelectionDecision? decision, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        if (decision is null || decision.RejectedCandidates.Count == 0)
        {
            return localizer.Get("instances.java.notFound");
        }

        return string.Join(
            "，",
            decision.RejectedCandidates
                .GroupBy(candidate => candidate.Status)
                .Select(group => $"{DescribeCandidateStatus(group.Key, localizer)} ×{group.Count()}"));
    }

    /// <summary>Java 候选运行时的显示名：版本 · 发行版 · 目录名。</summary>
    public static string FormatRuntime(JavaRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var version = runtime.Version?.Raw ?? "?";
        var vendor = runtime.Vendor ?? "Java";
        var folder = Path.GetFileName(runtime.HomePath);
        return $"{version} · {vendor} · {folder}";
    }

    private static string DescribeCore(JavaSelectionResult selection, ILocalizer localizer)
    {
        if (selection.IsBlocked)
        {
            var blockedMessage = selection.Compatibility?.Status switch
            {
                JavaCompatibilityStatus.UnsupportedArchitecture => localizer.Get(
                    "instances.java.unsupported32",
                    selection.Runtime?.DisplayName ?? "Java"),
                JavaCompatibilityStatus.RequirementsConflict => localizer.Get(
                    "instances.java.conflict",
                    selection.Decision?.Requirements?.Explain() ?? string.Empty),
                _ => selection.BlockReason ?? localizer.Get("instances.java.blocked"),
            };
            return blockedMessage;
        }

        if (selection.Runtime is null)
        {
            return selection.Decision?.Failure switch
            {
                JavaSelectionFailure.RequirementsConflict => localizer.Get(
                    "instances.java.conflict",
                    selection.Decision.Requirements?.Explain() ?? string.Empty),
                JavaSelectionFailure.NoCompatibleCandidate => localizer.Get(
                    "instances.java.noCompatibleReasons",
                    selection.Requirement?.Describe() ?? "Java",
                    DescribeRejectedCandidates(selection.Decision, localizer)),
                _ => localizer.Get("instances.java.notFound"),
            };
        }

        if (selection.Compatibility is null || selection.Compatibility.IsCompatible)
        {
            return localizer.Get("instances.java.compatible", selection.Runtime.DisplayName);
        }

        return selection.Compatibility.Status switch
        {
            JavaCompatibilityStatus.Unknown => localizer.Get(
                "instances.java.unknownVersion",
                selection.Runtime.DisplayName),
            JavaCompatibilityStatus.UnknownRequirement => localizer.Get(
                "instances.java.unknownRequirement",
                selection.Runtime.DisplayName),
            JavaCompatibilityStatus.UnsupportedArchitecture => localizer.Get(
                "instances.java.unsupported32",
                selection.Runtime.DisplayName),
            _ => localizer.Get(
                selection.IsCompatibilityWarningAcknowledged
                    ? "instances.java.incompatibleAcknowledged"
                    : "instances.java.incompatible",
                selection.Runtime.Version?.Raw ?? selection.Runtime.DisplayName,
                selection.Requirement?.Describe() ?? "Java"),
        };
    }

    private static string DescribeCandidateStatus(JavaCandidateStatus status, ILocalizer localizer) =>
        localizer.Get(status switch
        {
            JavaCandidateStatus.TooOld => "instances.java.candidate.tooOld",
            JavaCandidateStatus.TooNew => "instances.java.candidate.tooNew",
            JavaCandidateStatus.Unsupported32Bit => "instances.java.candidate.unsupported32Bit",
            JavaCandidateStatus.UnknownVersion => "instances.java.candidate.unknownVersion",
            JavaCandidateStatus.CompatibleButNotPreferred => "instances.java.candidate.compatibleButNotPreferred",
            JavaCandidateStatus.RequirementsConflict => "instances.java.candidate.requirementsConflict",
            _ => "instances.java.candidate.unknownVersion",
        });
}
