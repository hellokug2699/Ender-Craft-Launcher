// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;

namespace Ecl.Java;

/// <summary>有效 Java 要求的来源类别。要求必须保留来源，不能只留下一个版本号。</summary>
public enum JavaRequirementKind
{
    Minecraft,
    Loader,
    Mod,
    Instance,
    UserPolicy,
    Unknown,
}

/// <summary>
/// 要求证据质量。只有强证据或可证明的下界才能进入硬约束；
/// 文件名、猜测和社区说法不能静默升级为硬约束。
/// </summary>
public enum JavaRequirementEvidence
{
    DeclaredByVersionMetadata,
    InferredFromGameVersion,
    LoaderMetadata,
    ModClassFileLowerBound,
    ExplicitInstanceConstraint,
    Unknown,
}

/// <summary>
/// 一条有效 Java 要求。最小/最大主版本都可能是 null；没有版本约束的来源只用于解释。
/// </summary>
public sealed record JavaRequirementSource(
    JavaRequirementKind Kind,
    string Id,
    JavaRequirementEvidence Evidence,
    string Description,
    int? MinimumMajor = null,
    int? MaximumMajor = null,
    string? Component = null,
    bool IsHard = true)
{
    public bool HasVersionConstraint => MinimumMajor.HasValue || MaximumMajor.HasValue;

    public bool IsAdvisory => !IsHard;

    public string CanonicalForm()
    {
        var minimum = MinimumMajor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
        var maximum = MaximumMajor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
        return string.Join(
            '|',
            Kind.ToString(),
            Id,
            Evidence.ToString(),
            minimum,
            maximum,
            Component ?? "-",
            IsHard ? "hard" : "advisory");
    }
}

/// <summary>互相矛盾的要求。冲突必须被诊断，不能靠“选最高版本”掩盖。</summary>
public sealed record JavaRequirementConflict(
    string Description,
    IReadOnlyList<JavaRequirementSource> Sources);

/// <summary>
/// 合并后的有效 Java 要求。Core 的选择器只消费这个模型，
/// WPF/CLI 只负责展示，不自行推导版本规则。
/// </summary>
public sealed class EffectiveJavaRequirements
{
    private readonly int? _componentMinimum;
    private readonly string? _component;

    private EffectiveJavaRequirements(
        IReadOnlyList<JavaRequirementSource> sources,
        bool isUnknown)
    {
        Sources = sources;
        IsUnknown = isUnknown;

        var hard = sources
            .Where(source => source.IsHard && source.HasVersionConstraint)
            .ToArray();
        MinimumMajor = hard
            .Where(source => source.MinimumMajor.HasValue)
            .Select(source => source.MinimumMajor!.Value)
            .DefaultIfEmpty(0)
            .Max();

        var maximums = hard
            .Where(source => source.MaximumMajor.HasValue)
            .Select(source => source.MaximumMajor!.Value)
            .ToArray();
        MaximumMajor = maximums.Length == 0 ? null : maximums.Min();

        var minimumSource = hard
            .Where(source => source.MinimumMajor == MinimumMajor && !string.IsNullOrWhiteSpace(source.Component))
            .OrderBy(source => source.Kind == JavaRequirementKind.Mod ? 1 : 0)
            .FirstOrDefault();
        _componentMinimum = minimumSource is null ? null : minimumSource.MinimumMajor;
        _component = minimumSource?.Component;

        if (MinimumMajor > 0 && MaximumMajor is { } maximum && MinimumMajor > maximum)
        {
            var conflicting = hard
                .Where(source =>
                    source.MinimumMajor == MinimumMajor ||
                    source.MaximumMajor == MaximumMajor)
                .ToArray();
            Conflicts =
            [
                new JavaRequirementConflict(
                    $"最低 Java {MinimumMajor} 与最高 Java {maximum} 冲突。",
                    conflicting),
            ];
        }
        else
        {
            Conflicts = [];
        }
    }

    public IReadOnlyList<JavaRequirementSource> Sources { get; }

    /// <summary>没有任何可信来源能说明 Java 要求。未知不等于不兼容。</summary>
    public bool IsUnknown { get; }

    public int MinimumMajor { get; }

    public int? MaximumMajor { get; }

    public string? Component => _componentMinimum == MinimumMajor ? _component : null;

    public IReadOnlyList<JavaRequirementConflict> Conflicts { get; }

    public bool HasConflicts => Conflicts.Count > 0;

    public static EffectiveJavaRequirements Empty { get; } = new([], isUnknown: true);

    public static EffectiveJavaRequirements FromSource(JavaRequirementSource source) =>
        new([source], isUnknown: !source.HasVersionConstraint && source.IsHard);

    public static EffectiveJavaRequirements FromRequirement(
        JavaRequirement requirement,
        JavaRequirementKind kind = JavaRequirementKind.Instance,
        JavaRequirementEvidence evidence = JavaRequirementEvidence.ExplicitInstanceConstraint,
        string id = "instance:explicit") =>
        FromSource(new JavaRequirementSource(
            kind,
            id,
            evidence,
            requirement.Describe(),
            requirement.MinimumMajor,
            requirement.MaximumMajor,
            requirement.Component));

    public static EffectiveJavaRequirements Merge(IEnumerable<JavaRequirementSource> sources)
    {
        var materialized = sources
            .Where(source => source is not null)
            .ToArray();
        if (materialized.Length == 0)
        {
            return Empty;
        }

        var hasConstraint = materialized.Any(source => source.IsHard && source.HasVersionConstraint);
        return new EffectiveJavaRequirements(materialized, isUnknown: !hasConstraint);
    }

    public JavaCompatibilityResult Evaluate(JavaRuntime? runtime)
    {
        var requirement = ToRequirement();
        if (HasConflicts)
        {
            return new JavaCompatibilityResult(
                JavaCompatibilityStatus.RequirementsConflict,
                requirement,
                runtime?.Version,
                this);
        }

        if (runtime is null)
        {
            return new JavaCompatibilityResult(
                JavaCompatibilityStatus.Unknown,
                requirement,
                null,
                this);
        }

        if (runtime.IsKnown32Bit)
        {
            return new JavaCompatibilityResult(
                JavaCompatibilityStatus.UnsupportedArchitecture,
                requirement,
                runtime.Version,
                this);
        }

        if (runtime.Version is null)
        {
            return new JavaCompatibilityResult(
                JavaCompatibilityStatus.Unknown,
                requirement,
                null,
                this);
        }

        if (IsUnknown)
        {
            return new JavaCompatibilityResult(
                JavaCompatibilityStatus.UnknownRequirement,
                requirement,
                runtime.Version,
                this);
        }

        return JavaCompatibility.Evaluate(requirement, runtime) with { EffectiveRequirements = this };
    }

    public JavaCompatibilityStatus EvaluateStatus(JavaRuntime? runtime) => Evaluate(runtime).Status;

    public bool IsSatisfiedBy(JavaRuntime? runtime) =>
        Evaluate(runtime).Status == JavaCompatibilityStatus.Compatible;

    public JavaRequirement ToRequirement(int fallbackMinimumMajor = 21) =>
        new(
            MinimumMajor > 0 ? MinimumMajor : Math.Max(0, fallbackMinimumMajor),
            Component,
            MaximumMajor);

    /// <summary>用于 UI 的简短描述，例如 “Java 21+”。</summary>
    public string Describe()
    {
        if (IsUnknown)
        {
            return "Java（要求未知）";
        }

        return MaximumMajor is { } maximum
            ? MinimumMajor == maximum
                ? $"Java {MinimumMajor}"
                : $"Java {MinimumMajor}–{maximum}"
            : $"Java {MinimumMajor}+";
    }

    /// <summary>用于诊断/启动计划的详细说明，包含来源与证据。</summary>
    public string Explain()
    {
        if (Sources.Count == 0)
        {
            return "没有可用的 Java 要求来源。";
        }

        var parts = Sources.Select(source =>
            $"{source.Description}（{source.Evidence}，{(source.IsHard ? "硬约束" : "建议")}）");
        var text = string.Join("；", parts);
        return HasConflicts
            ? $"{text}；冲突：{string.Join("；", Conflicts.Select(conflict => conflict.Description))}"
            : text;
    }

    /// <summary>
    /// 基于结构化来源的稳定指纹，用于诊断与“要求来源是否变化”的记录。
    /// 风险确认使用 <see cref="EffectiveFingerprint"/>，只绑定实际生效的版本/组件约束。
    /// </summary>
    public string Fingerprint()
    {
        var canonical = string.Join(
            "\n",
            Sources
                .Select(source => source.CanonicalForm())
                .OrderBy(value => value, StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>
    /// 实际生效的要求指纹：最低/最高主版本、组件与未知状态。
    /// 来源集合变化但有效约束不变时，不应强迫用户重新确认同一个风险。
    /// </summary>
    public string EffectiveFingerprint()
    {
        var canonical = string.Join(
            '|',
            MinimumMajor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MaximumMajor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            Component ?? "-",
            IsUnknown ? "unknown" : "known",
            HasConflicts ? "conflict" : "ok");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
