// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Workspace;

/// <summary>
/// 工作区稳定标识。与 <see cref="WorkspaceDescriptor.ToolId"/> 不同：一个工具可以打开多个工作区，
/// 关闭再打开同一目标会得到新的 WorkspaceId。
/// 只允许小写字母、数字、点、下划线和连字符，避免被当作路径或日志字段时产生歧义。
/// </summary>
public sealed record WorkspaceId
{
    public const int MaxLength = 64;

    private WorkspaceId(string value) => Value = value;

    public string Value { get; }

    public static WorkspaceId Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException($"WorkspaceId 长度不能超过 {MaxLength}。", nameof(value));
        }

        foreach (var character in trimmed)
        {
            if (!IsAllowed(character))
            {
                throw new ArgumentException($"WorkspaceId 包含非法字符“{character}”。", nameof(value));
            }
        }

        return new WorkspaceId(trimmed);
    }

    /// <summary>为新的工作区实例生成 ID；不承载任何业务含义。</summary>
    public static WorkspaceId New() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;

    private static bool IsAllowed(char character) =>
        character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '_'
            or '-';
}
