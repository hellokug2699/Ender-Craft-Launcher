// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Versions;

public static class MavenCoordinates
{
    public static string? GetPath(string? name)
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
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
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

    private static string[]? Split(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parts = name.Split(':');
        return parts.Length >= 3 ? parts : null;
    }
}
