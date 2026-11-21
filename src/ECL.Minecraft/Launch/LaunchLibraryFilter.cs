// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Launch;

/// <summary>
/// 启动期库规范化。导入自其它启动器的 1.12.2 整合包 version JSON 往往把原版库与加载器库
/// 合并成一张列表，同一个 Java 包的多个版本会同时进入 classpath；Java 只加载第一个同名类，
/// 于是旧版本遮蔽新版本并抛 <c>NoSuchMethodError</c>。这里在生成 classpath 之前剔除已知的
/// 「已被替代的同包构件」：
///
/// 1. Cleanroom：移除 LWJGL 2（保留 LWJGL 3 + <c>lwjglxx</c>）。两套 <c>org.lwjgl</c> 同时存在时
///    <c>org.lwjgl.Sys</c> 会加载到不匹配的原生库并报 <c>NoSuchMethodError: getPointer</c>。
/// 2. 所有加载器：当 <c>net.java.dev.jna:jna-platform</c> 存在时移除 JNA 3.x 的旧构件名
///    <c>net.java.dev.jna:platform</c>。两者提供同一批 <c>com.sun.jna.platform.*</c> 类，
///    旧构件先出现会让 Cleanroom 的 Taskbar API 等调用方报
///    <c>NoSuchMethodError: Guid$GUID.&lt;init&gt;(String)</c>。
///
/// 同一构件不同版本（例如 <c>log4j-api:2.8.1</c> 与 <c>2.26.0</c>）由
/// <see cref="PlatformLibrarySelector"/> 统一按最高版本去重，不在这里处理。
/// </summary>
public static class LaunchLibraryFilter
{
    private const string LegacyJnaPlatformArtifactKey = "net.java.dev.jna:platform:";
    private const string JnaPlatformArtifactKey = "net.java.dev.jna:jna-platform:";

    public static IReadOnlyList<VersionLibrary> Apply(string? loader, IReadOnlyList<VersionLibrary> libraries)
    {
        if (libraries.Count == 0)
        {
            return libraries;
        }

        IReadOnlyList<VersionLibrary> result = libraries;
        if (string.Equals(loader, LoaderKinds.Cleanroom, StringComparison.OrdinalIgnoreCase))
        {
            result = RemoveWhere(result, library => IsLegacyLwjgl(library.Name));
        }

        result = DropSupersededJnaPlatform(result);
        return ReferenceEquals(result, libraries) ? libraries : result;
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

    /// <summary>JNA 3.x 的旧构件名 <c>net.java.dev.jna:platform</c>（JNA 4 起改名为 <c>jna-platform</c>）。</summary>
    public static bool IsLegacyJnaPlatform(string? mavenName) =>
        MavenCoordinates.GetArtifactKey(mavenName) == LegacyJnaPlatformArtifactKey;

    public static bool IsJnaPlatform(string? mavenName) =>
        MavenCoordinates.GetArtifactKey(mavenName) == JnaPlatformArtifactKey;

    /// <summary>只有当替代构件存在时才丢弃旧构件，避免把唯一可用的库删掉。</summary>
    private static IReadOnlyList<VersionLibrary> DropSupersededJnaPlatform(IReadOnlyList<VersionLibrary> libraries)
    {
        if (!libraries.Any(library => IsJnaPlatform(library.Name)))
        {
            return libraries;
        }

        return RemoveWhere(libraries, library => IsLegacyJnaPlatform(library.Name));
    }

    private static IReadOnlyList<VersionLibrary> RemoveWhere(
        IReadOnlyList<VersionLibrary> libraries,
        Func<VersionLibrary, bool> predicate)
    {
        var filtered = libraries.Where(library => !predicate(library)).ToArray();
        return filtered.Length == libraries.Count ? libraries : filtered;
    }
}
