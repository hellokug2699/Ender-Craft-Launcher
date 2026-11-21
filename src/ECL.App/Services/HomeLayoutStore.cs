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
/// - 只保存“实例 ID 的顺序”，不写入实例目录、不进入实例 JSON；
/// - 一格一实例：不保存空位、不保存重复项（2026-11-21 维护者决定：有多少实例就多少格子）；
/// - 旧版本留下的空位/重复项在读取时被丢弃并立即回写，避免把历史空洞重新变成格子；
/// - 文件缺失或损坏时按“没有自定义排列”处理，由主页回退到实例列表顺序。
/// </summary>
public interface IHomeLayoutStore
{
    /// <summary>读取保存的实例顺序；只包含合法实例 ID（空位、重复项、非法 ID 会被丢弃）。</summary>
    IReadOnlyList<string> Load();

    /// <summary>保存实例顺序；重复调用幂等，空位与重复项不会被写入。</summary>
    void Save(IReadOnlyList<string> slots);
}

public sealed class HomeLayoutStore : IHomeLayoutStore
{
    private const int MaxSlots = 4096;

    private readonly ILogger<HomeLayoutStore> _logger;
    private readonly string _path;
    private readonly object _gate = new();

    public HomeLayoutStore(IAppPaths paths, ILogger<HomeLayoutStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger;
        _path = Path.Combine(paths.ConfigDirectory, "home-layout.json");
    }

    public IReadOnlyList<string> Load()
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
                var stored = state?.Slots;
                if (stored is not { Count: > 0 })
                {
                    return [];
                }

                var normalized = Normalize(stored);
                // 历史布局可能含删除留下的空位（甚至上百个）或重复项：只修内存会让下次保存
                // 又写回旧数组，所以这里把修复结果立即落盘，让文件本身就是“一格一实例”。
                if (normalized.Count != stored.Count)
                {
                    _logger.LogInformation(
                        "主页实例排列已整理：{Stored} 项 → {Normalized} 个实例（丢弃空位与重复项）。",
                        stored.Count,
                        normalized.Count);
                    Write(normalized);
                }

                return normalized;
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

    public void Save(IReadOnlyList<string> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        lock (_gate)
        {
            try
            {
                Write(Normalize(slots));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 保存失败只影响下次启动时的排列，不回滚当前页面操作。
                _logger.LogWarning(exception, "无法保存主页实例排列：{Path}", _path);
            }
        }
    }

    private void Write(IReadOnlyList<string> slots)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var state = new HomeLayoutState([.. slots]);
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>只保留合法实例 ID，丢弃空位、重复项与异常长度。</summary>
    private static IReadOnlyList<string> Normalize(IEnumerable<string?> slots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var slot in slots)
        {
            if (result.Count >= MaxSlots)
            {
                break;
            }

            if (AppPaths.IsValidInstanceId(slot) && seen.Add(slot!))
            {
                result.Add(slot!);
            }
        }

        return result;
    }

    private sealed record HomeLayoutState(IReadOnlyList<string?>? Slots);
}
