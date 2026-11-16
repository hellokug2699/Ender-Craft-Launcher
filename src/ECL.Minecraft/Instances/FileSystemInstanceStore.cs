// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Instances;

/// <summary>
/// 文件系统实例仓库（F-02/F-03 多受管理根）。记录目录解析统一走
/// <see cref="IAppPaths.GetInstanceDirectory"/>：主工作区与已登记的其他受管理根
/// 都由 AppPaths 的实例→根映射解析，避免各调用方自行拼接路径。
/// </summary>
internal sealed class FileSystemInstanceStore(
    IAppPaths paths,
    IWorkspaceService workspace,
    ILogger<FileSystemInstanceStore> logger) : IInstanceStore
{
    private static readonly string[] ManagedGameDirectories =
    [
        "mods",
        "config",
        "saves",
        "resourcepacks",
        "shaderpacks",
        "logs",
        "natives",
    ];

    public Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var instances = new List<GameInstance>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rootId, instancesDirectory) in EnumerateInstanceDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(instancesDirectory))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(instancesDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instanceId = Path.GetFileName(directory);
                if (!AppPaths.IsValidInstanceId(instanceId) || !seen.Add(instanceId))
                {
                    continue;
                }

                var instance = TryLoadAt(directory);
                if (instance is null)
                {
                    continue;
                }

                if (rootId is not null && instance.SourceRootId is null)
                {
                    instance = instance with { SourceRootId = rootId };
                }

                instances.Add(instance);
            }
        }

        return Task.FromResult<IReadOnlyList<GameInstance>>(
            instances.OrderByDescending(instance => instance.LastPlayedAt ?? instance.CreatedAt).ToArray());
    }

    public Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Task.FromResult<GameInstance?>(null);
        }

        var instance = TryLoadAt(paths.GetInstanceDirectory(instanceId));
        if (instance is not null)
        {
            return Task.FromResult<GameInstance?>(instance);
        }

        // 映射缺失（例如外部登记后未刷新）：退回按受管理根查找同一记录。
        foreach (var (rootId, instancesDirectory) in EnumerateInstanceDirectories())
        {
            if (rootId is null)
            {
                continue;
            }

            var candidate = Path.Combine(instancesDirectory, instanceId);
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            instance = TryLoadAt(candidate);
            if (instance is not null)
            {
                paths.RegisterInstanceRoot(instanceId, Path.GetDirectoryName(instancesDirectory)!);
                return Task.FromResult<GameInstance?>(instance with { SourceRootId = instance.SourceRootId ?? rootId });
            }
        }

        return Task.FromResult<GameInstance?>(null);
    }

    public Task SaveAsync(GameInstance instance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = paths.GetInstanceDirectory(instance.Id);
        Directory.CreateDirectory(directory);

        // 自建实例：版本隔离始终开启，游戏目录固定在 <根>/instances/<id>/minecraft。
        // 导入的既有安装直接引用原目录，这里不创建、也不改写它。
        if (string.IsNullOrWhiteSpace(instance.GameDirectory))
        {
            var gameDirectory = paths.GetInstanceGameDirectory(instance.Id);
            Directory.CreateDirectory(gameDirectory);
            foreach (var name in ManagedGameDirectories)
            {
                Directory.CreateDirectory(Path.Combine(gameDirectory, name));
            }
        }

        Directory.CreateDirectory(paths.GetInstanceLogsDirectory(instance.Id));

        var path = paths.GetInstanceMetadataFile(instance.Id);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(instance, EclJson.Indented));
        File.Move(temporaryPath, path, overwrite: true);

        return Task.CompletedTask;
    }

    public Task<Result> DeleteAsync(
        InstanceDeletionPlan plan,
        InstanceDeletionMode mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // 游戏数据先删、元数据最后删：删除失败时实例仍可见，便于用户重试或手工恢复。
            if (mode == InstanceDeletionMode.DeleteManagedData &&
                plan.Find(InstanceDeletionEntryKind.ManagedGameData) is { CanDelete: true, Exists: true } gameData)
            {
                Directory.Delete(gameData.Path, recursive: true);
                logger.LogInformation("已删除实例 {Id} 的 ECL 托管游戏目录：{Directory}", plan.InstanceId, gameData.Path);
            }

            if (plan.Find(InstanceDeletionEntryKind.LauncherLogs) is { Exists: true } logs)
            {
                Directory.Delete(logs.Path, recursive: true);
            }

            foreach (var entry in plan.Entries.Where(entry =>
                         entry.Kind == InstanceDeletionEntryKind.LauncherRecord && entry.Exists))
            {
                if (File.Exists(entry.Path))
                {
                    File.Delete(entry.Path);
                }
            }

            // 只移除空的实例记录目录。仅移除记录或保留游戏数据时，剩下的 minecraft/ 不会被触碰。
            if (Directory.Exists(plan.RecordDirectory) &&
                !Directory.EnumerateFileSystemEntries(plan.RecordDirectory).Any())
            {
                Directory.Delete(plan.RecordDirectory);
            }

            logger.LogInformation(
                "已移除实例 {Name}（{Id}）的记录，模式：{Mode}，外部游戏目录保留：{GameDirectory}",
                plan.InstanceName,
                plan.InstanceId,
                mode,
                plan.GameDirectory);
            return Task.FromResult(Result.Ok());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "删除实例 {Id} 失败（模式：{Mode}）。",
                plan.InstanceId,
                mode);
            return Task.FromResult(Result.Fail(
                "instance.delete_failed",
                "删除实例失败，请检查目录权限或占用后重试。",
                exception.Message));
        }
    }

    /// <summary>
    /// 枚举主工作区与所有已登记受管理根的实例目录。主工作区在前；
    /// 同一 InstanceId 出现在多个根时只取第一条并记录警告（身份冲突由生命周期计划阻断）。
    /// </summary>
    private IEnumerable<(string? RootId, string InstancesDirectory)> EnumerateInstanceDirectories()
    {
        var primary = workspace.Primary;
        yield return (primary?.Id, paths.InstancesDirectory);

        foreach (var root in workspace.Roots)
        {
            if (root.Kind != WorkspaceKind.Managed ||
                !root.Enabled ||
                string.Equals(root.Id, primary?.Id, StringComparison.Ordinal) ||
                PathIdentity.PathsEqual(root.Path, paths.GameRoot))
            {
                continue;
            }

            yield return (root.Id, Path.Combine(root.Path, "instances"));
        }
    }

    private GameInstance? TryLoadAt(string recordDirectory)
    {
        var path = Path.Combine(recordDirectory, "instance.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<GameInstance>(stream, EclJson.Default);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "读取实例元数据失败，已跳过：{Path}", path);
            return null;
        }
    }
}
