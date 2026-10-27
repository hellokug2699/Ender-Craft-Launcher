// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Minecraft.Instances;

namespace Ecl.Minecraft.Install;

/// <summary>
/// 安装事务：记录本次安装会写入的文件，失败时删除本次新建的文件并恢复被覆盖的版本 JSON。
/// 共享 libraries/assets 中原本就存在的文件不会被删除，避免影响其他实例。
/// </summary>
public sealed class InstallTransaction
{
    private readonly Dictionary<string, TrackedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _directories = [];
    private bool _committed;
    private bool _rolledBack;

    public void TrackFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (_files.ContainsKey(fullPath))
        {
            return;
        }

        _files[fullPath] = File.Exists(fullPath)
            ? new TrackedFile(Existed: true, Backup: null)
            : new TrackedFile(Existed: false, Backup: null);
    }

    /// <summary>用于版本 JSON 等会被覆盖的小文件：记录原始内容，失败时恢复。</summary>
    public void TrackFileWithBackup(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (_files.ContainsKey(fullPath))
        {
            return;
        }

        _files[fullPath] = File.Exists(fullPath)
            ? new TrackedFile(Existed: true, Backup: File.ReadAllBytes(fullPath))
            : new TrackedFile(Existed: false, Backup: null);
    }

    public void TrackDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) && !_directories.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            _directories.Add(fullPath);
        }
    }

    public void Commit()
    {
        _committed = true;
        _directories.Clear();
        _files.Clear();
    }

    /// <summary>回滚本次安装写入的文件，返回人类可读的操作记录（用于诊断文件与日志）。</summary>
    public IReadOnlyList<string> Rollback()
    {
        if (_committed || _rolledBack)
        {
            return [];
        }

        _rolledBack = true;
        var actions = new List<string>();

        foreach (var (path, tracked) in _files)
        {
            try
            {
                if (!tracked.Existed)
                {
                    DeleteIfExists(path, actions);
                    DeleteIfExists(path + ".part", actions);
                }
                else if (tracked.Backup is not null)
                {
                    File.WriteAllBytes(path, tracked.Backup);
                    actions.Add($"restored {path}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                actions.Add($"failed to rollback {path}: {exception.Message}");
            }
        }

        foreach (var directory in _directories.OrderByDescending(item => item.Length))
        {
            try
            {
                if (Directory.Exists(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    actions.Add($"removed directory {directory}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                actions.Add($"failed to remove directory {directory}: {exception.Message}");
            }
        }

        _files.Clear();
        _directories.Clear();
        return actions;
    }

    public static string WriteDiagnostics(
        IAppPaths paths,
        GameInstance instance,
        EclError error,
        IReadOnlyList<string> rollbackActions)
    {
        var directory = Path.Combine(paths.LogsDirectory, "install");
        Directory.CreateDirectory(directory);

        var timestamp = DateTimeOffset.UtcNow;
        var path = Path.Combine(
            directory,
            $"install-{instance.Id}-{timestamp:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        var diagnostic = new
        {
            timestamp = timestamp.ToString("O"),
            instance = new
            {
                instance.Id,
                instance.Name,
                instance.GameVersion,
                instance.VersionId,
                instance.Loader,
                instance.LoaderVersion,
            },
            error = new
            {
                error.Code,
                error.Message,
                error.Detail,
            },
            environment = new
            {
                os = Environment.OSVersion.ToString(),
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                launcherVersion = typeof(InstallTransaction).Assembly.GetName().Version?.ToString(3),
            },
            rollbackActions,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(diagnostic, EclJson.Default));
        return path;
    }

    private static void DeleteIfExists(string path, ICollection<string> actions)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.Delete(path);
        actions.Add($"deleted {path}");
    }

    private sealed record TrackedFile(bool Existed, byte[]? Backup);
}
