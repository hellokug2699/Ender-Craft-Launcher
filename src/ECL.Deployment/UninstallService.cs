// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Deployment;

public sealed record UninstallResult(
    bool Success,
    bool Partial,
    int DeletedFiles,
    int FailedFiles,
    IReadOnlyList<string> Errors,
    string? UninstallerPath)
{
    public static UninstallResult Fail(string error) => new(false, false, 0, 0, [error], null);
}

/// <summary>
/// 独立卸载的核心逻辑：只删除安装清单中、仍位于安装根目录内的程序文件；
/// 默认保留实例、世界存档、模组与用户配置。清单缺失/损坏时拒绝自动删除。
/// </summary>
public static class UninstallService
{
    public static UninstallResult Run(string installRoot, UninstallRegistration? registration = null)
    {
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return UninstallResult.Fail($"安装目录无效：{exception.Message}");
        }

        if (!Directory.Exists(root))
        {
            return UninstallResult.Fail("安装目录不存在。");
        }

        var manifestResult = InstallManifestStore.Read(root);
        if (!manifestResult.IsSuccess)
        {
            return UninstallResult.Fail(manifestResult.Error!.Message);
        }

        var validation = manifestResult.Value!;
        if (!validation.IsValid)
        {
            return UninstallResult.Fail(string.Join("；", validation.Errors));
        }

        var deleted = 0;
        var failed = 0;
        var errors = new List<string>();
        var uninstaller = InstallManifestStore.ResolveFile(root, validation.Manifest.Uninstaller);
        var files = validation.Manifest.Files
            .OrderByDescending(file => file.Path.Count(character => character == '/'))
            .ThenByDescending(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        foreach (var file in files)
        {
            string path;
            try
            {
                path = InstallManifestStore.ResolveFile(root, file.Path);
            }
            catch (InvalidOperationException exception)
            {
                failed++;
                errors.Add(exception.Message);
                continue;
            }

            if (string.Equals(path, uninstaller, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                failed++;
                errors.Add($"拒绝删除重解析点：{file.Path}");
                continue;
            }

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
                errors.Add($"删除失败：{file.Path}（{exception.Message}）");
            }
        }

        foreach (var directory in validation.Manifest.Directories
                     .OrderByDescending(path => path.Count(character => character == '/'))
                     .ThenByDescending(path => path, StringComparer.Ordinal))
        {
            try
            {
                if (!UpdatePathSafety.TryNormalizeRelativePath(directory, out var relative))
                {
                    continue;
                }

                var path = Path.GetFullPath(Path.Combine(root, relative));
                if (!UpdatePathSafety.IsContainedIn(root, path))
                {
                    continue;
                }

                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // 非空目录或用户文件保留，不视为卸载失败。
            }
        }

        var unregistered = (registration ?? new UninstallRegistration()).Unregister();
        if (!unregistered.IsSuccess)
        {
            failed++;
            errors.Add(unregistered.Error!.Message);
        }

        var leftovers = Directory.Exists(root)
            ? Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).ToArray()
            : [];
        if (leftovers.Length == 0)
        {
            try
            {
                Directory.Delete(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"安装目录未能删除：{exception.Message}");
            }
        }
        else
        {
            errors.Add($"安装目录仍保留以下文件（不会被卸载器删除）：{string.Join("、", leftovers.Take(8))}");
        }

        return new UninstallResult(
            failed == 0,
            failed > 0,
            deleted,
            failed,
            errors,
            uninstaller);
    }
}
