// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Versions;

public static class MavenCoordinates
{
    public static string? GetPath(string? name, string extension = "jar")
    {
        var parts = Split(name);
        if (parts is null)
        {
            return null;
        }

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length >= 4 ? $"-{parts[3]}" : string.Empty;
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{extension}";
    }

    public static string? GetBaseName(string? name)
    {
        var parts = Split(name);
        return parts is null ? null : $"{parts[0]}:{parts[1]}:{parts[2]}";
    }

    public static string? GetClassifier(string? name)
    {
        var parts = Split(name);
        return parts is { Length: >= 4 } ? parts[3] : null;
    }

    /// <summary>
    /// 解析 install_profile 中的 Maven 引用，例如 [group:artifact:version:classifier@zip]。
    /// </summary>
    public static bool TryParseReference(string? token, out string coordinate, out string extension)
    {
        coordinate = string.Empty;
        extension = "jar";

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        if (trimmed.Length < 3 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            return false;
        }

        var inner = trimmed[1..^1];
        var at = inner.LastIndexOf('@');
        if (at > 0)
        {
            var parsedExtension = inner[(at + 1)..];
            if (parsedExtension.Length > 0)
            {
                extension = parsedExtension;
            }

            inner = inner[..at];
        }

        if (inner.Length == 0)
        {
            return false;
        }

        coordinate = inner;
        return true;
    }

    private static string[]? Split(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalized = name.Trim();
        var at = normalized.LastIndexOf('@');
        if (at > 0 && !normalized[(at + 1)..].Contains(':'))
        {
            normalized = normalized[..at];
        }

        var parts = normalized.Split(':');
        return parts.Length >= 3 ? parts : null;
    }
}
