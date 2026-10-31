// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Instances;

internal sealed class FileSystemInstanceStore(IAppPaths paths, ILogger<FileSystemInstanceStore> logger) : IInstanceStore
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

        if (!Directory.Exists(paths.InstancesDirectory))
        {
            return Task.FromResult<IReadOnlyList<GameInstance>>([]);
        }

        var instances = new List<GameInstance>();
        foreach (var directory in Directory.EnumerateDirectories(paths.InstancesDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var instanceId = Path.GetFileName(directory);
            if (!AppPaths.IsValidInstanceId(instanceId))
            {
                continue;
            }

            var instance = TryLoad(instanceId);
            if (instance is not null)
            {
                instances.Add(instance);
            }
        }

        return Task.FromResult<IReadOnlyList<GameInstance>>(
            instances.OrderByDescending(instance => instance.LastPlayedAt ?? instance.CreatedAt).ToArray());
    }

    public Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryLoad(instanceId));
    }

    public Task SaveAsync(GameInstance instance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = paths.GetInstanceDirectory(instance.Id);
        Directory.CreateDirectory(directory);

        // 自建实例：版本隔离始终开启，游戏目录固定在 instances/<id>/minecraft。
        // 导入的既有安装直接引用原目录，这里不创建、也不改写它。
        if (string.IsNullOrWhiteSpace(instance.GameDirectory))
        {
            Directory.CreateDirectory(paths.GetInstanceGameDirectory(instance.Id));
            foreach (var name in ManagedGameDirectories)
            {
                Directory.CreateDirectory(Path.Combine(paths.GetInstanceGameDirectory(instance.Id), name));
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

    private GameInstance? TryLoad(string instanceId)
    {
        var path = paths.GetInstanceMetadataFile(instanceId);
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
