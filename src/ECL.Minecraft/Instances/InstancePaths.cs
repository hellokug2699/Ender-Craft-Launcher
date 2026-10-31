// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Instances;

/// <summary>
/// 实例目录解析。ECL 自建实例的游戏目录固定在 <c>instances/&lt;id&gt;/minecraft</c>；
/// 导入的既有安装（例如其它启动器的版本隔离目录）直接引用其原始目录，不复制文件。
/// </summary>
public static class InstancePaths
{
    /// <summary>实例实际使用的游戏目录（存档、mods、config、options.txt 所在处）。</summary>
    public static string GetGameDirectory(IAppPaths paths, GameInstance instance) =>
        string.IsNullOrWhiteSpace(instance.GameDirectory)
            ? paths.GetInstanceGameDirectory(instance.Id)
            : instance.GameDirectory;

    /// <summary>natives 始终解压到 ECL 自己的实例目录，避免污染用户的安装目录。</summary>
    public static string GetNativesDirectory(IAppPaths paths, GameInstance instance) =>
        paths.GetInstanceNativesDirectory(instance.Id);
}

/// <summary>扫描到的既有版本候选（尚未导入为 ECL 实例）。</summary>
public sealed record DetectedVersion(
    string VersionId,
    string GameVersion,
    string Loader,
    bool Isolated,
    string GameDirectory,
    bool AlreadyImported);
