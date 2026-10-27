// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ecl.Minecraft.Versions;

public sealed partial class MinecraftVersion
{
    public string Id { get; set; } = string.Empty;

    public string? Type { get; set; }

    public string? MainClass { get; set; }

    public string? InheritsFrom { get; set; }

    public string? Assets { get; set; }

    public string? MinecraftArguments { get; set; }

    public VersionArguments? Arguments { get; set; }

    public VersionAssetIndex? AssetIndex { get; set; }

    public VersionJavaRequirement? JavaVersion { get; set; }

    public VersionDownloads? Downloads { get; set; }

    public VersionLogging? Logging { get; set; }

    public List<VersionLibrary> Libraries { get; set; } = [];

    public static bool IsValidVersionId(string? versionId) =>
        !string.IsNullOrWhiteSpace(versionId) && VersionIdRegex().IsMatch(versionId);

    [GeneratedRegex("^[0-9A-Za-z._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionIdRegex();
}

public sealed class VersionArguments
{
    public List<JsonElement> Game { get; set; } = [];

    public List<JsonElement> Jvm { get; set; } = [];
}

public sealed class VersionAssetIndex
{
    public string Id { get; set; } = string.Empty;

    public string? Url { get; set; }

    public string? Sha1 { get; set; }

    public long? Size { get; set; }

    public long? TotalSize { get; set; }
}

public sealed class VersionJavaRequirement
{
    public string? Component { get; set; }

    public int MajorVersion { get; set; }
}

public sealed class VersionDownloads
{
    public VersionDownload? Client { get; set; }

    public VersionDownload? Server { get; set; }
}

public sealed class VersionDownload
{
    public string? Url { get; set; }

    public string? Sha1 { get; set; }

    public long? Size { get; set; }
}

public sealed class VersionLogging
{
    public VersionLoggingClient? Client { get; set; }
}

public sealed class VersionLoggingClient
{
    public string? Argument { get; set; }

    public string? Type { get; set; }

    public VersionLoggingFile? File { get; set; }
}

public sealed class VersionLoggingFile
{
    public string Id { get; set; } = string.Empty;

    public string? Url { get; set; }

    public string? Sha1 { get; set; }

    public long? Size { get; set; }
}

public sealed class VersionLibrary
{
    public string Name { get; set; } = string.Empty;

    public string? Url { get; set; }

    public string? Sha1 { get; set; }

    public long? Size { get; set; }

    public VersionLibraryDownloads? Downloads { get; set; }

    public Dictionary<string, string>? Natives { get; set; }

    public VersionLibraryExtract? Extract { get; set; }

    public List<VersionRule>? Rules { get; set; }
}

public sealed class VersionLibraryDownloads
{
    public VersionLibraryArtifact? Artifact { get; set; }

    public Dictionary<string, VersionLibraryArtifact>? Classifiers { get; set; }
}

public sealed class VersionLibraryArtifact
{
    public string? Path { get; set; }

    public string? Url { get; set; }

    public string? Sha1 { get; set; }

    public long? Size { get; set; }
}

public sealed class VersionLibraryExtract
{
    public List<string>? Exclude { get; set; }
}

public sealed class VersionRule
{
    public string Action { get; set; } = "allow";

    public VersionRuleOs? Os { get; set; }

    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class VersionRuleOs
{
    public string? Name { get; set; }

    public string? Arch { get; set; }

    public string? Version { get; set; }
}
