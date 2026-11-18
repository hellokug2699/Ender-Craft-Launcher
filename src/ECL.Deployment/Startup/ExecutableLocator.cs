// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Deployment.Startup;

/// <summary>
/// 解析“登录启动时应执行的程序”。只有稳定的可执行文件才能写进 Run 项：
/// 写 <c>dotnet ECL.dll</c> 之类的宿主路径会在升级或移动目录后失效，因此明确报告不支持。
/// </summary>
public interface IExecutableLocator
{
    /// <summary>可用于登录启动的绝对路径；不支持时返回 null。</summary>
    string? ResolveStartupExecutable();

    /// <summary>当前进程由 <c>dotnet.exe</c> 承载（开发构建 / CLI），没有稳定的启动入口。</summary>
    bool IsDotnetHosted { get; }
}

/// <summary>
/// 安装布局优先：<c>&lt;install&gt;\ECL.Bootstrapper.exe</c> 是升级与回滚期间始终存在的入口，
/// 因此比 <c>versions\&lt;version&gt;\ECL.exe</c> 更稳定。找不到 Bootstrapper 时回退到当前进程
/// 可执行文件（便携版 / 单文件发布），仍然拒绝 dotnet 宿主。
/// </summary>
public sealed class InstallExecutableLocator : IExecutableLocator
{
    public const string BootstrapperFileName = "ECL.Bootstrapper.exe";

    /// <summary>安装布局中 <c>versions\&lt;version&gt;\</c> 距安装根最多两级，再向上就是用户目录，不应继续探测。</summary>
    private const int MaxParentDepth = 3;

    private readonly string _baseDirectory;
    private readonly string? _processPath;
    private readonly Func<string, bool> _fileExists;
    private readonly Lazy<string?> _resolved;

    public InstallExecutableLocator(
        string? baseDirectory = null,
        string? processPath = null,
        Func<string, bool>? fileExists = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        _processPath = string.IsNullOrWhiteSpace(processPath) ? Environment.ProcessPath : processPath;
        _fileExists = fileExists ?? File.Exists;
        IsDotnetHosted = string.Equals(
            Path.GetFileNameWithoutExtension(_processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
        _resolved = new Lazy<string?>(ResolveCore);
    }

    public bool IsDotnetHosted { get; }

    public string? ResolveStartupExecutable() => IsDotnetHosted ? null : _resolved.Value;

    private string? ResolveCore()
    {
        var directory = SafeFullPath(_baseDirectory);
        for (var depth = 0; depth <= MaxParentDepth && !string.IsNullOrEmpty(directory); depth++)
        {
            var candidate = Path.Combine(directory, BootstrapperFileName);
            if (_fileExists(candidate))
            {
                return candidate;
            }

            var parent = Path.GetDirectoryName(directory);
            directory = string.IsNullOrEmpty(parent) ? string.Empty : parent;
        }

        return string.IsNullOrWhiteSpace(_processPath) ? null : SafeFullPath(_processPath);
    }

    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
