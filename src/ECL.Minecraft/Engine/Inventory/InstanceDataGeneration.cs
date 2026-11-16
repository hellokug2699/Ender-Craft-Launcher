// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;

namespace Ecl.Minecraft.Engine.Inventory;

/// <summary>
/// 实例派生数据代次（F-04 §6.7）。事务提交/回滚后递增；缓存条目记录代次，
/// 代次不匹配即视为过期，不得作为当前事实返回。
/// </summary>
public sealed class InstanceDataGeneration
{
    private readonly IAppPaths _paths;
    private readonly object _sync = new();

    public InstanceDataGeneration(IAppPaths paths)
    {
        _paths = paths;
    }

    public string GetStatePath(string instanceId) =>
        Path.Combine(_paths.ConfigDirectory, "instance-engine", "state", $"{instanceId}.json");

    public long Get(string instanceId)
    {
        var path = GetStatePath(instanceId);
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("generation", out var value) &&
                   value.TryGetInt64(out var generation)
                ? generation
                : 0;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public long Increment(string instanceId)
    {
        lock (_sync)
        {
            var current = Get(instanceId);
            var next = current + 1;
            var path = GetStatePath(instanceId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(
                    temporary,
                    JsonSerializer.Serialize(new InstanceGenerationDocument(1, next), EclJson.Default));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // 残留临时文件无害。
                    }
                }
            }

            return next;
        }
    }

    private sealed record InstanceGenerationDocument(int SchemaVersion, long Generation);
}
