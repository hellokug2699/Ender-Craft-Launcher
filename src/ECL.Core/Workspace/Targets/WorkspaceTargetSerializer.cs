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

    /// <summary>目标类型在当前构建尚未实现（file:、world:）。</summary>
    UnsupportedScheme,

    /// <summary>目标类型存在，但 schema 版本高于当前构建支持的版本。</summary>
    UnsupportedSchemaVersion,

    /// <summary>声明缺少标识，或在标识内部再次声明协议。</summary>
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
/// <item>旧值兼容：现存裸实例 ID 与旧 <c>instance:&lt;id&gt;</c> 引用被规范化为 <c>instance:v1:&lt;id&gt;</c>。</item>
/// </list>
///
/// 它**不承担**文件授权、路径边界、符号链接/重解析点校验——那些属于受控资源解析与访问服务
/// （ADR 0018/0022/0042/0045），不得塞进通用 serializer。
///
/// 编码规则：
/// <list type="bullet">
/// <item>目的地 → <c>null</c>；</item>
/// <item>实例 → <c>instance:v1:&lt;stableInstanceId&gt;</c>；标识规范化 = trim + Unicode NFC，大小写敏感；</item>
/// <item><c>file:</c> / <c>world:</c> 被显式拒绝，因为对应目标类型在各自消费者批次才引入；</item>
/// <item>版本号高于当前构建的实例目标被拒绝（<see cref="WorkspaceTargetError.UnsupportedSchemaVersion"/>），不退化为任意字符串。</item>
/// </list>
/// </summary>
public static class WorkspaceTargetSerializer
{
    /// <summary>目标资源允许的最大长度（字符）。</summary>
    public const int MaxResourceLength = 256;

    /// <summary>实例目标的协议前缀。</summary>
    public const string InstanceScheme = "instance";

    /// <summary>实例目标当前使用的版本标记。</summary>
    public const string InstanceVersionTag = "v1";

    private static readonly string[] KnownSchemes = ["instance", "file", "world"];

    /// <summary>序列化为 <c>TargetResource</c>；目的地返回 <c>null</c>。</summary>
    public static string? Serialize(WorkspaceTarget? target) => target switch
    {
        null => null,
        { Kind: WorkspaceTargetKind.Destination } => null,
        { Kind: WorkspaceTargetKind.Instance, StableInstanceId: { } instanceId } =>
            $"{InstanceScheme}:{InstanceVersionTag}:{instanceId}",
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

        if (FindScheme(trimmed) is not { } scheme)
        {
            // 旧裸实例 ID：一次性规范化为 instance:v1:<id>。
            return NormalizeInstanceId(trimmed);
        }

        var payload = trimmed[(scheme.Length + 1)..];
        if (payload.Length == 0)
        {
            return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.MalformedResource);
        }

        if (!string.Equals(scheme, InstanceScheme, StringComparison.OrdinalIgnoreCase))
        {
            // file: / world: 目标类型在其消费者批次才引入。
            return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.UnsupportedScheme);
        }

        if (TryReadVersion(payload, out var version, out var remainder))
        {
            if (!string.Equals(version, InstanceVersionTag, StringComparison.OrdinalIgnoreCase))
            {
                return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.UnsupportedSchemaVersion);
            }

            return remainder.Length == 0
                ? WorkspaceTargetParseResult.Fail(WorkspaceTargetError.MalformedResource)
                : NormalizeInstanceId(remainder);
        }

        // 旧 instance:<id> 写法：一次性规范化。
        if (FindScheme(payload) is not null)
        {
            return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.MalformedResource);
        }

        return NormalizeInstanceId(payload);
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

    /// <summary>只校验并规范化实例标识，不要求调用方先构造目标。</summary>
    public static bool TryNormalizeInstanceId(string raw, out string normalized, out WorkspaceTargetError error)
    {
        var result = NormalizeInstanceId(raw);
        normalized = result.Target?.StableInstanceId ?? string.Empty;
        error = result.Error;
        return result.Success;
    }

    private static WorkspaceTargetParseResult NormalizeInstanceId(string instanceId)
    {
        if (instanceId.Length > MaxResourceLength)
        {
            return WorkspaceTargetParseResult.Fail(WorkspaceTargetError.TooLong);
        }

        foreach (var character in instanceId)
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

        var normalized = instanceId.Normalize(NormalizationForm.FormC);
        return WorkspaceTargetParseResult.Ok(WorkspaceTarget.CreateInstance(normalized));
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

    /// <summary>读取 <c>v&lt;数字&gt;:</c> 形式的前缀；不是版本前缀时返回 <c>false</c>。</summary>
    private static bool TryReadVersion(string payload, out string version, out string remainder)
    {
        version = string.Empty;
        remainder = string.Empty;

        if (payload.Length < 3 || payload[0] is not ('v' or 'V') || !char.IsAsciiDigit(payload[1]))
        {
            return false;
        }

        var index = 1;
        while (index < payload.Length && char.IsAsciiDigit(payload[index]))
        {
            index++;
        }

        if (index >= payload.Length || payload[index] != ':')
        {
            return false;
        }

        version = payload[..index];
        remainder = payload[(index + 1)..];
        return true;
    }
}
