// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Ecl.Deployment;

namespace Ecl.Installer;

internal static class InstallerBundle
{
    private const string ResourceName = "ECL.Installer.Bundle.zip";
    private const int MaxEntryCount = 64;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private static string? _extractedDirectory;

    public static string Extract()
    {
        var existing = _extractedDirectory;
        if (existing is not null && Directory.Exists(existing))
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
            if (archive.Entries.Count > MaxEntryCount)
            {
                throw new InvalidDataException("安装介质包含过多文件条目。");
            }

            var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            long expandedBytes = 0;

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

                if (entry.Length < 0 || entry.Length > MaxExpandedBytes - expandedBytes)
                {
                    throw new InvalidDataException("安装介质解压后的总大小超过允许上限。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    expandedBytes += read;
                    if (expandedBytes > MaxExpandedBytes)
                    {
                        throw new InvalidDataException("安装介质解压后的总大小超过允许上限。");
                    }

                    output.Write(buffer, 0, read);
                }
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
