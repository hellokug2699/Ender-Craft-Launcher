// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Reflection;
using Ecl.Deployment;

namespace Ecl.Installer;

internal static class InstallerBundle
{
    private const string ResourceName = "ECL.Installer.Bundle.zip";
    private static string? _extractedDirectory;

    public static string Extract()
    {
        if (_extractedDirectory is { } existing && Directory.Exists(existing))
        {
            return existing;
        }

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (resource is null)
        {
            if (InstallMedia.Inspect(AppContext.BaseDirectory).IsSuccess)
            {
                return AppContext.BaseDirectory;
            }

            throw new InvalidOperationException("Installer 未包含完整部署介质资源，请重新下载官方独立安装程序。");
        }

        var destination = Path.Combine(Path.GetTempPath(), "EnderCraftLauncher", "Installer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);

        try
        {
            using var archive = new ZipArchive(resource, ZipArchiveMode.Read, leaveOpen: false);
            var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType == 0xA000)
                {
                    throw new InvalidDataException($"安装介质包含不允许的符号链接：{entry.FullName}");
                }

                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!target.StartsWith(root, comparison))
                {
                    throw new InvalidDataException($"安装介质路径越界：{entry.FullName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }

            var inspected = InstallMedia.Inspect(destination);
            if (!inspected.IsSuccess)
            {
                throw new InvalidDataException($"内嵌安装介质不完整：{inspected.Error!.Message}");
            }

            _extractedDirectory = destination;
            return destination;
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    public static void Cleanup()
    {
        if (_extractedDirectory is not { } directory ||
            string.Equals(directory, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TryDelete(directory);
        _extractedDirectory = null;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不应覆盖安装结果；系统稍后可回收临时文件。
        }
        catch (UnauthorizedAccessException)
        {
            // 清理失败不应覆盖安装结果；系统稍后可回收临时文件。
        }
    }
}
