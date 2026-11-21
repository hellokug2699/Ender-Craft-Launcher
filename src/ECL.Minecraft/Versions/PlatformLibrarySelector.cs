// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Versions;

public sealed record LibraryPlanItem(
    VersionLibrary Library,
    VersionLibraryArtifact? PrimaryArtifact,
    VersionLibraryArtifact? NativeArtifact,
    bool IsNativeOnly);

/// <summary>
/// 按当前平台把 version JSON 的库列表整理成启动计划。
///
/// 规范化（2026-11-21）：同一构件（group:artifact:classifier）只保留**最高版本**。
/// 从其它启动器导入的整合包 JSON 常把原版库与加载器库合并成一张列表，
/// 同一个构件会出现多个版本（例如 <c>log4j-api:2.8.1</c> 与 <c>log4j-api:2.26.0</c>）。
/// Java 类路径只有第一个同名类生效，旧版本先出现会遮蔽新版本并抛
/// <c>NoSuchMethodError</c>（Cleanroom 0.6 的 <c>log4j-slf4j2-impl</c> 就因此失败），
/// 因此必须在生成类路径、提取 natives 与校验文件之前统一去重。
/// </summary>
public static class PlatformLibrarySelector
{
    public static IReadOnlyList<LibraryPlanItem> Plan(
        IEnumerable<VersionLibrary> libraries,
        string osName,
        string osArch,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        var allowed = DeduplicateVersions(
            libraries.Where(library => LibraryRules.IsAllowed(library, osName, osArch, features)).ToList());

        var selectedNativeLibraries = new HashSet<VersionLibrary>();
        var modernNatives = allowed.Where(LibraryRules.IsModernNativeClassifier);

        foreach (var group in modernNatives.GroupBy(
                     library => MavenCoordinates.GetBaseName(library.Name) ?? library.Name,
                     StringComparer.Ordinal))
        {
            var best = group
                .Select(library => (
                    Library: library,
                    Priority: LibraryRules.NativeClassifierPriority(
                        MavenCoordinates.GetClassifier(library.Name)!,
                        osName,
                        osArch)))
                .Where(item => item.Priority > 0)
                .OrderByDescending(item => item.Priority)
                .FirstOrDefault();

            if (best.Library is not null)
            {
                selectedNativeLibraries.Add(best.Library);
            }
        }

        var result = new List<LibraryPlanItem>(allowed.Count);
        foreach (var library in allowed)
        {
            if (LibraryRules.IsModernNativeClassifier(library))
            {
                if (!selectedNativeLibraries.Contains(library))
                {
                    continue;
                }

                var artifact = LibraryRules.GetPrimaryArtifact(library);
                result.Add(new LibraryPlanItem(library, artifact, artifact, IsNativeOnly: true));
                continue;
            }

            result.Add(new LibraryPlanItem(
                library,
                LibraryRules.GetPrimaryArtifact(library),
                LibraryRules.GetNativeArtifact(library, osName, osArch),
                IsNativeOnly: false));
        }

        return result;
    }

    public static IReadOnlyList<VersionLibraryArtifact> GetDownloadArtifacts(IEnumerable<LibraryPlanItem> plan)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<VersionLibraryArtifact>();

        foreach (var item in plan)
        {
            foreach (var artifact in new[] { item.PrimaryArtifact, item.NativeArtifact })
            {
                if (artifact?.Path is null || !seen.Add(artifact.Path))
                {
                    continue;
                }

                result.Add(artifact);
            }
        }

        return result;
    }

    public static IReadOnlyList<VersionLibraryArtifact> GetClasspathArtifacts(IEnumerable<LibraryPlanItem> plan) =>
        plan.Where(item => !item.IsNativeOnly)
            .Select(item => item.PrimaryArtifact)
            .Where(artifact => artifact?.Path is not null)
            .Select(artifact => artifact!)
            .ToArray();

    /// <summary>
    /// 同一构件只保留最高版本，其余丢弃；其它构件与顺序保持不变。
    /// 无法解析坐标的条目原样保留，避免因元数据异常丢库。
    /// </summary>
    private static List<VersionLibrary> DeduplicateVersions(List<VersionLibrary> libraries)
    {
        if (libraries.Count < 2)
        {
            return libraries;
        }

        var winners = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < libraries.Count; index++)
        {
            var key = MavenCoordinates.GetArtifactKey(libraries[index].Name);
            if (key is null)
            {
                continue;
            }

            if (!winners.TryGetValue(key, out var winnerIndex) ||
                MavenVersionComparer.Compare(
                    MavenCoordinates.GetVersion(libraries[index].Name),
                    MavenCoordinates.GetVersion(libraries[winnerIndex].Name)) > 0)
            {
                winners[key] = index;
            }
        }

        if (winners.Count == libraries.Count)
        {
            return libraries;
        }

        var kept = new List<VersionLibrary>(winners.Count);
        var keptIndexes = new HashSet<int>(winners.Values);
        for (var index = 0; index < libraries.Count; index++)
        {
            if (MavenCoordinates.GetArtifactKey(libraries[index].Name) is null || keptIndexes.Contains(index))
            {
                kept.Add(libraries[index]);
            }
        }

        return kept;
    }
}
