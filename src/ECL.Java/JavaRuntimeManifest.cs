// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

public sealed class JavaRuntimeIndexEntry
{
    public JavaRuntimeManifestReference? Manifest { get; set; }

    public JavaRuntimeVersionInfo? Version { get; set; }

    public JavaRuntimeAvailability? Availability { get; set; }
}

public sealed class JavaRuntimeManifestReference
{
    public string? Url { get; set; }

    public string? Sha1 { get; set; }

    public long? Size { get; set; }
}

public sealed class JavaRuntimeVersionInfo
{
    public string? Name { get; set; }

    public string? Released { get; set; }
}

public sealed class JavaRuntimeAvailability
{
    public int Group { get; set; }

    public int Progress { get; set; }
}

public sealed class JavaRuntimeManifest
{
    public Dictionary<string, JavaRuntimeFile> Files { get; set; } = new(StringComparer.Ordinal);
}

public sealed class JavaRuntimeFile
{
    public string Type { get; set; } = string.Empty;

    public bool Executable { get; set; }

    public string? Target { get; set; }

    public JavaRuntimeFileDownloads? Downloads { get; set; }
}

public sealed class JavaRuntimeFileDownloads
{
    public JavaRuntimeFileDownload? Raw { get; set; }
}

public sealed class JavaRuntimeFileDownload
{
    public string? Url { get; set; }

    public string? Sha1 { get; set; }

    public long? Size { get; set; }
}
