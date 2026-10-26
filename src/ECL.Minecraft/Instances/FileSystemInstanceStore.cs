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
        Directory.CreateDirectory(paths.GetInstanceGameDirectory(instance.Id));
        foreach (var name in ManagedGameDirectories)
        {
            Directory.CreateDirectory(Path.Combine(paths.GetInstanceGameDirectory(instance.Id), name));
        }

        Directory.CreateDirectory(paths.GetInstanceLogsDirectory(instance.Id));

        var path = paths.GetInstanceMetadataFile(instance.Id);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(instance, EclJson.Indented));
        File.Move(temporaryPath, path, overwrite: true);

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = paths.GetInstanceDirectory(instanceId);
        if (!Directory.Exists(directory))
        {
            return Task.CompletedTask;
        }

        Directory.Delete(directory, recursive: true);
        logger.LogInformation("已删除实例目录：{Directory}", directory);
        return Task.CompletedTask;
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
