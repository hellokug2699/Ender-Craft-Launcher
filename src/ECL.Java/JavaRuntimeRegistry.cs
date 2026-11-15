// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Java;

/// <summary>一个 Java 运行时在 ECL 管理列表中的来源。来源不影响启动选择，只影响呈现与卸载权限。</summary>
public enum JavaRuntimeOrigin
{
    /// <summary>由有界发现（JAVA_HOME、PATH、已知系统位置、搜索路径）找到。</summary>
    Discovered,

    /// <summary>用户通过“添加 Java”显式登记的外部运行时；ECL 只持有引用，不拥有文件。</summary>
    UserImported,

    /// <summary>位于 ECL 的 <c>Runtimes</c> 目录，由 ECL 安装并拥有，可安全卸载。</summary>
    EclManaged,
}

/// <summary>ECL Java 管理列表的用户登记状态（ADR 0014 §运行时生命周期、页面规格 §7）。</summary>
public sealed record JavaRuntimeRegistrySnapshot(
    IReadOnlyList<string> ImportedPaths,
    IReadOnlyList<string> RemovedPaths);

/// <summary>
/// 用户登记与“从 ECL 列表移除”的持久化状态。
///
/// 发现结果、用户登记、全局优先级与验证状态是四种不同语义（页面规格 §7）：
/// 一次扫描不得覆盖用户登记，扫描遗漏也不得自动移除已登记候选。
/// 这里只保存“用户添加过”和“用户明确移除过”的规范化可执行文件路径；
/// 顺序与启动优先级属于 <c>java.priority</c> 配置，验证状态是运行时事实，均不落在这里。
/// </summary>
public interface IJavaRuntimeRegistry
{
    IReadOnlyList<string> ImportedPaths { get; }

    IReadOnlyList<string> RemovedPaths { get; }

    bool IsImported(string executablePath);

    bool IsRemoved(string executablePath);

    /// <summary>登记用户添加的外部 Java；重复登记同一路径是幂等的。</summary>
    bool RegisterImported(string executablePath);

    /// <summary>取消登记（不删除文件、不影响实例固定引用）。</summary>
    bool UnregisterImported(string executablePath);

    /// <summary>记录用户从 ECL 列表移除的候选；之后的扫描不会把它自动带回列表。</summary>
    bool MarkRemoved(string executablePath);

    /// <summary>清除成员移除记录（用户重新添加同一 Java 时使用）。</summary>
    bool ClearRemoved(string executablePath);

    /// <summary>同时清除登记与移除记录（卸载成功后使用；文件此后已不存在）。</summary>
    bool Forget(string executablePath);

    JavaRuntimeRegistrySnapshot Snapshot();
}

public sealed class JavaRuntimeRegistry : IJavaRuntimeRegistry
{
    private const int CurrentSchemaVersion = 1;

    private readonly ILogger<JavaRuntimeRegistry> _logger;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<string> _imported = [];
    private readonly List<string> _removed = [];
    private bool _loaded;

    public JavaRuntimeRegistry(IAppPaths paths, ILogger<JavaRuntimeRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger;
        _path = Path.Combine(paths.ConfigDirectory, "java-runtimes.json");
    }

    public IReadOnlyList<string> ImportedPaths
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _imported.ToArray();
            }
        }
    }

    public IReadOnlyList<string> RemovedPaths
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _removed.ToArray();
            }
        }
    }

    public bool IsImported(string executablePath) => Contains(_imported, executablePath, out _);

    public bool IsRemoved(string executablePath) => Contains(_removed, executablePath, out _);

    public bool RegisterImported(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        lock (_gate)
        {
            EnsureLoaded();
            var canonical = JavaRuntimePath.Canonicalize(executablePath);
            if (canonical.Length == 0)
            {
                return false;
            }

            var changed = false;
            var removedIndex = IndexOf(_removed, canonical);
            if (removedIndex >= 0)
            {
                _removed.RemoveAt(removedIndex);
                changed = true;
            }

            if (IndexOf(_imported, canonical) < 0)
            {
                _imported.Add(canonical);
                changed = true;
            }

            return !changed || Save();
        }
    }

    public bool UnregisterImported(string executablePath)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var index = IndexOf(_imported, JavaRuntimePath.Canonicalize(executablePath));
            if (index < 0)
            {
                return true;
            }

            _imported.RemoveAt(index);
            return Save();
        }
    }

    public bool MarkRemoved(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        lock (_gate)
        {
            EnsureLoaded();
            var canonical = JavaRuntimePath.Canonicalize(executablePath);
            if (canonical.Length == 0)
            {
                return false;
            }

            var changed = false;
            var importedIndex = IndexOf(_imported, canonical);
            if (importedIndex >= 0)
            {
                _imported.RemoveAt(importedIndex);
                changed = true;
            }

            if (IndexOf(_removed, canonical) < 0)
            {
                _removed.Add(canonical);
                changed = true;
            }

            return !changed || Save();
        }
    }

    public bool ClearRemoved(string executablePath)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var index = IndexOf(_removed, JavaRuntimePath.Canonicalize(executablePath));
            if (index < 0)
            {
                return true;
            }

            _removed.RemoveAt(index);
            return Save();
        }
    }

    public bool Forget(string executablePath)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var canonical = JavaRuntimePath.Canonicalize(executablePath);
            var changed = false;
            var importedIndex = IndexOf(_imported, canonical);
            if (importedIndex >= 0)
            {
                _imported.RemoveAt(importedIndex);
                changed = true;
            }

            var removedIndex = IndexOf(_removed, canonical);
            if (removedIndex >= 0)
            {
                _removed.RemoveAt(removedIndex);
                changed = true;
            }

            return !changed || Save();
        }
    }

    public JavaRuntimeRegistrySnapshot Snapshot()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return new JavaRuntimeRegistrySnapshot(_imported.ToArray(), _removed.ToArray());
        }
    }

    private bool Contains(List<string> list, string executablePath, out string canonical)
    {
        canonical = JavaRuntimePath.Canonicalize(executablePath);
        if (canonical.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            EnsureLoaded();
            return IndexOf(list, canonical) >= 0;
        }
    }

    private static int IndexOf(List<string> list, string canonical) =>
        list.FindIndex(item => string.Equals(item, canonical, StringComparison.Ordinal));

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<StoredRegistry>(File.ReadAllText(_path), EclJson.Default);
            if (document is null)
            {
                return;
            }

            foreach (var path in document.Imported ?? [])
            {
                var canonical = JavaRuntimePath.Canonicalize(path ?? string.Empty);
                if (canonical.Length > 0 && IndexOf(_imported, canonical) < 0)
                {
                    _imported.Add(canonical);
                }
            }

            foreach (var path in document.Removed ?? [])
            {
                var canonical = JavaRuntimePath.Canonicalize(path ?? string.Empty);
                if (canonical.Length > 0 && IndexOf(_removed, canonical) < 0)
                {
                    _removed.Add(canonical);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // 状态文件损坏时按“没有用户登记/移除记录”处理，页面仍可发现与验证；
            // 下一次成功写入会重建文件，不因辅助状态损坏阻断 Java 管理。
            _logger.LogWarning(exception, "无法读取 Java 运行时登记状态，按空状态处理：{Path}", _path);
        }
    }

    private bool Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new StoredRegistry
            {
                SchemaVersion = CurrentSchemaVersion,
                Imported = [.. _imported],
                Removed = [.. _removed],
            };
            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, EclJson.Default));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "无法保存 Java 运行时登记状态：{Path}", _path);
            return false;
        }
    }

    private sealed class StoredRegistry
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<string?>? Imported { get; set; }

        public List<string?>? Removed { get; set; }
    }
}
