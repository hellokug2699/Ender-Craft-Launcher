// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using Ecl.Core;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Launch;

public static class NativesExtractor
{
    public static int Extract(
        IEnumerable<LibraryPlanItem> plan,
        IAppPaths paths,
        string nativesDirectory)
    {
        if (Directory.Exists(nativesDirectory))
        {
            Directory.Delete(nativesDirectory, recursive: true);
        }

        Directory.CreateDirectory(nativesDirectory);
        var root = Path.GetFullPath(nativesDirectory) + Path.DirectorySeparatorChar;
        var extracted = 0;

        foreach (var item in plan)
        {
            var artifact = item.NativeArtifact;
            if (artifact?.Path is null)
            {
                continue;
            }

            var jarPath = VersionFilePlanner.GetLibraryPath(paths, artifact);
            if (!File.Exists(jarPath))
            {
                throw new FileNotFoundException("缺少 natives 库文件。", jarPath);
            }

            using var archive = ZipFile.OpenRead(jarPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var entryName = entry.FullName.Replace('\\', '/');
                if (IsExcluded(item.Library, entryName))
                {
                    continue;
                }

                var destination = Path.GetFullPath(
                    Path.Combine(nativesDirectory, entryName.Replace('/', Path.DirectorySeparatorChar)));

                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"natives 压缩包包含非法路径：{entry.FullName}");
                }

                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                entry.ExtractToFile(destination, overwrite: true);
                extracted++;
            }
        }

        return extracted;
    }

    private static bool IsExcluded(VersionLibrary library, string entryName)
    {
        if (entryName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) ||
            entryName.EndsWith(".sha1", StringComparison.OrdinalIgnoreCase) ||
            entryName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var exclude in library.Extract?.Exclude ?? [])
        {
            if (entryName.StartsWith(exclude.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
