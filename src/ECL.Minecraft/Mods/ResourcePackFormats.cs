// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Minecraft.Mods;

/// <summary>
/// <c>pack.mcmeta</c> 的 <c>pack_format</c> 与 Minecraft 版本的官方对照。
/// <para>
/// 只用于在资源包页面展示“版本范围”列（resource-library-template.md §3.1），
/// 不用于判断兼容性：兼容状态只来自文件自身的可读性、<c>pack.mcmeta</c> 的声明，
/// 以及游戏写入 <c>incompatibleResourcePacks</c> 的记录。
/// </para>
/// <para>
/// 数据是 Mojang 客户端版本历史中 pack_format 的实际取值（1.6.1–1.21.4）。
/// 未收录的格式（更早或更新的版本）返回 <c>null</c>，页面显示未知，不按经验外推。
/// 新版本引入新格式时须先核实官方值再补充此表。
/// </para>
/// </summary>
public static class ResourcePackFormats
{
    /// <summary>一个格式覆盖的 Minecraft 版本范围（包含两端）。</summary>
    public readonly record struct VersionRange(string Minimum, string Maximum)
    {
        public bool IsSingleVersion => string.Equals(Minimum, Maximum, StringComparison.Ordinal);
    }

    private static readonly (int Format, string Minimum, string Maximum)[] OfficialFormats =
    [
        (1, "1.6.1", "1.8.9"),
        (2, "1.9", "1.10.2"),
        (3, "1.11", "1.12.2"),
        (4, "1.13", "1.14.4"),
        (5, "1.15", "1.16.1"),
        (6, "1.16.2", "1.16.5"),
        (7, "1.17", "1.17.1"),
        (8, "1.18", "1.18.2"),
        (9, "1.19", "1.19.2"),
        (12, "1.19.3", "1.19.3"),
        (13, "1.19.4", "1.19.4"),
        (15, "1.20", "1.20.1"),
        (18, "1.20.2", "1.20.2"),
        (22, "1.20.3", "1.20.4"),
        (32, "1.20.5", "1.20.6"),
        (34, "1.21", "1.21.1"),
        (42, "1.21.2", "1.21.3"),
        (46, "1.21.4", "1.21.4"),
    ];

    /// <summary>单个 pack_format 覆盖的版本范围；未收录时返回 <c>null</c>。</summary>
    public static VersionRange? Resolve(int format)
    {
        foreach (var (candidate, minimum, maximum) in OfficialFormats)
        {
            if (candidate == format)
            {
                return new VersionRange(minimum, maximum);
            }
        }

        return null;
    }

    /// <summary>
    /// <c>pack.mcmeta</c> 声明的格式范围对应的版本范围：只有两端都能解析时才返回结果，
    /// 否则返回 <c>null</c>（页面改为只显示声明的格式数字）。
    /// </summary>
    public static VersionRange? Resolve(PackFormatRange formats)
    {
        var minimum = Resolve(formats.MinInclusive);
        var maximum = Resolve(formats.MaxInclusive);
        if (minimum is null || maximum is null)
        {
            return null;
        }

        return new VersionRange(minimum.Value.Minimum, maximum.Value.Maximum);
    }
}
