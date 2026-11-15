// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

/// <summary>
/// Java 运行时路径的规范化与安全判定。
///
/// 运行时身份以规范化可执行文件路径为准（ADR 0014），而不是目录名或用户输入：
/// 页面、登记存储、优先级配置与卸载安全校验都必须使用同一套规范化结果，
/// 否则同一次扫描可能得到两个“不同”的候选，或让卸载绕过托管目录边界。
/// </summary>
public static class JavaRuntimePath
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// 规范化绝对路径：去掉首尾空白与引号、解析相对路径、解析符号链接（若存在）。
    /// 结果只用于比较，不用于 IO 拼接以外的用途。
    /// </summary>
    public static string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim().Trim('"');
        try
        {
            var fullPath = Path.GetFullPath(trimmed);
            if (File.Exists(fullPath))
            {
                var target = File.ResolveLinkTarget(fullPath, returnFinalTarget: true);
                if (target is not null)
                {
                    fullPath = Path.GetFullPath(target.FullName);
                }
            }

            fullPath = Path.TrimEndingDirectorySeparator(fullPath);
            return OperatingSystem.IsWindows() ? fullPath.ToLowerInvariant() : fullPath;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException
                or IOException or UnauthorizedAccessException)
        {
            return OperatingSystem.IsWindows() ? trimmed.ToLowerInvariant() : trimmed;
        }
    }

    public static bool Same(string? left, string? right) =>
        !string.IsNullOrEmpty(left) &&
        !string.IsNullOrEmpty(right) &&
        string.Equals(Canonicalize(left), Canonicalize(right), StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="path"/> 是否就是 <paramref name="root"/> 的直接子目录（ECL 托管运行时布局：
    /// <c>Runtimes/&lt;component&gt;</c>）。只会被用于卸载所有权判定，必须严格。
    /// </summary>
    public static bool IsDirectChild(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var canonicalPath = Canonicalize(path);
        var canonicalRoot = Canonicalize(root);
        if (canonicalPath.Length == 0 || canonicalRoot.Length == 0 ||
            string.Equals(canonicalPath, canonicalRoot, StringComparison.Ordinal))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(canonicalPath);
        return parent is not null &&
               string.Equals(Canonicalize(parent), canonicalRoot, StringComparison.Ordinal);
    }

    /// <summary>严格位于目录内部（不含目录自身），用于判断可执行文件是否属于运行时主目录。</summary>
    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var canonicalPath = Canonicalize(path);
        var canonicalRoot = Canonicalize(root);
        if (canonicalPath.Length == 0 || canonicalRoot.Length == 0 ||
            string.Equals(canonicalPath, canonicalRoot, StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        return canonicalPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// 运行时主目录是否是可以安全递归删除的 ECL 托管目录：
    /// 必须是 <paramref name="runtimesDirectory"/> 的直接子目录、存在、不是驱动器根、
    /// 不是重解析点（junction/symlink），且可执行文件位于该目录内。
    /// </summary>
    public static bool IsSafeManagedHome(JavaRuntime runtime, string runtimesDirectory)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var home = runtime.HomePath;
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
        {
            return false;
        }

        if (!IsDirectChild(home, runtimesDirectory))
        {
            return false;
        }

        // 卸载是破坏性操作：不猜测根目录、不跨越重解析点。
        if (string.Equals(
                Path.GetPathRoot(Path.GetFullPath(home)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)),
                PathComparison))
        {
            return false;
        }

        try
        {
            if ((File.GetAttributes(home) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return IsUnder(runtime.ExecutablePath, home);
    }
}
