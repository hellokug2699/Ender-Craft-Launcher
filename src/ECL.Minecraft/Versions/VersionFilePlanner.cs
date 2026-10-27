// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;

namespace Ecl.Minecraft.Versions;

/// <summary>
/// 启动前需要校验的文件。Size/Sha1 为版本 JSON 中记录的期望值；
/// VerifyHash 表示该文件较小且关键，值得每次启动计算 SHA-1。
/// </summary>
public sealed record VersionFileCheck(string Path, long? Size, string? Sha1, bool VerifyHash);

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

    public static IReadOnlyList<VersionFileCheck> GetRequiredFileChecks(
        MinecraftVersion version,
        IAppPaths paths,
        string versionId)
    {
        var checks = new List<VersionFileCheck>
        {
            new(
                GetClientJarPath(paths, versionId),
                version.Downloads?.Client?.Size,
                version.Downloads?.Client?.Sha1,
                VerifyHash: true),
        };

        foreach (var artifact in PlatformLibrarySelector.GetDownloadArtifacts(PlanLibraries(version)))
        {
            checks.Add(new VersionFileCheck(
                GetLibraryPath(paths, artifact),
                artifact.Size,
                artifact.Sha1,
                VerifyHash: true));
        }

        if (version.AssetIndex is not null && !string.IsNullOrWhiteSpace(version.AssetIndex.Id))
        {
            checks.Add(new VersionFileCheck(
                GetAssetIndexPath(paths, version.AssetIndex.Id),
                version.AssetIndex.Size,
                version.AssetIndex.Sha1,
                VerifyHash: true));

            checks.AddRange(GetAssetObjectChecks(paths, version.AssetIndex.Id));
        }

        if (version.Logging?.Client?.File is { Id.Length: > 0 } loggingFile)
        {
            checks.Add(new VersionFileCheck(
                GetLoggingFilePath(paths, loggingFile.Id),
                loggingFile.Size,
                loggingFile.Sha1,
                VerifyHash: true));
        }

        return checks;
    }

    /// <summary>保留旧名称，返回需要存在的文件路径。</summary>
    public static IReadOnlyList<string> GetRequiredFiles(
        MinecraftVersion version,
        IAppPaths paths,
        string versionId) =>
        GetRequiredFileChecks(version, paths, versionId)
            .Select(check => check.Path)
            .ToArray();

    private static IEnumerable<VersionFileCheck> GetAssetObjectChecks(IAppPaths paths, string assetIndexId)
    {
        var indexPath = GetAssetIndexPath(paths, assetIndexId);
        if (!File.Exists(indexPath))
        {
            yield break;
        }

        AssetIndexFile? index;
        try
        {
            index = JsonSerializer.Deserialize<AssetIndexFile>(File.ReadAllText(indexPath), EclJson.Default);
        }
        catch (JsonException)
        {
            // 索引损坏由资源索引自身的哈希校验负责报告。
            yield break;
        }

        if (index is null)
        {
            yield break;
        }

        foreach (var asset in index.Objects.Values)
        {
            if (string.IsNullOrWhiteSpace(asset.Hash) || asset.Hash.Length < 2)
            {
                continue;
            }

            // 资源对象数量大、总量高；启动前检查大小与存在性即可，避免每次启动全量哈希。
            yield return new VersionFileCheck(
                GetAssetObjectPath(paths, asset.Hash),
                asset.Size,
                asset.Hash,
                VerifyHash: false);
        }
    }
}
