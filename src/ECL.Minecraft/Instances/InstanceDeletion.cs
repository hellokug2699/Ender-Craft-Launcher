// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Instances;

/// <summary>
/// 实例目录的所有权。ECL 只主动删除 <see cref="EclManaged"/> 的游戏数据；
/// 导入产生的 <see cref="External"/> / <see cref="SharedExternal"/> 目录永远只被引用，不被删除。
/// </summary>
public enum InstanceDirectoryOwnership
{
    /// <summary>ECL 在 <c>instances/&lt;id&gt;</c> 下创建并管理。</summary>
    EclManaged,

    /// <summary>导入的既有安装，由外部启动器或用户管理。</summary>
    External,

    /// <summary>被多个实例共同引用的外部游戏目录。</summary>
    SharedExternal,
}

/// <summary>删除计划中的一项。记录、日志和游戏数据分开列出，调用方无需猜测目录语义。</summary>
public enum InstanceDeletionEntryKind
{
    /// <summary>实例元数据与实例设置文件。</summary>
    LauncherRecord,

    /// <summary>ECL 写入的实例日志目录。</summary>
    LauncherLogs,

    /// <summary>ECL 托管的 <c>instances/&lt;id&gt;/minecraft</c> 游戏数据。</summary>
    ManagedGameData,

    /// <summary>外部启动器/用户管理的游戏目录，只建立引用。</summary>
    ExternalGameData,
}

/// <summary>
/// 删除计划中的一项。<see cref="CanDelete"/> 为 <c>false</c> 时，即使调用方要求删除托管数据，
/// 也不得触碰该路径。
/// </summary>
public sealed record InstanceDeletionEntry(
    string Path,
    InstanceDeletionEntryKind Kind,
    InstanceDirectoryOwnership Ownership,
    bool Exists,
    bool CanDelete);

/// <summary>被同一游戏目录引用的其它实例。</summary>
public sealed record InstanceReference(string Id, string Name);

/// <summary>
/// 删除实例前的结构化预览。计划只描述“存在什么、归谁所有、能否删除”，
/// 具体删除哪些内容由 <see cref="InstanceDeleteOptions"/> 决定。
/// </summary>
public sealed record InstanceDeletionPlan(
    string InstanceId,
    string InstanceName,
    string RecordDirectory,
    string GameDirectory,
    InstanceDirectoryOwnership GameDirectoryOwnership,
    IReadOnlyList<InstanceDeletionEntry> Entries,
    IReadOnlyList<InstanceReference> SharedWith)
{
    /// <summary>是否存在 ECL 托管、可以随实例一起删除的游戏数据。</summary>
    public bool HasManagedGameData =>
        Entries.Any(entry => entry.Kind == InstanceDeletionEntryKind.ManagedGameData && entry.Exists);

    /// <summary>游戏目录是否来自外部安装（单实例引用或共享）。</summary>
    public bool HasExternalGameData => GameDirectoryOwnership is
        InstanceDirectoryOwnership.External or InstanceDirectoryOwnership.SharedExternal;

    /// <summary>游戏目录是否被其它实例引用。</summary>
    public bool IsShared => GameDirectoryOwnership == InstanceDirectoryOwnership.SharedExternal;

    public InstanceDeletionEntry? Find(InstanceDeletionEntryKind kind) =>
        Entries.FirstOrDefault(entry => entry.Kind == kind);
}

/// <summary>删除模式。默认删除 ECL 托管数据；外部目录在任何模式下都不会被删除。</summary>
public enum InstanceDeletionMode
{
    /// <summary>只移除启动器记录、设置与日志，保留游戏目录。</summary>
    RemoveRecordOnly,

    /// <summary>移除启动器记录，并删除 ECL 托管的游戏数据；外部目录仍然保留。</summary>
    DeleteManagedData,
}

/// <summary>删除实例的调用参数。结构化选项避免 UI 或 CLI 自行猜测删除语义。</summary>
public sealed record InstanceDeleteOptions
{
    public InstanceDeletionMode Mode { get; init; } = InstanceDeletionMode.DeleteManagedData;

    public static InstanceDeleteOptions RemoveRecordOnly { get; } = new()
    {
        Mode = InstanceDeletionMode.RemoveRecordOnly,
    };

    public static InstanceDeleteOptions DeleteManagedData { get; } = new();
}

/// <summary>根据实例元数据生成删除计划。共享目录只做引用检查，不推断所有权。</summary>
public static class InstanceDeletionPlanner
{
    public static InstanceDeletionPlan Create(
        IAppPaths paths,
        GameInstance instance,
        IReadOnlyList<GameInstance> allInstances)
    {
        var recordDirectory = paths.GetInstanceDirectory(instance.Id);
        var metadataFile = paths.GetInstanceMetadataFile(instance.Id);
        var settingsFile = paths.GetInstanceSettingsFile(instance.Id);
        var logsDirectory = paths.GetInstanceLogsDirectory(instance.Id);

        var entries = new List<InstanceDeletionEntry>
        {
            new(metadataFile, InstanceDeletionEntryKind.LauncherRecord, InstanceDirectoryOwnership.EclManaged, File.Exists(metadataFile), true),
            new(settingsFile, InstanceDeletionEntryKind.LauncherRecord, InstanceDirectoryOwnership.EclManaged, File.Exists(settingsFile), true),
            new(logsDirectory, InstanceDeletionEntryKind.LauncherLogs, InstanceDirectoryOwnership.EclManaged, Directory.Exists(logsDirectory), true),
        };

        string gameDirectory;
        InstanceDeletionEntryKind gameKind;
        InstanceDirectoryOwnership ownership;
        bool canDelete;
        IReadOnlyList<InstanceReference> sharedWith = [];

        if (string.IsNullOrWhiteSpace(instance.GameDirectory))
        {
            gameDirectory = paths.GetInstanceGameDirectory(instance.Id);
            gameKind = InstanceDeletionEntryKind.ManagedGameData;
            ownership = InstanceDirectoryOwnership.EclManaged;
            canDelete = true;
        }
        else
        {
            gameDirectory = NormalizePath(instance.GameDirectory);
            gameKind = InstanceDeletionEntryKind.ExternalGameData;
            canDelete = false;

            sharedWith = allInstances
                .Where(other => !string.Equals(other.Id, instance.Id, StringComparison.Ordinal))
                .Where(other => PathsEqual(InstancePaths.GetGameDirectory(paths, other), gameDirectory))
                .Select(other => new InstanceReference(other.Id, other.Name))
                .ToArray();
            ownership = sharedWith.Count > 0
                ? InstanceDirectoryOwnership.SharedExternal
                : InstanceDirectoryOwnership.External;
        }

        entries.Add(new InstanceDeletionEntry(
            gameDirectory,
            gameKind,
            ownership,
            SafeDirectoryExists(gameDirectory),
            canDelete));

        return new InstanceDeletionPlan(
            instance.Id,
            instance.Name,
            recordDirectory,
            gameDirectory,
            ownership,
            entries,
            sharedWith);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 元数据可能来自旧版本或手工编辑；保留原始值，计划会把它标记为不存在。
            return path;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
