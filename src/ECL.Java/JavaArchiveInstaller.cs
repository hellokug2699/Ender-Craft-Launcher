// SPDX-License-Identifier: GPL-3.0-or-later

using System.Formats.Tar;
using System.IO.Compression;
using Ecl.Core;

namespace Ecl.Java;

/// <summary>下载第三方运行时压缩包后的安全解压与 Java 主目录定位。</summary>
public static class JavaArchiveInstaller
{
    public static async Task<Result<string>> ExtractAsync(
        string archivePath,
        JavaArchiveKind kind,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        var temporaryRoot = destinationRoot + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            if (kind == JavaArchiveKind.Zip)
            {
                ZipFile.ExtractToDirectory(archivePath, temporaryRoot, overwriteFiles: true);
            }
            else
            {
                await using var file = File.OpenRead(archivePath);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, temporaryRoot, overwriteFiles: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var home = FindJavaHome(temporaryRoot);
            if (home is null)
            {
                return Result<string>.Fail("java.archive_invalid", "压缩包中没有找到 bin/java 可执行文件。");
            }

            var parent = Path.GetDirectoryName(destinationRoot);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot, recursive: true);
            }

            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(home),
                    Path.TrimEndingDirectorySeparator(temporaryRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(temporaryRoot, destinationRoot);
            }
            else
            {
                Directory.Move(home, destinationRoot);
                TryDeleteDirectory(temporaryRoot);
            }

            MarkJavaExecutable(destinationRoot);
            return Result<string>.Ok(destinationRoot);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(temporaryRoot);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or NotSupportedException)
        {
            TryDeleteDirectory(temporaryRoot);
            return Result<string>.Fail(
                "java.archive_extract_failed",
                $"解压 Java 运行时失败：{exception.Message}");
        }
    }

    public static string? FindJavaHome(string root)
    {
        var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        try
        {
            return Directory
                .EnumerateFiles(root, executableName, SearchOption.AllDirectories)
                .Where(path => string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(path)),
                    "bin",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path.Length)
                .Select(path => Path.GetDirectoryName(Path.GetDirectoryName(path)))
                .FirstOrDefault(home => !string.IsNullOrWhiteSpace(home));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void MarkJavaExecutable(string home)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode mode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        var candidates = new List<string>();
        var bin = Path.Combine(home, "bin");
        if (Directory.Exists(bin))
        {
            candidates.AddRange(Directory.EnumerateFiles(bin));
        }

        var spawnHelper = Path.Combine(home, "lib", "jspawnhelper");
        if (File.Exists(spawnHelper))
        {
            candidates.Add(spawnHelper);
        }

        foreach (var file in candidates)
        {
            try
            {
                File.SetUnixFileMode(file, mode);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or PlatformNotSupportedException)
            {
                // 无法设置执行位时继续；Java 仍可能由系统包管理器提供。
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响结果。
        }
    }
}
