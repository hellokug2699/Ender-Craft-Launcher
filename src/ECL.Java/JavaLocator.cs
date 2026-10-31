// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Java;

public sealed class JavaLocator : IJavaLocator
{
    private const int MaxScanDepth = 4;
    private const int MaxScanDirectories = 4096;

    private static readonly string JavaExecutableName = OperatingSystem.IsWindows() ? "java.exe" : "java";

    private readonly IAppPaths _paths;
    private readonly ILogger<JavaLocator> _logger;
    private readonly IReadOnlyList<IJavaSearchRootProvider> _searchRootProviders;

    public JavaLocator(
        IAppPaths paths,
        ILogger<JavaLocator> logger,
        IEnumerable<IJavaSearchRootProvider>? searchRootProviders = null)
    {
        _paths = paths;
        _logger = logger;
        _searchRootProviders = searchRootProviders?.ToArray() ?? [];
    }

    public Task<IReadOnlyList<JavaRuntime>> FindAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddHome(candidates, Environment.GetEnvironmentVariable("JAVA_HOME"));
        AddPathEnvironment(candidates);
        AddHomeChildren(candidates, _paths.JavaDirectory, depth: 2);

        if (OperatingSystem.IsWindows())
        {
            AddHomeChildren(candidates, GetProgramFilesPath("Java"), depth: 2);
            AddHomeChildren(candidates, GetProgramFilesPath("Eclipse Adoptium"), depth: 2);
            AddHomeChildren(candidates, GetProgramFilesPath("Microsoft"), depth: 2);
            AddHomeChildren(candidates, GetProgramFilesPath("BellSoft"), depth: 2);
            AddHomeChildren(candidates, GetProgramFilesPath("Zulu"), depth: 2);
            AddHomeChildren(candidates, GetProgramFilesPath("Amazon Corretto"), depth: 2);
            AddHomeChildren(candidates, GetProgramFilesPath("IBM Semeru"), depth: 2);
            AddHomeChildren(candidates, GetLocalAppDataPath("Programs", "Eclipse Adoptium"), depth: 2);
            AddHomeChildren(candidates, GetUserProfilePath(".jdks"), depth: 2);
        }
        else
        {
            AddHomeChildren(candidates, "/usr/lib/jvm", depth: 2);
            AddHomeChildren(candidates, "/usr/java", depth: 2);
            AddHomeChildren(candidates, "/opt/java", depth: 2);
        }

        // 用户在全局设置中指定的 Java 目录通常包含多个 JDK/JRE，
        // 这些目录不一定是“Java 主目录”（可能只是它们的上级目录）。
        foreach (var provider in _searchRootProviders)
        {
            foreach (var root in provider.GetSearchRoots())
            {
                AddConfiguredRoot(candidates, root);
            }
        }

        var runtimes = candidates
            .Select(CreateRuntime)
            .Where(runtime => runtime is not null)
            .Select(runtime => runtime!)
            .GroupBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(runtime => runtime.Version).First())
            .OrderByDescending(runtime => runtime.Version)
            .ThenBy(runtime => runtime.HomePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _logger.LogInformation("共发现 {Count} 个 Java 运行时。", runtimes.Length);
        return Task.FromResult<IReadOnlyList<JavaRuntime>>(runtimes);
    }

    public JavaRuntime? FromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(fullPath))
            {
                return CreateRuntime(fullPath);
            }

            if (!Directory.Exists(fullPath))
            {
                return null;
            }

            var direct = Path.Combine(fullPath, "bin", JavaExecutableName);
            if (File.Exists(direct))
            {
                return CreateRuntime(direct);
            }

            // 允许用户直接选择 ...\bin 目录。
            var inBin = Path.Combine(fullPath, JavaExecutableName);
            if (File.Exists(inBin) &&
                string.Equals(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)),
                    "bin",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CreateRuntime(inBin);
            }

            // 选择的是包含多个 Java 的上级目录：返回其中版本最高的运行时。
            var nested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = 0;
            AddJavaHomes(nested, fullPath, MaxScanDepth, ref visited);

            return nested
                .Select(CreateRuntime)
                .Where(runtime => runtime is not null)
                .Select(runtime => runtime!)
                .OrderByDescending(runtime => runtime.Version)
                .ThenByDescending(runtime => runtime.Version?.Minor)
                .ThenByDescending(runtime => runtime.Version?.Patch)
                .FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetProgramFilesPath(string child)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return programFiles.Length == 0 ? null : Path.Combine(programFiles, child);
    }

    private static string? GetLocalAppDataPath(params string[] children)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return localData.Length == 0 ? null : Path.Combine([localData, .. children]);
    }

    private static string? GetUserProfilePath(string child)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return userProfile.Length == 0 ? null : Path.Combine(userProfile, child);
    }

    private static void AddPathEnvironment(HashSet<string> candidates)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return;
        }

        foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(entry, JavaExecutableName);
            if (File.Exists(candidate))
            {
                candidates.Add(Path.GetFullPath(candidate));
            }
        }
    }

    private static void AddHome(HashSet<string> candidates, string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
        {
            return;
        }

        if (File.Exists(home))
        {
            candidates.Add(Path.GetFullPath(home));
            return;
        }

        var candidate = Path.Combine(home, "bin", JavaExecutableName);
        if (File.Exists(candidate))
        {
            candidates.Add(Path.GetFullPath(candidate));
        }
    }

    private static void AddHomeChildren(HashSet<string> candidates, string? root, int depth)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        AddHome(candidates, root);

        if (depth <= 0)
        {
            return;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            AddHome(candidates, child);
            if (depth > 1)
            {
                AddHomeChildren(candidates, child, depth - 1);
            }
        }
    }

    /// <summary>
    /// 从用户配置的根目录查找 Java。支持“根目录本身就是 Java 主目录”、
    /// “根目录下有一层或多层 JDK/JRE”等布局，并限制深度和目录数量以避免误选磁盘根目录时长时间扫描。
    /// </summary>
    private static void AddConfiguredRoot(HashSet<string> candidates, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        try
        {
            if (File.Exists(root))
            {
                candidates.Add(Path.GetFullPath(root));
                return;
            }

            if (!Directory.Exists(root))
            {
                return;
            }

            var visited = 0;
            AddJavaHomes(candidates, root, MaxScanDepth, ref visited);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 无法访问的目录直接跳过，不影响其它候选。
        }
    }

    private static void AddJavaHomes(HashSet<string> candidates, string directory, int depth, ref int visited)
    {
        if (depth < 0 || visited >= MaxScanDirectories)
        {
            return;
        }

        visited++;

        var executable = Path.Combine(directory, "bin", JavaExecutableName);
        if (File.Exists(executable))
        {
            candidates.Add(Path.GetFullPath(executable));
            return;
        }

        if (depth == 0)
        {
            return;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (visited >= MaxScanDirectories)
            {
                return;
            }

            try
            {
                // 跳过符号链接/junction，避免在用户误选磁盘根目录时绕圈。
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            AddJavaHomes(candidates, child, depth - 1, ref visited);
        }
    }

    private static JavaRuntime? CreateRuntime(string executablePath)
    {
        var executable = Path.GetFullPath(executablePath);
        if (!File.Exists(executable))
        {
            return null;
        }

        var binDirectory = Path.GetDirectoryName(executable);
        var home = binDirectory is null ? null : Path.GetDirectoryName(binDirectory);
        if (home is null)
        {
            return null;
        }

        var release = ReadReleaseFile(home);
        release.TryGetValue("JAVA_VERSION", out var javaVersionValue);
        var version = JavaVersion.TryParse(javaVersionValue) ?? JavaVersion.TryParse(Path.GetFileName(home));

        release.TryGetValue("IMPLEMENTOR", out var implementor);
        release.TryGetValue("JAVA_VENDOR", out var javaVendor);
        release.TryGetValue("OS_ARCH", out var architecture);

        var vendor = implementor ?? javaVendor;
        if (string.IsNullOrWhiteSpace(vendor))
        {
            vendor = InferVendor(home);
        }

        return new JavaRuntime(home, executable, vendor, architecture, version);
    }

    private static string InferVendor(string home)
    {
        var text = home.ToLowerInvariant();
        if (text.Contains("temurin", StringComparison.Ordinal) || text.Contains("adoptium", StringComparison.Ordinal))
        {
            return "Eclipse Adoptium";
        }

        if (text.Contains("zulu", StringComparison.Ordinal))
        {
            return "Azul Zulu";
        }

        if (text.Contains("corretto", StringComparison.Ordinal))
        {
            return "Amazon Corretto";
        }

        if (text.Contains("liberica", StringComparison.Ordinal) || text.Contains("bellsoft", StringComparison.Ordinal))
        {
            return "BellSoft Liberica";
        }

        if (text.Contains("microsoft", StringComparison.Ordinal))
        {
            return "Microsoft";
        }

        if (text.Contains("graalvm", StringComparison.Ordinal))
        {
            return "GraalVM";
        }

        if (text.Contains("semeru", StringComparison.Ordinal))
        {
            return "IBM Semeru";
        }

        return "Java";
    }

    private static Dictionary<string, string> ReadReleaseFile(string home)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(home, "release");
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = trimmed[..separator].Trim();
                var value = trimmed[(separator + 1)..].Trim().Trim('"');
                result[key] = value;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 忽略无法读取的 release 文件；版本信息会回退到目录名。
        }

        return result;
    }
}
