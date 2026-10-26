// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Download;

public enum DownloadSourceMode
{
    OfficialOnly,
    OfficialFirst,
    MirrorFirst,
    MirrorOnly,
}

public static class DownloadSourceModeParser
{
    public static DownloadSourceMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "official-only" or "official" => DownloadSourceMode.OfficialOnly,
        "official-first" => DownloadSourceMode.OfficialFirst,
        "mirror-first" or "bmclapi-first" => DownloadSourceMode.MirrorFirst,
        "mirror-only" or "bmclapi" => DownloadSourceMode.MirrorOnly,
        _ => DownloadSourceMode.OfficialFirst,
    };

    public static string ToOptionValue(DownloadSourceMode mode) => mode switch
    {
        DownloadSourceMode.OfficialOnly => "official-only",
        DownloadSourceMode.OfficialFirst => "official-first",
        DownloadSourceMode.MirrorFirst => "mirror-first",
        _ => "mirror-only",
    };
}
