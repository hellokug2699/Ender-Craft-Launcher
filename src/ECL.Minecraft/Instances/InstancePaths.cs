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
    bool AlreadyImported,
    DetectedVersionProblem Problem = DetectedVersionProblem.None,
    string? ProblemDetail = null)
{
    /// <summary>
    /// 该版本是否具备导入条件：版本 JSON 可解析、继承链完整、已确定 mainClass。
    /// 已导入的版本即使结构完整也不可重复导入。
    /// </summary>
    public bool CanImport => Problem == DetectedVersionProblem.None && !AlreadyImported;
}

/// <summary>扫描到的版本为什么不能导入。</summary>
public enum DetectedVersionProblem
{
    /// <summary>版本结构完整，可以导入。</summary>
    None,

    /// <summary>版本 JSON 损坏、缺少 mainClass，或无法确定 Minecraft 版本。</summary>
    InvalidManifest,

    /// <summary>继承链缺少父版本文件或存在循环，属于不完整的安装。</summary>
    IncompleteInheritance,
}
