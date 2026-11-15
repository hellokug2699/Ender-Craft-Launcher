// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Text.Json;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Services;

/// <summary>
/// 光影包“无支持组件”首次提示的按实例状态（shaders.md §5）。
/// 只是 UI 提示状态，不是实例配置；保存在启动器数据目录，
/// 不写入实例目录、不进入诊断包。
/// </summary>
public interface IShaderHintStore
{
    /// <summary>该实例的首次提示是否已被用户收起。</summary>
    bool IsDismissed(string instanceId);

    /// <summary>记录用户已收起首次提示；重复调用幂等。</summary>
    void Dismiss(string instanceId);

    /// <summary>发生“全部光影包被清空”时重置该实例的提示状态。</summary>
    void Reset(string instanceId);
}

public sealed class ShaderHintStore : IShaderHintStore
{
    private readonly ILogger<ShaderHintStore> _logger;
    private readonly string _path;
    private readonly object _gate = new();
    private HashSet<string>? _dismissed;

    public ShaderHintStore(IAppPaths paths, ILogger<ShaderHintStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger;
        _path = Path.Combine(paths.ConfigDirectory, "shader-hints.json");
    }

    public bool IsDismissed(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        lock (_gate)
        {
            return Load().Contains(instanceId);
        }
    }

    public void Dismiss(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            var dismissed = Load();
            if (dismissed.Add(instanceId))
            {
                Save(dismissed);
            }
        }
    }

    public void Reset(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            var dismissed = Load();
            if (dismissed.Remove(instanceId))
            {
                Save(dismissed);
            }
        }
    }

    private HashSet<string> Load()
    {
        if (_dismissed is not null)
        {
            return _dismissed;
        }

        _dismissed = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(_path))
        {
            return _dismissed;
        }

        try
        {
            var state = JsonSerializer.Deserialize<ShaderHintState>(File.ReadAllText(_path));
            foreach (var id in state?.DismissedInstanceIds ?? [])
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _dismissed.Add(id);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // 文件损坏时按“未收起”处理，不因提示状态损坏影响页面；下一次写入会重建文件。
            _logger.LogWarning(exception, "无法读取光影包提示状态，按未收起处理：{Path}", _path);
        }

        return _dismissed;
    }

    private void Save(HashSet<string> dismissed)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = new ShaderHintState([.. dismissed.OrderBy(id => id, StringComparer.Ordinal)]);
            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 提示状态写入失败只影响下一次是否再次显示提示，不回滚页面操作。
            _logger.LogWarning(exception, "无法保存光影包提示状态：{Path}", _path);
        }
    }

    private sealed record ShaderHintState(IReadOnlyList<string>? DismissedInstanceIds);
}
