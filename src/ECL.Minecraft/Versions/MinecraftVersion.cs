// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace Ecl.Minecraft.Versions;

public sealed partial class MinecraftVersion
{
    public string Id { get; set; } = string.Empty;

    public string? Type { get; set; }

    public string? MainClass { get; set; }

    public string? InheritsFrom { get; set; }

    public string? Assets { get; set; }

    public VersionAssetIndex? AssetIndex { get; set; }

    public VersionJavaRequirement? JavaVersion { get; set; }

    public List<VersionLibrary> Libraries { get; set; } = [];

    public static bool IsValidVersionId(string? versionId) =>
        !string.IsNullOrWhiteSpace(versionId) && VersionIdRegex().IsMatch(versionId);

    [GeneratedRegex("^[0-9A-Za-z._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionIdRegex();
}

public sealed class VersionAssetIndex
{
    public string Id { get; set; } = string.Empty;
}

public sealed class VersionJavaRequirement
{
    public string? Component { get; set; }

    public int MajorVersion { get; set; }
}

public sealed class VersionLibrary
{
    public string Name { get; set; } = string.Empty;
}
