// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text.Json;
using Ecl.Core.Json;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Loaders;

/// <summary>
/// 从 Forge / NeoForge 安装器 jar 中提取版本 JSON 与 install_profile。
/// </summary>
public static class ForgeInstallerReader
{
    private const string OfficialLibrariesRoot = "https://libraries.minecraft.net/";

    public static ForgeInstallerContent Read(string installerPath, string mavenRoot)
    {
        using var archive = ZipFile.OpenRead(installerPath);

        var profileEntry = archive.GetEntry("install_profile.json")
                           ?? throw new InvalidDataException("安装器缺少 install_profile.json。");
        var profile = JsonSerializer.Deserialize<ForgeInstallProfile>(ReadText(profileEntry), EclJson.Default)
                      ?? throw new InvalidDataException("install_profile.json 无法解析。");

        var version = profile.VersionInfo;
        if (version is null)
        {
            var versionEntry = archive.GetEntry("version.json")
                               ?? throw new InvalidDataException("安装器缺少 version.json。");
            version = JsonSerializer.Deserialize<MinecraftVersion>(ReadText(versionEntry), EclJson.Default)
                      ?? throw new InvalidDataException("version.json 无法解析。");
        }

        if (string.IsNullOrWhiteSpace(version.MainClass))
        {
            throw new InvalidDataException("安装器 version.json 缺少 mainClass。");
        }

        ResolveLibraryUrls(version, profile, mavenRoot);
        return new ForgeInstallerContent(version, profile);
    }

    /// <summary>
    /// 旧版安装器的库条目有三种形式：
    /// 1. downloads.artifact.url 是完整文件 URL；
    /// 2. 只有 library.url（Maven 根地址或完整文件 URL）；
    /// 3. 什么都没有（旧 Forge 的 launchwrapper、lzma 等依赖）。
    /// 这里统一把地址补成完整文件 URL，避免 <see cref="LibraryRules.GetPrimaryArtifact"/> 再次拼接。
    /// </summary>
    public static void ResolveLibraryUrls(
        MinecraftVersion version,
        ForgeInstallProfile profile,
        string mavenRoot)
    {
        var root = mavenRoot.EndsWith('/') ? mavenRoot : mavenRoot + "/";

        foreach (var library in version.Libraries)
        {
            var artifact = library.Downloads?.Artifact;
            var path = artifact?.Path ?? MavenCoordinates.GetPath(library.Name);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var isForgeArtifact = IsLegacyForgeArtifact(library.Name);
            var relative = path.Replace('\\', '/');
            var isUniversal = relative.EndsWith("-universal.jar", StringComparison.OrdinalIgnoreCase);
            if (profile.Spec < 1 &&
                isForgeArtifact &&
                relative.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                !isUniversal)
            {
                // Forge 1.12.2 及更早版本只发布 -universal.jar；安装器的 path 却是无后缀路径。
                var universalPath = relative[..^".jar".Length] + "-universal.jar";
                var universalUrl = CombineUrl(root, universalPath);
                if (artifact is not null)
                {
                    artifact.Url = universalUrl;
                }
                else
                {
                    library.Url = universalUrl;
                }

                continue;
            }

            var currentUrl = artifact?.Url;
            if (string.IsNullOrWhiteSpace(currentUrl))
            {
                currentUrl = library.Url;
            }

            // 旧版 Forge 的依赖（launchwrapper、lzma 等）默认来自 Mojang 库；
            // 只有 Forge/NeoForge 自己的产物才使用加载器 Maven 根。
            var baseUrl = isForgeArtifact ? root : OfficialLibrariesRoot;
            var url = string.IsNullOrWhiteSpace(currentUrl)
                ? CombineUrl(baseUrl, relative)
                : LibraryRules.ResolveArtifactUrl(currentUrl, relative);

            if (artifact is not null)
            {
                artifact.Url = url;
            }
            else
            {
                library.Url = url;
            }
        }
    }

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        var normalizedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        return new Uri(new Uri(normalizedBase), relativePath.Replace('\\', '/')).ToString();
    }

    private static bool IsLegacyForgeArtifact(string name) =>
        name.StartsWith("net.minecraftforge:forge:", StringComparison.Ordinal) ||
        name.StartsWith("net.neoforged:forge:", StringComparison.Ordinal);

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
