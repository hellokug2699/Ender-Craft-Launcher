// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

public sealed record JavaVersion(int Major, int Minor, int Patch, string Raw) : IComparable<JavaVersion>
{
    public static JavaVersion? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();

        var quoteStart = text.IndexOf('"');
        if (quoteStart >= 0)
        {
            var quoteEnd = text.IndexOf('"', quoteStart + 1);
            if (quoteEnd > quoteStart)
            {
                text = text[(quoteStart + 1)..quoteEnd];
            }
        }
        else
        {
            var firstSpace = text.IndexOf(' ');
            if (firstSpace > 0)
            {
                text = text[..firstSpace];
            }
        }

        foreach (var prefix in new[] { "openjdk-", "openjdk", "jdk-", "jdk", "jre-", "jre", "java-" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].TrimStart('-');
                break;
            }
        }

        var plusIndex = text.IndexOf('+');
        if (plusIndex > 0)
        {
            text = text[..plusIndex];
        }

        var dashIndex = text.IndexOf('-');
        if (dashIndex > 0)
        {
            text = text[..dashIndex];
        }

        var parts = text.Split('.');
        if (parts.Length == 0 || !TryParsePart(parts[0], out var first))
        {
            return null;
        }

        int major;
        int minor = 0;
        int patch = 0;

        if (first == 1 && parts.Length >= 2 && TryParsePart(parts[1], out var legacyMajor))
        {
            major = legacyMajor;
            if (parts.Length >= 3 && TryParsePart(parts[2], out var legacyMinor))
            {
                minor = legacyMinor;
            }

            if (parts.Length >= 4 && TryParsePart(parts[3], out var legacyPatch))
            {
                patch = legacyPatch;
            }
        }
        else
        {
            major = first;
            if (parts.Length >= 2 && TryParsePart(parts[1], out var parsedMinor))
            {
                minor = parsedMinor;
            }

            if (parts.Length >= 3 && TryParsePart(parts[2], out var parsedPatch))
            {
                patch = parsedPatch;
            }
        }

        if (major <= 0)
        {
            return null;
        }

        return new JavaVersion(major, minor, patch, raw.Trim());
    }

    public bool IsAtLeast(int majorVersion) => Major >= majorVersion;

    public int CompareTo(JavaVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => Raw;

    private static bool TryParsePart(string part, out int value)
    {
        value = 0;
        var end = 0;
        while (end < part.Length && char.IsAsciiDigit(part[end]))
        {
            end++;
        }

        return end > 0 && int.TryParse(part.AsSpan(0, end), out value);
    }
}
