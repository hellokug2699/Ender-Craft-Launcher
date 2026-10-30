// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;

namespace Ecl.Minecraft.Mods;

/// <summary>
/// 安全地从整合包 ZIP 中提取覆盖文件。
/// 将归档路径解析与实际文件写入集中到一个边界，避免 Zip Slip 等路径问题。
/// </summary>
internal static class ModpackArchiveExtractor
{
    private const int MaxEntries = 20_000;
    private const long MaxEntrySize = 512L * 1024 * 1024;
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;

    public static int ExtractOverrides(
        ZipArchive archive,
        string gameDirectory,
        IReadOnlyList<string> overrideRoots)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(overrideRoots);

        if (archive.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"整合包包含过多文件条目（上限 {MaxEntries:N0}）。");
        }

        var count = 0;
        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntryName(entry.FullName);
            var root = overrideRoots.FirstOrDefault(candidate =>
                normalized.StartsWith(NormalizeRoot(candidate), StringComparison.OrdinalIgnoreCase));
            if (root is null || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var normalizedRoot = NormalizeRoot(root);
            var relative = normalized[normalizedRoot.Length..];
            if (string.IsNullOrEmpty(relative))
            {
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaxEntrySize)
            {
                throw new InvalidDataException(
                    $"整合包条目过大：{entry.FullName}（上限 {MaxEntrySize / (1024 * 1024)} MiB）。");
            }

            if (entry.Length > MaxExtractedBytes - extractedBytes)
            {
                throw new InvalidDataException(
                    $"整合包覆盖文件总大小超过限制（上限 {MaxExtractedBytes / (1024 * 1024 * 1024)} GiB）。");
            }

            var destination = ResolveSafePath(gameDirectory, relative);
            if (destination is null)
            {
                throw new InvalidDataException($"整合包包含非法路径：{entry.FullName}");
            }

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            entry.ExtractToFile(destination, overwrite: true);
            extractedBytes += entry.Length;
            count++;
        }

        return count;
    }

    private static string NormalizeEntryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("整合包包含空路径条目。");
        }

        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith('/', StringComparison.Ordinal) ||
            Path.IsPathRooted(normalized) ||
            normalized.Contains('\0'))
        {
            throw new InvalidDataException($"整合包包含绝对或非法路径：{value}");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"整合包包含路径穿越：{value}");
        }

        return string.Join('/', segments);
    }

    private static string NormalizeRoot(string value)
    {
        var normalized = NormalizeEntryName(value.TrimEnd('/', '\\'));
        return normalized + "/";
    }

    private static string? ResolveSafePath(string root, string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }
}
