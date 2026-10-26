// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Java;

public sealed class JavaLocator : IJavaLocator
{
    private static readonly string JavaExecutableName = OperatingSystem.IsWindows() ? "java.exe" : "java";

    private readonly IAppPaths _paths;
    private readonly ILogger<JavaLocator> _logger;

    public JavaLocator(IAppPaths paths, ILogger<JavaLocator> logger)
    {
        _paths = paths;
        _logger = logger;
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
            AddHomeChildren(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } programFiles
                ? Path.Combine(programFiles, "Java")
                : null, depth: 1);
            AddHomeChildren(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } programFiles2
                ? Path.Combine(programFiles2, "Eclipse Adoptium")
                : null, depth: 1);
            AddHomeChildren(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } programFiles3
                ? Path.Combine(programFiles3, "Microsoft")
                : null, depth: 1);
            AddHomeChildren(candidates, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } localData
                ? Path.Combine(localData, "Programs", "Eclipse Adoptium")
                : null, depth: 1);
            AddHomeChildren(candidates, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } userProfile
                ? Path.Combine(userProfile, ".jdks")
                : null, depth: 1);
        }
        else
        {
            AddHomeChildren(candidates, "/usr/lib/jvm", depth: 1);
            AddHomeChildren(candidates, "/usr/java", depth: 1);
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

        var executable = ResolveExecutable(path);
        return executable is null ? null : CreateRuntime(executable);
    }

    private static string? ResolveExecutable(string path)
    {
        if (File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        if (Directory.Exists(path))
        {
            var candidate = Path.Combine(path, "bin", JavaExecutableName);
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }

        return null;
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
            vendor = Path.GetFileName(Path.GetDirectoryName(home));
        }

        return new JavaRuntime(home, executable, vendor, architecture, version);
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
