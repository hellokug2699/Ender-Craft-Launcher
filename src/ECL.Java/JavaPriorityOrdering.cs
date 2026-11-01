// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

public sealed record JavaPriorityOrder(
    IReadOnlyList<JavaRuntime> Runtimes,
    int ConfiguredCount);

/// <summary>
/// 全局 Java 优先级排序。配置项可以指向可执行文件、Java 主目录，
/// 也可以指向包含多个 JDK/JRE 的父目录；父目录中的运行时保持发现顺序，
/// 不会被压缩成单个“最高版本”候选。
/// </summary>
public static class JavaPriorityOrdering
{
    public static JavaPriorityOrder Order(
        IReadOnlyList<JavaRuntime> runtimes,
        IReadOnlyList<string> configuredPaths)
    {
        if (runtimes.Count == 0)
        {
            return new JavaPriorityOrder([], 0);
        }

        var ordered = new List<JavaRuntime>(runtimes.Count);
        var remaining = runtimes.ToList();

        foreach (var configuredPath in configuredPaths)
        {
            var path = configuredPath.Trim().Trim('"');
            if (path.Length == 0)
            {
                continue;
            }

            // 不能边遍历边删除：同一父目录下的多个 Java 需要保持发现顺序。
            for (var index = 0; index < remaining.Count;)
            {
                var runtime = remaining[index];
                if (!Matches(runtime, path))
                {
                    index++;
                    continue;
                }

                ordered.Add(runtime);
                remaining.RemoveAt(index);
            }
        }

        var configuredCount = ordered.Count;
        ordered.AddRange(remaining);
        return new JavaPriorityOrder(ordered, configuredCount);
    }

    public static bool Matches(JavaRuntime runtime, string configuredPath)
    {
        var fullConfigured = NormalizePath(configuredPath);
        var executable = NormalizePath(runtime.ExecutablePath);
        var home = NormalizePath(runtime.HomePath);

        if (string.Equals(fullConfigured, executable, PathComparison) ||
            string.Equals(fullConfigured, home, PathComparison))
        {
            return true;
        }

        if (!Directory.Exists(configuredPath))
        {
            return false;
        }

        var root = fullConfigured.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                   Path.DirectorySeparatorChar;
        return executable.StartsWith(root, PathComparison) || home.StartsWith(root, PathComparison);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return path;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
