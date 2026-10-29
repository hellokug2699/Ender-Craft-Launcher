// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.CurseForge;

internal static class CurseForgeDownloads
{
    /// <summary>只接受 CurseForge 官方 CDN 的下载地址，避免 API 返回值被滥用为任意 URL。</summary>
    public static bool TryCreateUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!IsAllowedHost(parsed.Host))
        {
            return false;
        }

        if (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            parsed = new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        }

        uri = parsed;
        return true;
    }

    public static bool IsSafeFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName is not "." and not ".." &&
        string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) &&
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsAllowedHost(string host) =>
        string.Equals(host, "forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "curseforge.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".curseforge.com", StringComparison.OrdinalIgnoreCase);
}
