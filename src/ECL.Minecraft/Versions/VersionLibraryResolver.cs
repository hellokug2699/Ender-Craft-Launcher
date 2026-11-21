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
/// 同一构件的候选先按 <c>rules</c> 筛出当前平台可用的条目再择优：被 rules 排除的高版本
/// 不能顶掉可用版本（否则平台过滤会把整个构件丢光）。顶层列表中未被任何段覆盖的条目保持在末尾，
/// 避免它们反过来遮蔽加载器库。
///
/// 没有 <c>patches</c> 的版本（ECL 自己安装的实例、官方版本 JSON）原样返回，
/// 由 <see cref="PlatformLibrarySelector"/> 继续按最高版本去重。
/// </summary>
public static class VersionLibraryResolver
{
    /// <summary>原版 1.12.2 合并型 JSON 中基础段的 id。</summary>
    private const string BasePatchId = "game";

    public static IReadOnlyList<VersionLibrary> Resolve(MinecraftVersion version) =>
        Resolve(version, LibraryRules.CurrentOsName, LibraryRules.CurrentArch);

    /// <summary>
    /// 平台参数用于判断候选条目是否被 <c>rules</c> 允许。同一构件存在多个条目时
    /// 必须只在「当前平台可用」的候选里择优，否则被 rules 排除的高版本会顶掉可用的低版本，
    /// 随后平台过滤又把这条唯一候选丢掉，整个构件从类路径消失（实测
    /// <c>com.mojang:text2speech</c>：通用 <c>1.10.3</c> + 仅 <c>osx-arm64</c> 的 <c>1.11.3</c>，
    /// Windows 上会让 <c>com.mojang.text2speech.Narrator</c> 缺失并在初始化末尾
    /// <c>NoClassDefFoundError</c>）。
    /// </summary>
    public static IReadOnlyList<VersionLibrary> Resolve(MinecraftVersion version, string osName, string osArch)
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

                entries.Add(CreateEntry(library, key, segmentIndex, osName, osArch));
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

            entries.Add(CreateEntry(library, key, tailSegment, osName, osArch));
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
    /// 每个构件保留一个条目，择优顺序：
    /// 1. 当前平台允许的条目优先（<c>rules</c> 判定）；
    /// 2. 段优先级高者优先（加载器段覆盖原版段，加载器故意固定旧版本时也要生效）；
    /// 3. 同一段内版本高者优先。
    /// 全部候选都被 rules 排除时仍按 2/3 保留一条，交给后续平台过滤统一丢弃，
    /// 避免去重结果依赖过滤顺序。无法解析坐标的条目不参与去重，原样保留。
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

            if (IsBetterCandidate(entry, entries[incumbentIndex]))
            {
                winners[entry.Key] = index;
            }
        }

        return winners;
    }

    private static bool IsBetterCandidate(Entry candidate, Entry incumbent)
    {
        if (candidate.Allowed != incumbent.Allowed)
        {
            return candidate.Allowed;
        }

        if (candidate.Segment != incumbent.Segment)
        {
            return candidate.Segment < incumbent.Segment;
        }

        return MavenVersionComparer.Compare(
            MavenCoordinates.GetVersion(candidate.Library.Name),
            MavenCoordinates.GetVersion(incumbent.Library.Name)) > 0;
    }

    private static Entry CreateEntry(
        VersionLibrary library,
        string? key,
        int segment,
        string osName,
        string osArch) => new(
        library,
        key,
        segment,
        LibraryRules.IsAllowed(library, osName, osArch));

    private static bool IsBasePatch(VersionPatch patch) =>
        string.Equals(patch.Id, BasePatchId, StringComparison.OrdinalIgnoreCase);

    private readonly record struct Segment(VersionPatch Patch, int Index);

    private readonly record struct Entry(VersionLibrary Library, string? Key, int Segment, bool Allowed);
}
