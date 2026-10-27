// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;

namespace Ecl.Download;

/// <summary>
/// 把 Mojang 官方 URL 映射为 BMCLAPI 镜像 URL，并按用户选择的策略生成候选顺序。
/// </summary>
public sealed class DownloadSourceResolver(ISettingsService settings)
{
    public const string OfficialManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string DefaultMirrorRoot = "https://bmclapi2.bangbang93.com";
    public const string MavenCentralMirrorRoot = "https://maven.aliyun.com/repository/public";

    public string MirrorRoot { get; init; } = DefaultMirrorRoot;

    public DownloadSourceMode Mode => DownloadSourceModeParser.Parse(settings.Get<string>("network.downloadSource"));

    public IReadOnlyList<Uri> GetManifestCandidates() => GetCandidates(new Uri(OfficialManifestUrl));

    public IReadOnlyList<Uri> GetCandidates(string officialUrl) =>
        Uri.TryCreate(officialUrl, UriKind.Absolute, out var uri) ? GetCandidates(uri) : [];

    public IReadOnlyList<Uri> GetCandidates(Uri officialUrl)
    {
        if (!officialUrl.IsAbsoluteUri)
        {
            return [];
        }

        var mirror = MapToMirror(officialUrl);

        return Mode switch
        {
            DownloadSourceMode.OfficialOnly => [officialUrl],
            DownloadSourceMode.OfficialFirst => mirror is null ? [officialUrl] : [officialUrl, mirror],
            DownloadSourceMode.MirrorFirst => mirror is null ? [officialUrl] : [mirror, officialUrl],
            DownloadSourceMode.MirrorOnly => mirror is null ? [] : [mirror],
            _ => [officialUrl],
        };
    }

    public Uri? MapToMirror(Uri officialUrl)
    {
        if (!officialUrl.IsAbsoluteUri)
        {
            return null;
        }

        var host = officialUrl.Host.ToLowerInvariant();
        var root = MirrorRoot.TrimEnd('/');
        var path = officialUrl.AbsolutePath;

        if (host == "bmclapi2.bangbang93.com")
        {
            return officialUrl;
        }

        if (host == "resources.download.minecraft.net")
        {
            return new Uri(root + "/assets" + path);
        }

        if (host == "libraries.minecraft.net")
        {
            return new Uri(root + "/libraries" + path);
        }

        if (host is "launchermeta.mojang.com" or "piston-meta.mojang.com" or "piston-data.mojang.com" or "launcher.mojang.com")
        {
            return new Uri(root + path);
        }

        if (host is "repo.maven.apache.org" or "repo1.maven.org")
        {
            // Maven Central 没有 BMCLAPI 映射，使用阿里云公共仓库作为镜像。
            var mavenPath = path.StartsWith("/maven2/", StringComparison.OrdinalIgnoreCase)
                ? path["/maven2".Length..]
                : path;
            return new Uri(MavenCentralMirrorRoot + mavenPath);
        }

        if (host == "maven.minecraftforge.net")
        {
            return new Uri(root + "/maven" + path);
        }

        if (host == "maven.neoforged.net")
        {
            // BMCLAPI 的 Maven 镜像不带 /releases 前缀。
            var neoforgePath = path.StartsWith("/releases/", StringComparison.OrdinalIgnoreCase)
                ? path["/releases".Length..]
                : path;
            return new Uri(root + "/maven" + neoforgePath);
        }

        return null;
    }
}
