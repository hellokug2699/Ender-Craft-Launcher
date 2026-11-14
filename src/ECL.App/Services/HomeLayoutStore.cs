// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Text.Json;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Services;

/// <summary>
/// 主页中央实例切换栏的排列（home.md §4）。
///
/// 这是主页的布局偏好，不是实例配置：
/// - 只保存“位置 → 实例 ID”的顺序，不写入实例目录、不进入实例 JSON；
/// - 空位用 null 表示：删除实例后其位置保持为空位，不自动补位（§4.4）；
/// - 文件缺失或损坏时按“没有自定义排列”处理，由主页回退到实例列表顺序。
/// </summary>
public interface IHomeLayoutStore
{
    /// <summary>读取保存的位置顺序；没有记录时返回空列表。</summary>
    IReadOnlyList<string?> Load();

    /// <summary>保存位置顺序；重复调用幂等，null 表示空位。</summary>
    void Save(IReadOnlyList<string?> slots);
}

public sealed class HomeLayoutStore : IHomeLayoutStore
{
    private readonly ILogger<HomeLayoutStore> _logger;
    private readonly string _path;
    private readonly object _gate = new();

    public HomeLayoutStore(IAppPaths paths, ILogger<HomeLayoutStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger;
        _path = Path.Combine(paths.ConfigDirectory, "home-layout.json");
    }

    public IReadOnlyList<string?> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            try
            {
                var state = JsonSerializer.Deserialize<HomeLayoutState>(File.ReadAllText(_path));
                return state?.Slots is { Count: > 0 } slots
                    ? slots.Select(Normalize).ToArray()
                    : [];
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // 排列记录损坏时回退到实例列表顺序，不因布局偏好损坏影响主页。
                _logger.LogWarning(exception, "无法读取主页实例排列，回退到实例列表顺序：{Path}", _path);
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<string?> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var state = new HomeLayoutState([.. slots.Select(Normalize)]);
                var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(state));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 保存失败只影响下次启动时的排列，不回滚当前页面操作。
                _logger.LogWarning(exception, "无法保存主页实例排列：{Path}", _path);
            }
        }
    }

    /// <summary>只有合法实例 ID 才会被保存；其它值视为空位。</summary>
    private static string? Normalize(string? instanceId) =>
        AppPaths.IsValidInstanceId(instanceId) ? instanceId : null;

    private sealed record HomeLayoutState(IReadOnlyList<string?>? Slots);
}
