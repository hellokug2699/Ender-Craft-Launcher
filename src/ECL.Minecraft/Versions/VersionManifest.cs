// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Versions;

public sealed class VersionManifest
{
    public LatestVersions? Latest { get; set; }

    public List<VersionManifestEntry> Versions { get; set; } = [];
}

public sealed class LatestVersions
{
    public string? Release { get; set; }

    public string? Snapshot { get; set; }
}

public sealed class VersionManifestEntry
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string? Sha1 { get; set; }

    public long? Size { get; set; }

    public string? ReleaseTime { get; set; }
}

public sealed class AssetIndexFile
{
    public Dictionary<string, AssetObject> Objects { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AssetObject
{
    public string Hash { get; set; } = string.Empty;

    public long Size { get; set; }
}
