// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Ecl.World.Worlds;

namespace Ecl.World.Dimensions;

/// <summary>
/// 维度的逻辑身份，例如 <c>minecraft:the_nether</c> 或 <c>example:moon</c>。
/// 它不是目录名：磁盘路径由 <see cref="DimensionDiscoveryService"/> 依据版本规则与元数据单独解析
/// （ADR 0031 §2）。仅接受规范化的资源位置字符集；不静默改写大小写，避免把两个不同目录合并。
/// </summary>
public readonly partial struct DimensionId : IEquatable<DimensionId>
{
    public static readonly DimensionId Overworld = new("minecraft", "overworld");
    public static readonly DimensionId Nether = new("minecraft", "the_nether");
    public static readonly DimensionId End = new("minecraft", "the_end");

    private static readonly HashSet<string> VanillaIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            Overworld.Value,
            Nether.Value,
            End.Value,
        };

    public DimensionId(string @namespace, string path)
    {
        if (!NamespaceRegex().IsMatch(@namespace))
        {
            throw new ArgumentException($"维度命名空间无效：{@namespace}", nameof(@namespace));
        }

        if (!PathRegex().IsMatch(path) || path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException($"维度路径无效：{path}", nameof(path));
        }

        Namespace = @namespace.ToLowerInvariant();
        Path = path.ToLowerInvariant();
    }

    public string Namespace { get; }

    public string Path { get; }

    public string Value => $"{Namespace}:{Path}";

    public bool IsVanilla => string.Equals(Namespace, "minecraft", StringComparison.Ordinal) &&
                             Path is "overworld" or "the_nether" or "the_end";

    public DimensionKind Kind => Value switch
    {
        "minecraft:overworld" => DimensionKind.Overworld,
        "minecraft:the_nether" => DimensionKind.Nether,
        "minecraft:the_end" => DimensionKind.End,
        _ => DimensionKind.Custom,
    };

    public static bool IsKnownVanilla(string id) => VanillaIds.Contains(id);

    public static bool TryParse([NotNullWhen(true)] string? text, out DimensionId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var separator = text.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == text.Length - 1)
        {
            return false;
        }

        var @namespace = text[..separator];
        var path = text[(separator + 1)..];
        if (!NamespaceRegex().IsMatch(@namespace) || !PathRegex().IsMatch(path))
        {
            return false;
        }

        // 拒绝大小写混写与 . / .. 路径段：这些要么不是有效 ID，要么会造成目录歧义。
        if (!string.Equals(@namespace, @namespace.ToLowerInvariant(), StringComparison.Ordinal) ||
            !string.Equals(path, path.ToLowerInvariant(), StringComparison.Ordinal) ||
            path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        id = new DimensionId(@namespace, path);
        return true;
    }

    public static DimensionId Parse(string text) =>
        TryParse(text, out var id) ? id : throw new FormatException($"维度 ID 无效：{text}");

    public bool Equals(DimensionId other) =>
        string.Equals(Namespace, other.Namespace, StringComparison.Ordinal) &&
        string.Equals(Path, other.Path, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is DimensionId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Namespace, Path);

    public static bool operator ==(DimensionId left, DimensionId right) => left.Equals(right);

    public static bool operator !=(DimensionId left, DimensionId right) => !left.Equals(right);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9_.-]+$")]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex("^[a-z0-9_./-]+$")]
    private static partial Regex PathRegex();
}
