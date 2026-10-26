// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Versions;

public static class VersionFilePlanner
{
    public static string GetClientJarPath(IAppPaths paths, string versionId) =>
        Path.Combine(paths.VersionsDirectory, versionId, $"{versionId}.jar");

    public static string GetVersionJsonPath(IAppPaths paths, string versionId) =>
        Path.Combine(paths.VersionsDirectory, versionId, $"{versionId}.json");

    public static string GetAssetIndexPath(IAppPaths paths, string assetIndexId) =>
        Path.Combine(paths.AssetsDirectory, "indexes", $"{assetIndexId}.json");

    public static string GetLoggingFilePath(IAppPaths paths, string fileId) =>
        Path.Combine(paths.AssetsDirectory, "logging", fileId);

    public static string GetAssetObjectPath(IAppPaths paths, string hash)
    {
        if (hash.Length < 2)
        {
            throw new ArgumentException("资源哈希长度不足。", nameof(hash));
        }

        return Path.Combine(paths.AssetsDirectory, "objects", hash[..2], hash);
    }

    public static string GetLibraryPath(IAppPaths paths, VersionLibraryArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.Path))
        {
            throw new ArgumentException("库文件缺少路径。", nameof(artifact));
        }

        return Path.Combine(paths.LibrariesDirectory, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
    }

    public static IReadOnlyList<LibraryPlanItem> PlanLibraries(MinecraftVersion version) =>
        PlatformLibrarySelector.Plan(version.Libraries, LibraryRules.CurrentOsName, LibraryRules.CurrentArch);

    public static IReadOnlyList<string> GetRequiredFiles(MinecraftVersion version, IAppPaths paths, string versionId)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GetClientJarPath(paths, versionId),
        };

        foreach (var artifact in PlatformLibrarySelector.GetDownloadArtifacts(PlanLibraries(version)))
        {
            files.Add(GetLibraryPath(paths, artifact));
        }

        if (version.AssetIndex is not null && !string.IsNullOrWhiteSpace(version.AssetIndex.Id))
        {
            files.Add(GetAssetIndexPath(paths, version.AssetIndex.Id));
        }

        if (version.Logging?.Client?.File is { Id.Length: > 0 } loggingFile)
        {
            files.Add(GetLoggingFilePath(paths, loggingFile.Id));
        }

        return files.ToArray();
    }
}
