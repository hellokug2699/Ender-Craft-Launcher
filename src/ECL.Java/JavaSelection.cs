// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

public enum JavaSelectionReason
{
    None,
    ExplicitOverride,
    GlobalPriority,
    DiscoveryDefault,
}

public enum JavaSelectionFailure
{
    None,
    NoCandidates,
    NoCompatibleCandidate,
    RequirementsConflict,
    ExplicitRuntimeUnsupported,
}

/// <summary>一个候选运行时的评估结果。被拒绝的候选也要保留原因，供“为什么是这个 Java”使用。</summary>
public enum JavaCandidateStatus
{
    Selected,
    CompatibleButNotPreferred,
    TooOld,
    TooNew,
    Unsupported32Bit,
    UnknownVersion,
    RequirementsConflict,
}

public sealed record JavaCandidateEvaluation(
    JavaRuntime Runtime,
    JavaCandidateStatus Status,
    string Detail)
{
    public bool IsSelected => Status == JavaCandidateStatus.Selected;
}

/// <summary>
/// Core 的 Java 选择结果。它同时描述选中项、被拒候选、失败类别和解释，
/// WPF/CLI 只渲染，不再各自实现选择规则。
/// </summary>
public sealed record JavaSelectionDecision(
    JavaRuntime? Runtime,
    JavaSelectionReason Reason,
    JavaSelectionFailure Failure,
    EffectiveJavaRequirements? Requirements,
    IReadOnlyList<JavaCandidateEvaluation> Candidates,
    bool IsExplicitOverride,
    bool RequiresRiskAcknowledgement,
    bool IsBlocked,
    string? RiskIdentity,
    string Explanation)
{
    public bool HasRuntime => Runtime is not null;

    public IReadOnlyList<JavaCandidateEvaluation> RejectedCandidates =>
        Candidates.Where(candidate => !candidate.IsSelected).ToArray();
}

public static class JavaSelectionPlanner
{
    /// <summary>
    /// 按全局优先级选择 Java。
    /// <paramref name="priorityCandidates"/> 必须已经按“配置优先级组 + 发现默认顺序”排好序；
    /// <paramref name="configuredPriorityCount"/> 表示前多少项来自用户配置的优先级列表。
    /// </summary>
    public static JavaSelectionDecision Select(
        IReadOnlyList<JavaRuntime> priorityCandidates,
        EffectiveJavaRequirements? requirements,
        JavaRuntime? explicitRuntime = null,
        int configuredPriorityCount = 0)
    {
        var effective = requirements ?? EffectiveJavaRequirements.Empty;
        var candidates = new List<JavaCandidateEvaluation>();

        if (effective.HasConflicts)
        {
            foreach (var runtime in priorityCandidates)
            {
                candidates.Add(new JavaCandidateEvaluation(
                    runtime,
                    JavaCandidateStatus.RequirementsConflict,
                    "有效 Java 要求互相冲突，无法选择运行时。"));
            }

            if (explicitRuntime is not null && candidates.All(candidate =>
                    !ReferenceEquals(candidate.Runtime, explicitRuntime)))
            {
                candidates.Add(new JavaCandidateEvaluation(
                    explicitRuntime,
                    JavaCandidateStatus.RequirementsConflict,
                    "有效 Java 要求互相冲突，无法验证显式覆盖。"));
            }

            return new JavaSelectionDecision(
                null,
                explicitRuntime is null ? JavaSelectionReason.None : JavaSelectionReason.ExplicitOverride,
                JavaSelectionFailure.RequirementsConflict,
                effective,
                candidates,
                IsExplicitOverride: explicitRuntime is not null,
                RequiresRiskAcknowledgement: false,
                IsBlocked: true,
                RiskIdentity: null,
                Explanation: $"Java 要求冲突：{effective.Describe()}。{effective.Explain()}");
        }

        // 实例显式覆盖是 pin：即使不兼容也不在后台替换，只要求风险确认；32 位则始终拒绝。
        if (explicitRuntime is not null)
        {
            var status = MapStatus(effective.Evaluate(explicitRuntime).Status);
            candidates.Add(new JavaCandidateEvaluation(
                explicitRuntime,
                status,
                DescribeCandidateStatus(status, explicitRuntime, effective)));

            if (status == JavaCandidateStatus.Unsupported32Bit)
            {
                return new JavaSelectionDecision(
                    explicitRuntime,
                    JavaSelectionReason.ExplicitOverride,
                    JavaSelectionFailure.ExplicitRuntimeUnsupported,
                    effective,
                    candidates,
                    IsExplicitOverride: true,
                    RequiresRiskAcknowledgement: false,
                    IsBlocked: true,
                    RiskIdentity: null,
                    Explanation:
                    $"32 位 Java 不能用于 Minecraft 启动：{explicitRuntime.ExecutablePath}。请改用 64 位 Java。");
            }

            var requiresAcknowledgement = status is JavaCandidateStatus.TooOld
                or JavaCandidateStatus.TooNew
                or JavaCandidateStatus.UnknownVersion;
            return new JavaSelectionDecision(
                explicitRuntime,
                JavaSelectionReason.ExplicitOverride,
                JavaSelectionFailure.None,
                effective,
                candidates,
                IsExplicitOverride: true,
                RequiresRiskAcknowledgement: requiresAcknowledgement,
                IsBlocked: false,
                RiskIdentity: requiresAcknowledgement
                    ? JavaRiskIdentity.Create(explicitRuntime, effective)
                    : null,
                Explanation:
                $"使用实例显式 Java：{explicitRuntime.ExecutablePath}（{DescribeCandidateStatus(status, explicitRuntime, effective)}）。");
        }

        if (priorityCandidates.Count == 0)
        {
            return new JavaSelectionDecision(
                null,
                JavaSelectionReason.None,
                JavaSelectionFailure.NoCandidates,
                effective,
                candidates,
                IsExplicitOverride: false,
                RequiresRiskAcknowledgement: false,
                IsBlocked: false,
                RiskIdentity: null,
                Explanation: "没有发现任何 Java 运行时。");
        }

        JavaRuntime? selected = null;
        var selectedIndex = -1;

        for (var index = 0; index < priorityCandidates.Count; index++)
        {
            var runtime = priorityCandidates[index];
            if (runtime.IsKnown32Bit)
            {
                candidates.Add(new JavaCandidateEvaluation(
                    runtime,
                    JavaCandidateStatus.Unsupported32Bit,
                    "32 位 Java 不能用于 Minecraft 启动。"));
                continue;
            }

            if (runtime.Version is null)
            {
                candidates.Add(new JavaCandidateEvaluation(
                    runtime,
                    JavaCandidateStatus.UnknownVersion,
                    "无法识别 Java 版本，不能证明满足实例要求。"));
                continue;
            }

            if (effective.IsUnknown)
            {
                selected = runtime;
                selectedIndex = index;
                candidates.Add(new JavaCandidateEvaluation(
                    runtime,
                    JavaCandidateStatus.Selected,
                    "没有可信的 Java 要求来源，按优先级选择第一个可用运行时。"));
                break;
            }

            var status = MapStatus(effective.Evaluate(runtime).Status);
            if (status == JavaCandidateStatus.Selected)
            {
                selected = runtime;
                selectedIndex = index;
                candidates.Add(new JavaCandidateEvaluation(
                    runtime,
                    JavaCandidateStatus.Selected,
                    DescribeCandidateStatus(status, runtime, effective)));
                break;
            }

            candidates.Add(new JavaCandidateEvaluation(
                runtime,
                status,
                DescribeCandidateStatus(status, runtime, effective)));
        }

        if (selected is null)
        {
            return new JavaSelectionDecision(
                null,
                JavaSelectionReason.None,
                JavaSelectionFailure.NoCompatibleCandidate,
                effective,
                candidates,
                IsExplicitOverride: false,
                RequiresRiskAcknowledgement: false,
                IsBlocked: false,
                RiskIdentity: null,
                Explanation:
                $"没有满足 {effective.Describe()} 的 Java 运行时。{effective.Explain()}");
        }

        // 选中后继续评估剩余候选，让启动计划能解释“为什么没有选它”。
        for (var index = selectedIndex + 1; index < priorityCandidates.Count; index++)
        {
            var runtime = priorityCandidates[index];
            var status = runtime.IsKnown32Bit
                ? JavaCandidateStatus.Unsupported32Bit
                : runtime.Version is null
                    ? JavaCandidateStatus.UnknownVersion
                    : MapStatus(effective.Evaluate(runtime).Status) switch
                    {
                        JavaCandidateStatus.Selected => JavaCandidateStatus.CompatibleButNotPreferred,
                        var other => other,
                    };

            candidates.Add(new JavaCandidateEvaluation(
                runtime,
                status,
                status == JavaCandidateStatus.CompatibleButNotPreferred
                    ? "兼容，但在全局优先级中排在已选运行时之后。"
                    : DescribeCandidateStatus(status, runtime, effective)));
        }

        var reason = configuredPriorityCount > 0 && selectedIndex < configuredPriorityCount
            ? JavaSelectionReason.GlobalPriority
            : JavaSelectionReason.DiscoveryDefault;
        var reasonText = reason == JavaSelectionReason.GlobalPriority
            ? "按全局 Java 优先级选择"
            : "按有界发现结果的默认顺序选择";

        return new JavaSelectionDecision(
            selected,
            reason,
            JavaSelectionFailure.None,
            effective,
            candidates,
            IsExplicitOverride: false,
            RequiresRiskAcknowledgement: false,
            IsBlocked: false,
            RiskIdentity: null,
            Explanation: $"{reasonText}：{selected.ExecutablePath}；{effective.Describe()}。");
    }

    private static JavaCandidateStatus MapStatus(JavaCompatibilityStatus status) => status switch
    {
        JavaCompatibilityStatus.Compatible => JavaCandidateStatus.Selected,
        JavaCompatibilityStatus.TooOld => JavaCandidateStatus.TooOld,
        JavaCompatibilityStatus.TooNew => JavaCandidateStatus.TooNew,
        JavaCompatibilityStatus.UnsupportedArchitecture => JavaCandidateStatus.Unsupported32Bit,
        JavaCompatibilityStatus.Unknown => JavaCandidateStatus.UnknownVersion,
        JavaCompatibilityStatus.UnknownRequirement => JavaCandidateStatus.Selected,
        JavaCompatibilityStatus.RequirementsConflict => JavaCandidateStatus.RequirementsConflict,
        _ => JavaCandidateStatus.UnknownVersion,
    };

    private static string DescribeCandidateStatus(
        JavaCandidateStatus status,
        JavaRuntime runtime,
        EffectiveJavaRequirements requirements) => status switch
        {
            JavaCandidateStatus.Selected => $"满足 {requirements.Describe()}。",
            JavaCandidateStatus.CompatibleButNotPreferred => "兼容，但优先级较低。",
            JavaCandidateStatus.TooOld =>
                $"Java {runtime.Version?.Major.ToString() ?? "?"} 低于 {requirements.Describe()}。",
            JavaCandidateStatus.TooNew =>
                $"Java {runtime.Version?.Major.ToString() ?? "?"} 高于 {requirements.Describe()}。",
            JavaCandidateStatus.Unsupported32Bit => "32 位 Java 不能用于 Minecraft 启动。",
            JavaCandidateStatus.UnknownVersion => "无法识别 Java 版本。",
            JavaCandidateStatus.RequirementsConflict => "有效 Java 要求冲突。",
            _ => "未知状态。",
        };
}

/// <summary>
/// 风险确认的稳定身份：规范化可执行文件路径 + 检测到的 Java 版本 + 有效要求指纹。
/// 三者任一变化都会使旧确认失效。
/// </summary>
public static class JavaRiskIdentity
{
    public const string SettingsKey = "launcher.java.incompatiblePromptAcknowledged";

    public static string Create(JavaRuntime runtime, EffectiveJavaRequirements? requirements)
    {
        var path = CanonicalizePath(runtime.ExecutablePath);
        var version = runtime.Version is null
            ? "unknown"
            : $"{runtime.Version.Major}.{runtime.Version.Minor}.{runtime.Version.Patch}";
        var fingerprint = requirements?.EffectiveFingerprint() ?? "none";
        return $"v2|{path}|{version}|{fingerprint}";
    }

    public static string CanonicalizePath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(fullPath))
            {
                var target = File.ResolveLinkTarget(fullPath, returnFinalTarget: true);
                if (target is not null)
                {
                    fullPath = Path.GetFullPath(target.FullName);
                }
            }

            fullPath = Path.TrimEndingDirectorySeparator(fullPath);
            return OperatingSystem.IsWindows() ? fullPath.ToLowerInvariant() : fullPath;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;
        }
    }
}

/// <summary>
/// 兼容旧调用点的风险确认入口。新代码应直接使用 <see cref="JavaRiskIdentity"/>，
/// 传入完整的 <see cref="EffectiveJavaRequirements"/>。
/// </summary>
public static class JavaCompatibilityAcknowledgement
{
    public static string Create(JavaRuntime runtime, JavaRequirement requirement) =>
        JavaRiskIdentity.Create(runtime, EffectiveJavaRequirements.FromRequirement(requirement));
}

public sealed record JavaCandidatePlan(
    string ExecutablePath,
    string? Version,
    string? Architecture,
    string Status,
    string Detail);

/// <summary>
/// 启动计划中的 Java 记录，包含最终路径、版本、架构、要求摘要、选择原因与被拒候选。
/// </summary>
public sealed record JavaLaunchPlan(
    string? ExecutablePath,
    string? HomePath,
    string? Version,
    string? Architecture,
    string? Vendor,
    string? RequirementSummary,
    string? RequirementDetail,
    string? RequirementFingerprint,
    string SelectionReason,
    string? Failure,
    bool IsExplicitOverride,
    bool IsBlocked,
    string CompatibilityStatus,
    bool IsCompatibilityWarningAcknowledged,
    IReadOnlyList<JavaCandidatePlan> EvaluatedCandidates,
    IReadOnlyList<JavaRequirementSource> RequirementSources,
    string Explanation)
{
    public static JavaLaunchPlan From(
        JavaSelectionDecision decision,
        JavaCompatibilityResult? compatibility,
        bool compatibilityWarningAcknowledged)
    {
        var runtime = decision.Runtime;
        return new JavaLaunchPlan(
            runtime?.ExecutablePath,
            runtime?.HomePath,
            runtime?.Version?.Raw,
            runtime?.ArchitectureLabel,
            runtime?.Vendor,
            decision.Requirements?.Describe(),
            decision.Requirements?.Explain(),
            decision.Requirements?.Fingerprint(),
            decision.Reason.ToString(),
            decision.Failure == JavaSelectionFailure.None ? null : decision.Failure.ToString(),
            decision.IsExplicitOverride,
            decision.IsBlocked,
            compatibility?.Status.ToString() ?? "None",
            compatibilityWarningAcknowledged,
            decision.Candidates
                .Select(candidate => new JavaCandidatePlan(
                    candidate.Runtime.ExecutablePath,
                    candidate.Runtime.Version?.Raw,
                    candidate.Runtime.ArchitectureLabel,
                    candidate.Status.ToString(),
                    candidate.Detail))
                .ToArray(),
            decision.Requirements?.Sources ?? [],
            decision.Explanation);
    }
}
