// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;

namespace Ecl.Core;

/// <summary>
/// 构建信息。构建时间由 Directory.Build.props 写入程序集元数据，
/// 分发多个二进制时可据此区分版本。
/// </summary>
public static class BuildInfo
{
    /// <summary>形如 <c>2026-10-31 13:40</c>；元数据缺失时为 <see langword="null"/>。</summary>
    public static string? BuildTime { get; } = ReadBuildTime();

    /// <summary>当前构建的 SemVer 版本，保留预发布标识。</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var entry = Assembly.GetEntryAssembly();
        var value = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.1.0";
        }

        // Source revision is build metadata, not part of the user-facing version.
        return value.Split('+', 2)[0];
    }

    private static string? ReadBuildTime()
    {
        // 优先读入口程序集（EXE）的元数据；被单独引用时回退到本程序集。
        var entry = Assembly.GetEntryAssembly();
        var value = entry is null ? null : ReadMetadata(entry);
        return value ?? ReadMetadata(typeof(BuildInfo).Assembly);
    }

    private static string? ReadMetadata(Assembly assembly) =>
        assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "BuildTime", StringComparison.OrdinalIgnoreCase))
            ?.Value;
}
