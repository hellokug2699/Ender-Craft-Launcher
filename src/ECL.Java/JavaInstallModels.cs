// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Ecl.Java;

public enum JavaInstallStage
{
    Index,
    Manifest,
    Provider,
    Download,
    Extract,
    Files,
    Completed,
}

public sealed record JavaInstallProgress(
    JavaInstallStage Stage,
    string Message,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string? CurrentFile)
{
    public bool IsIndeterminate => TotalFiles == 0;

    public double Percent => TotalFiles > 0
        ? Math.Clamp(CompletedFiles * 100.0 / TotalFiles, 0, 100)
        : 0;
}

public interface IJavaInstallService
{
    Task<Ecl.Core.Result<JavaRuntime>> EnsureAsync(
        JavaRequirement requirement,
        IProgress<JavaInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Ecl.Core.Result<JavaRuntime>> DownloadAsync(
        JavaRequirement requirement,
        IProgress<JavaInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public static class JavaPlatform
{
    public const string OfficialRuntimeIndexUrl =
        "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    /// <summary>
    /// 返回 Mojang Java 运行时索引中的平台键。Linux ARM64 等平台没有 Mojang 运行时，返回 false。
    /// </summary>
    public static bool TryGetIndexPlatformKey(out string platformKey)
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "unknown",
        };

        if (OperatingSystem.IsWindows())
        {
            platformKey = $"windows-{arch}";
            return arch is "x64" or "x86" or "arm64";
        }

        if (OperatingSystem.IsMacOS())
        {
            platformKey = arch == "arm64" ? "mac-os-arm64" : "mac-os";
            return arch is "x64" or "arm64";
        }

        if (arch == "x86")
        {
            platformKey = "linux-i386";
            return true;
        }

        if (arch == "x64")
        {
            platformKey = "linux";
            return true;
        }

        platformKey = $"linux-{arch}";
        return false;
    }

    public static string GetIndexPlatformKey() =>
        TryGetIndexPlatformKey(out var platformKey)
            ? platformKey
            : throw new PlatformNotSupportedException($"Mojang 不提供 {DescribeCurrentPlatform()} 的 Java 运行时。");

    public static string DescribeCurrentPlatform()
    {
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var os = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS()
                ? "macos"
                : "linux";
        return $"{os}-{arch}";
    }
}
