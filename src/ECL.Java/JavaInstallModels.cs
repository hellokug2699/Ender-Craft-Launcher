// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Ecl.Java;

public enum JavaInstallStage
{
    Index,
    Manifest,
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

    public static string GetIndexPlatformKey()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

        if (OperatingSystem.IsWindows())
        {
            return $"windows-{arch}";
        }

        if (OperatingSystem.IsMacOS())
        {
            return arch == "arm64" ? "mac-os-arm64" : "mac-os";
        }

        return arch == "x86" ? "linux-i386" : "linux";
    }
}
