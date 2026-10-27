// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core.Json;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Loaders;

public sealed record CleanroomInstallerContent(
    MinecraftVersion Version,
    IReadOnlyDictionary<string, string> Repositories);

/// <summary>
/// 从 Cleanroom 安装器 jar 中提取完整版本 JSON 与仓库映射。
/// 安装器本身是 Forge 风格的 zip，包含 version.json 与 install_profile.json。
/// </summary>
public static class CleanroomInstallerReader
{
    public static CleanroomInstallerContent Read(string installerPath)
    {
        using var archive = ZipFile.OpenRead(installerPath);

        var versionEntry = archive.GetEntry("version.json")
                           ?? throw new InvalidDataException("Cleanroom 安装器缺少 version.json。");
        var version = JsonSerializer.Deserialize<MinecraftVersion>(ReadText(versionEntry), EclJson.Default)
                      ?? throw new InvalidDataException("Cleanroom version.json 无法解析。");
        if (string.IsNullOrWhiteSpace(version.MainClass))
        {
            throw new InvalidDataException("Cleanroom version.json 缺少 mainClass。");
        }

        var repositories = new Dictionary<string, string>(StringComparer.Ordinal);
        var profileEntry = archive.GetEntry("install_profile.json");
        if (profileEntry is not null)
        {
            var profile = JsonSerializer.Deserialize<CleanroomInstallProfile>(ReadText(profileEntry), EclJson.Default);
            if (profile?.Repositories is not null)
            {
                foreach (var pair in profile.Repositories)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        repositories[pair.Key] = pair.Value;
                    }
                }
            }
        }

        ResolveLibraryUrls(version, repositories);
        return new CleanroomInstallerContent(version, repositories);
    }

    public static void ResolveLibraryUrls(
        MinecraftVersion version,
        IReadOnlyDictionary<string, string> repositories)
    {
        foreach (var library in version.Libraries)
        {
            var artifact = library.Downloads?.Artifact;
            var path = artifact?.Path ?? MavenCoordinates.GetPath(library.Name);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var currentUrl = artifact?.Url;
            if (string.IsNullOrWhiteSpace(currentUrl))
            {
                currentUrl = library.Url;
            }

            if (!string.IsNullOrWhiteSpace(currentUrl))
            {
                continue;
            }

            var repository = FindRepository(library.Name, repositories);
            if (repository is null)
            {
                continue;
            }

            var url = new Uri(new Uri(repository.EndsWith('/') ? repository : repository + "/"), path).ToString();
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

    private static string? FindRepository(
        string libraryName,
        IReadOnlyDictionary<string, string> repositories)
    {
        var separator = libraryName.IndexOf(':');
        var group = separator > 0 ? libraryName[..separator] : libraryName;

        return repositories
            .Where(pair =>
                string.Equals(group, pair.Key, StringComparison.Ordinal) ||
                group.StartsWith(pair.Key + ".", StringComparison.Ordinal))
            .OrderByDescending(pair => pair.Key.Length)
            .Select(pair => pair.Value)
            .FirstOrDefault();
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class CleanroomInstallProfile
    {
        [JsonPropertyName("repositories")]
        public Dictionary<string, string>? Repositories { get; set; }
    }
}
