// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace Ecl.Minecraft.Mods;

/// <summary>
/// 仅覆盖 mods.toml 需要的 TOML 子集：注释、字符串（含三引号多行）、数组、布尔/数字、
/// [table] 与 [[array-of-table]]。不追求完整 TOML 规范，但能稳定解析 Forge/NeoForge 元数据。
/// </summary>
public static class TomlLite
{
    public static IReadOnlyList<TomlTable> Parse(string text)
    {
        var tables = new List<TomlTable>();
        TomlTable? current = null;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = StripComment(line).Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith("[[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]]", StringComparison.Ordinal))
            {
                current = new TomlTable(trimmed[2..^2].Trim());
                tables.Add(current);
                continue;
            }

            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                current = new TomlTable(trimmed[1..^1].Trim());
                tables.Add(current);
                continue;
            }

            if (current is null)
            {
                current = new TomlTable(string.Empty);
                tables.Add(current);
            }

            var (key, value) = SplitKeyValue(trimmed);
            if (key.Length == 0)
            {
                continue;
            }

            // 跨行三引号字符串。
            if (value.StartsWith("\"\"\"", StringComparison.Ordinal) ||
                value.StartsWith("'''", StringComparison.Ordinal))
            {
                var delimiter = value[..3];
                var builder = new StringBuilder();
                var firstLine = value[3..];
                if (firstLine.EndsWith(delimiter, StringComparison.Ordinal) && firstLine.Length >= 3)
                {
                    current.Values[key] = firstLine[..^3];
                    continue;
                }

                builder.Append(firstLine);
                for (index++; index < lines.Length; index++)
                {
                    var next = lines[index];
                    if (next.TrimEnd().EndsWith(delimiter, StringComparison.Ordinal))
                    {
                        var end = next.TrimEnd();
                        builder.Append('\n').Append(end[..^3]);
                        break;
                    }

                    builder.Append('\n').Append(next);
                }

                current.Values[key] = builder.ToString().TrimStart('\n');
                continue;
            }

            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                current.Arrays[key] = ParseArray(value);
                continue;
            }

            current.Values[key] = Unquote(value);
        }

        return tables;
    }

    private static (string Key, string Value) SplitKeyValue(string line)
    {
        var equals = line.IndexOf('=');
        return equals <= 0
            ? (string.Empty, string.Empty)
            : (line[..equals].Trim(), line[(equals + 1)..].Trim());
    }

    private static string StripComment(string line)
    {
        // 多行字符串的起止行可能包含 #；保留原样，避免破坏内容。
        if (line.Contains("'''", StringComparison.Ordinal) ||
            line.Contains("\"\"\"", StringComparison.Ordinal))
        {
            return line;
        }

        var quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (character == '\\' && quote == '"' && index + 1 < line.Length)
                {
                    index++;
                    continue;
                }

                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (character == '#')
            {
                return line[..index];
            }
        }

        return line;
    }

    private static List<string> ParseArray(string value)
    {
        var result = new List<string>();
        var inner = value[1..^1];
        var builder = new StringBuilder();
        var quote = '\0';

        foreach (var character in inner)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    builder.Append(character);
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (character == ',')
            {
                Add();
                continue;
            }

            builder.Append(character);
        }

        Add();
        return result;

        void Add()
        {
            var item = builder.ToString().Trim();
            if (item.Length > 0)
            {
                result.Add(Unquote(item));
            }

            builder.Clear();
        }
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            (trimmed[0] == '"' && trimmed[^1] == '"' || trimmed[0] == '\'' && trimmed[^1] == '\''))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);
    }
}

public sealed class TomlTable(string name)
{
    public string Name { get; } = name;

    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> Arrays { get; } = new(StringComparer.OrdinalIgnoreCase);
}
