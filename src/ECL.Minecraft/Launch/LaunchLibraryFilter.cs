// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Launch;

/// <summary>
/// 启动期库过滤。
///
/// Cleanroom 使用 LWJGL 3 + <c>lwjglxx</c> 兼容层；从其它启动器导入的 1.12.2 整合包
/// version JSON 可能同时保留原版的 LWJGL 2 库。两套 <c>org.lwjgl</c> 同时进入 classpath 时，
/// <c>org.lwjgl.Sys</c> 会加载到不匹配的原生库，启动阶段报
/// <c>NoSuchMethodError: getPointer</c>。这里在 Cleanroom 启动时移除 LWJGL 2，
/// 保留 LWJGL 3、lwjglxx 与其它库；Forge/Vanilla 等仍按原样使用 LWJGL 2。
/// </summary>
public static class LaunchLibraryFilter
{
    public static IReadOnlyList<VersionLibrary> Apply(string? loader, IReadOnlyList<VersionLibrary> libraries)
    {
        if (libraries.Count == 0 ||
            !string.Equals(loader, LoaderKinds.Cleanroom, StringComparison.OrdinalIgnoreCase))
        {
            return libraries;
        }

        var filtered = libraries
            .Where(library => !IsLegacyLwjgl(library.Name))
            .ToArray();
        return filtered.Length == libraries.Count ? libraries : filtered;
    }

    /// <summary>
    /// 识别 LWJGL 2 的 Maven 坐标：
    /// <c>org.lwjgl.lwjgl:lwjgl/…</c>（1.12.2 原生形式）或 <c>org.lwjgl:lwjgl:2.x</c>。
    /// LWJGL 3 的 <c>org.lwjgl:lwjgl:3.x</c> 以及 natives classifier 不会被移除。
    /// </summary>
    public static bool IsLegacyLwjgl(string? mavenName)
    {
        if (string.IsNullOrWhiteSpace(mavenName))
        {
            return false;
        }

        var parts = mavenName.Split(':');
        if (parts.Length < 3)
        {
            return false;
        }

        var group = parts[0].Trim();
        var version = parts[2].Trim();
        if (string.Equals(group, "org.lwjgl.lwjgl", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(group, "org.lwjgl", StringComparison.OrdinalIgnoreCase) &&
               version.StartsWith("2.", StringComparison.Ordinal);
    }
}
