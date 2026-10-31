// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using Ecl.Core;

namespace Ecl.Deployment;

public sealed record ArchiveLimits(
    int MaxEntries = 10_000,
    long MaxTotalBytes = 2L * 1024 * 1024 * 1024,
    long MaxFileBytes = 512L * 1024 * 1024);

/// <summary>
/// 安全解包完整发布包。拒绝绝对路径、盘符、父目录、符号链接、重复条目和超限大小，
/// 并在写入前用规范化后的完整路径确认目标仍位于 staging 根目录内。
/// </summary>
public static class SafeZipExtractor
{
    public static async Task<Result> ExtractAsync(
        string archivePath,
        string destination,
        ArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= new ArchiveLimits();
        if (!File.Exists(archivePath))
        {
            return Result.Fail("update.archive_missing", "更新包不存在。", archivePath);
        }

        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > limits.MaxEntries)
            {
                return Result.Fail(
                    "update.archive_too_many_entries",
                    "更新包条目数量超过限制。",
                    archive.Entries.Count.ToString());
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rawName = entry.FullName.Replace('\\', '/');
                if (rawName.EndsWith('/'))
                {
                    rawName = rawName.TrimEnd('/');
                    if (rawName.Length == 0)
                    {
                        continue;
                    }
                }

                if (!UpdatePathSafety.TryNormalizeRelativePath(rawName, out var relativePath))
                {
                    return Result.Fail(
                        "update.archive_unsafe_path",
                        "更新包包含不安全的路径。",
                        entry.FullName);
                }

                if (!seen.Add(relativePath))
                {
                    return Result.Fail(
                        "update.archive_duplicate_path",
                        "更新包包含重复路径。",
                        relativePath);
                }

                if (IsSymbolicLink(entry))
                {
                    return Result.Fail(
                        "update.archive_symlink",
                        "更新包包含符号链接或重解析条目。",
                        relativePath);
                }

                var target = Path.GetFullPath(Path.Combine(root, relativePath));
                if (!UpdatePathSafety.IsContainedIn(root, target))
                {
                    return Result.Fail(
                        "update.archive_path_escape",
                        "更新包条目会写入 staging 目录之外。",
                        relativePath);
                }

                if (entry.FullName.EndsWith('/') || entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                if (entry.Length > limits.MaxFileBytes)
                {
                    return Result.Fail(
                        "update.archive_file_too_large",
                        "更新包中的单个文件超过大小限制。",
                        relativePath);
                }

                totalBytes = checked(totalBytes + entry.Length);
                if (totalBytes > limits.MaxTotalBytes)
                {
                    return Result.Fail(
                        "update.archive_too_large",
                        "更新包解压后总大小超过限制。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = entry.Open();
                await using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var copied = await CopyAsync(source, output, limits.MaxFileBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (copied != entry.Length)
                {
                    return Result.Fail(
                        "update.archive_truncated",
                        "更新包条目的实际大小与目录记录不符。",
                        relativePath);
                }
            }

            return Result.Ok();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            return Result.Fail(
                "update.archive_failed",
                "解压更新包失败。",
                exception.Message);
        }
    }

    private static async Task<long> CopyAsync(
        Stream source,
        Stream output,
        long limit,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 128];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("条目解压后超过大小限制。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        // Unix mode 位于 ExternalAttributes 的高 16 位；0xA000 表示符号链接。
        var mode = (entry.ExternalAttributes >> 16) & 0xF000;
        return mode == 0xA000;
    }
}
