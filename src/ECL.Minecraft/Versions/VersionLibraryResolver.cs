// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Versions;

/// <summary>
/// 合并型版本 JSON（带 <c>patches</c>）的库顺序归一化。
///
/// 背景：其它启动器安装 Cleanroom/Forge 整合包时，会把「原版段（<c>game</c>）」与
/// 「加载器段（例如 <c>cleanroom</c>）」合并成顶层 <c>libraries</c>，但合并结果按段声明顺序
/// 排列，**原版段在前**；真正的段信息留在 <c>patches</c> 里（带 <c>priority</c>）。
/// 启动器直接用合并数组会复现「旧库先进入类路径」的问题：
///
/// - 同一构件多版本（如 <c>log4j-api:2.8.1</c> 与 <c>2.26.0</c>）——<see cref="PlatformLibrarySelector"/> 已按
///   最高版本去重，但那只覆盖「坐标完全相同」的情况。
/// - 同一 Java 包、不同构件名（如原版 <c>com.ibm.icu:icu4j-core-mojang:51.2</c> 与加载器
///   <c>com.ibm.icu:icu4j:78.3</c>）——坐标去重抓不到，只有让加载器段排在前面才能命中正确的类。
///
/// 这里按 <c>priority</c> 从高到低重建顺序（同优先级时 <c>game</c> 基础段垫底），
/// 并让高优先段在构件冲突时获胜，与其它启动器「加载器段覆盖原版段」的语义一致。
/// 顶层列表中未被任何段覆盖的条目保持在末尾，避免它们反过来遮蔽加载器库。
///
/// 没有 <c>patches</c> 的版本（ECL 自己安装的实例、官方版本 JSON）原样返回，
/// 由 <see cref="PlatformLibrarySelector"/> 继续按最高版本去重。
/// </summary>
public static class VersionLibraryResolver
{
    /// <summary>原版 1.12.2 合并型 JSON 中基础段的 id。</summary>
    private const string BasePatchId = "game";

    public static IReadOnlyList<VersionLibrary> Resolve(MinecraftVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (version.Patches is not { Count: > 0 } patches)
        {
            return version.Libraries;
        }

        var segments = patches
            .Where(patch => patch.Libraries is { Count: > 0 })
            .Select((patch, index) => new Segment(patch, index))
            .OrderByDescending(segment => segment.Patch.Priority)
            .ThenBy(segment => IsBasePatch(segment.Patch) ? 1 : 0)
            .ThenBy(segment => segment.Index)
            .ToList();

        if (segments.Count == 0)
        {
            return version.Libraries;
        }

        var entries = new List<Entry>();
        var coveredKeys = new HashSet<string>(StringComparer.Ordinal);
        var coveredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            foreach (var library in segments[segmentIndex].Patch.Libraries)
            {
                var key = MavenCoordinates.GetArtifactKey(library.Name);
                if (key is not null)
                {
                    coveredKeys.Add(key);
                }
                else
                {
                    // 无法解析坐标（例如本地 jar 的裸文件名）时按名字判断顶层条目是否已在本段出现。
                    coveredNames.Add(library.Name.Trim());
                }

                entries.Add(new Entry(library, key, segmentIndex));
            }
        }

        // 没有被任何段覆盖的顶层条目放在最后：它们通常是合并器追加的补充库，
        // 位置在后才不会遮蔽加载器段的同名类。
        var tailSegment = segments.Count;
        foreach (var library in version.Libraries)
        {
            var key = MavenCoordinates.GetArtifactKey(library.Name);
            if (key is not null ? coveredKeys.Contains(key) : coveredNames.Contains(library.Name.Trim()))
            {
                continue;
            }

            entries.Add(new Entry(library, key, tailSegment));
        }

        var winners = PickWinners(entries);
        var result = new List<VersionLibrary>(winners.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Key is null || winners[entry.Key] == index)
            {
                result.Add(entry.Library);
            }
        }

        return result;
    }

    /// <summary>
    /// 每个构件保留一个条目：段优先级高者获胜；同一段内版本高者获胜。
    /// 无法解析坐标的条目（<c>Key</c> 为 null）不参与去重，原样保留。
    /// </summary>
    private static Dictionary<string, int> PickWinners(List<Entry> entries)
    {
        var winners = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Key is null)
            {
                continue;
            }

            if (!winners.TryGetValue(entry.Key, out var incumbentIndex))
            {
                winners[entry.Key] = index;
                continue;
            }

            var incumbent = entries[incumbentIndex];
            if (entry.Segment < incumbent.Segment)
            {
                winners[entry.Key] = index;
                continue;
            }

            if (entry.Segment == incumbent.Segment &&
                MavenVersionComparer.Compare(
                    MavenCoordinates.GetVersion(entry.Library.Name),
                    MavenCoordinates.GetVersion(incumbent.Library.Name)) > 0)
            {
                winners[entry.Key] = index;
            }
        }

        return winners;
    }

    private static bool IsBasePatch(VersionPatch patch) =>
        string.Equals(patch.Id, BasePatchId, StringComparison.OrdinalIgnoreCase);

    private readonly record struct Segment(VersionPatch Patch, int Index);

    private readonly record struct Entry(VersionLibrary Library, string? Key, int Segment);
}
