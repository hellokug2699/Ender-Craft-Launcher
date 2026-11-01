// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Core.Storage;

/// <summary>
/// ECL 的四类存储边界（ADR 0018 §1）。四者在配置、代码与诊断中必须始终可区分：
/// 程序文件、用户数据、受管理 Java 运行时、Minecraft 工作区。
/// </summary>
public sealed record StorageLayout(
    string ProgramFilesRoot,
    string UserDataRoot,
    string RuntimesRoot,
    string WorkspaceRoot)
{
    public static StorageLayout From(IAppPaths paths) => new(
        AppContext.BaseDirectory,
        paths.Root,
        paths.RuntimesDirectory,
        paths.GameRoot);

    /// <summary>工作区是否与程序/用户数据目录分离。未确认工作区时视为未分离。</summary>
    public bool WorkspaceSeparatedFromUserData =>
        !string.Equals(
            WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            UserDataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
