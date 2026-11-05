// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace Ecl.Core.Workspace;

/// <summary>目标解析/规范化的失败原因。用于把工作区呈现为 <c>Unavailable</c> 并给出可执行原因。</summary>
public enum WorkspaceTargetError
{
    None = 0,

    /// <summary>超过 <see cref="WorkspaceTargetSerializer.MaxResourceLength"/>。</summary>
    TooLong,

    /// <summary>包含控制字符等不可用于身份表示的字符。</summary>
    InvalidCharacter,

    /// <summary>包含目录分隔符；目标资源是身份引用，不是路径。</summary>
    PathSeparator,

    /// <summary>协议本身可识别，但当前构建尚未实现对应目标类型（instance:v*、file:、world:）。</summary>
    UnsupportedScheme,

    /// <summary>协议声明缺少目标资源，或在资源内部再次声明协议。</summary>
    MalformedResource,
}

/// <summary><see cref="WorkspaceTargetSerializer.Parse"/> 的结果。</summary>
public readonly record struct WorkspaceTargetParseResult(WorkspaceTarget? Target, WorkspaceTargetError Error)
{
    public bool Success => Error == WorkspaceTargetError.None;

    public static WorkspaceTargetParseResult Ok(WorkspaceTarget target) => new(target, WorkspaceTargetError.None);

    public static WorkspaceTargetParseResult Fail(WorkspaceTargetError error) => new(null, error);
}

/// <summary>
/// 工作区目标的**唯一**版本化序列化器（迁移规划 §4.3、§5.8）。
///
/// 职责边界：
/// <list type="bullet">
/// <item>身份表示、版本化、语法校验与规范化；</item>
/// <item>旧值兼容：现存裸实例 ID 与旧 <c>instance:&lt;id&gt;</c> 引用被规范化为同一种身份。</item>
/// </list>
///
/// 它**不承担**文件授权、路径边界、符号链接/重解析点校验——那些属于受控资源解析与访问服务
/// （ADR 0018/0022/0042/0045），不得塞进通用 serializer。
///
/// B0 的编码规则：
/// <list type="bullet">
/// <item>目的地 → <c>null</c>；</item>
/// <item>旧裸资源 → 规范化后的裸字符串（trim + Unicode NFC），保持大小写敏感；</item>
/// <item><c>instance:v1:</c> / <c>file:</c> / <c>world:</c> 被显式拒绝（<see cref="WorkspaceTargetError.UnsupportedScheme"/>），
/// 因为对应目标类型在 B2 / File / World 消费者批次才引入。</item>
/// </list>
/// </summary>
public static class WorkspaceTargetSerializer
{
    /// <summary>旧裸资源允许的最大长度（字符）。</summary>
    public const int MaxResourceLength = 256;

    /// <summary>目标身份当前使用的 schema 版本。</summary>
    public const int CurrentSchemaVersion = WorkspaceTarget.DestinationSchemaVersion;

    private static readonly string[] KnownSchemes = ["instance", "file", "world"];

    /// <summary>序列化为 <c>TargetResource</c>；目的地返回 <c>null</c>。</summary>
    public static string? Serialize(WorkspaceTarget? target) => target switch
    {
        null => null,
        { Kind: WorkspaceTargetKind.Destination } => null,
        { Kind: WorkspaceTargetKind.Legacy, Resource: { } resource } => resource,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "未知的工作区目标类型。"),
    };

    /// <summary>解析并规范化目标资源。<c>null</c> 或空白表示全局目的地。</summary>
    public static WorkspaceTargetParseResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return WorkspaceTargetParseResult.Ok(WorkspaceTarget.Destination);
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxResourceLength)
        {
            return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.TooLong);
        }

        if (FindScheme(trimmed) is { } scheme)
        {
            var payload = trimmed[(scheme.Length + 1)..];
            if (payload.Length == 0)
            {
                return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.MalformedResource);
            }

            if (string.Equals(scheme, "instance", StringComparison.OrdinalIgnoreCase))
            {
                // instance:v1:<id> 尚未在此构建实现；B0 不得提前定义该编码。
                if (IsVersionedPayload(payload))
                {
                    return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.UnsupportedScheme);
                }

                // 旧 instance:<id> 一次性规范化为裸实例 ID。
                if (FindScheme(payload) is not null)
                {
                    return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.MalformedResource);
                }

                return Normalize(payload);
            }

            // file: / world: 目标类型在其消费者批次才引入。
            return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.UnsupportedScheme);
        }

        return Normalize(trimmed);
    }

    /// <summary>解析目标资源；失败时返回 <c>false</c>。</summary>
    public static bool TryParse(string? raw, out WorkspaceTarget? target)
    {
        var result = Parse(raw);
        target = result.Target;
        return result.Success;
    }

    /// <summary>解析失败时给出异常，供调用方在非用户输入路径上使用。</summary>
    public static WorkspaceTarget ParseRequired(string? raw)
    {
        var result = Parse(raw);
        return result.Success
            ? result.Target!
            : throw new FormatException($"工作区目标无法解析：{result.Error}。");
    }

    /// <summary>仅做语法校验与规范化，不改变旧裸资源的含义。</summary>
    public static bool TryNormalizeLegacyResource(
        string raw,
        out string normalized,
        out WorkspaceTargetError error)
    {
        var result = Parse(raw);
        if (!result.Success || result.Target is not { Kind: WorkspaceTargetKind.Legacy } target)
        {
            normalized = string.Empty;
            error = result.Success ? WorkspaceTargetError.MalformedResource : result.Error;
            return false;
        }

        normalized = target.Resource!;
        error = WorkspaceTargetError.None;
        return true;
    }

    private static WorkspaceTargetParseResult Normalize(string resource)
    {
        if (resource.Length > MaxResourceLength)
        {
            return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.TooLong);
        }

        foreach (var character in resource)
        {
            if (char.IsControl(character))
            {
                return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.InvalidCharacter);
            }

            if (character is '/' or '\\')
            {
                return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.PathSeparator);
            }
        }

        var normalized = resource.Normalize(NormalizationForm.FormC);
        return WorkspaceTargetParseResult.Ok(WorkspaceTarget.CreateLegacy(normalized));
    }

    /// <summary>返回字符串开头的已知协议名（不含冒号）；不构成已知协议时返回 <c>null</c>。</summary>
    private static string? FindScheme(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0)
        {
            return null;
        }

        var candidate = value[..separator];
        foreach (var scheme in KnownSchemes)
        {
            if (candidate.Length == scheme.Length
                && string.Equals(candidate, scheme, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>判断载荷是否为 <c>v&lt;数字&gt;:...</c> 形式的版本化身份。</summary>
    private static bool IsVersionedPayload(string payload)
    {
        if (payload.Length < 3 || payload[0] is not ('v' or 'V') || !char.IsAsciiDigit(payload[1]))
        {
            return false;
        }

        var index = 1;
        while (index < payload.Length && char.IsAsciiDigit(payload[index]))
        {
            index++;
        }

        return index < payload.Length && payload[index] == ':';
    }
}
