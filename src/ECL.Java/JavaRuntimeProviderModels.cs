// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Java;

public enum JavaArchiveKind
{
    Zip,
    TarGz,
}

/// <summary>
/// 第三方 Java 运行时提供方解析出的一个可下载安装包。
/// </summary>
public sealed record JavaRuntimePackage(
    string Provider,
    string Component,
    string DisplayName,
    string FileName,
    string DownloadUrl,
    string? Sha1,
    string? Sha256,
    long? Size,
    JavaVersion? Version,
    JavaArchiveKind ArchiveKind);

public interface IJavaRuntimeProvider
{
    string Id { get; }

    string DisplayName { get; }

    Task<Result<JavaRuntimePackage>> ResolveAsync(
        JavaRequirement requirement,
        CancellationToken cancellationToken = default);
}

public sealed record JavaProviderPlatform(
    string Os,
    string Arch,
    int Bitness,
    JavaArchiveKind ArchiveKind)
{
    public static bool TryGet(out JavaProviderPlatform platform)
    {
        var (os, arch, bitness, archive) = (string.Empty, string.Empty, 0, JavaArchiveKind.Zip);

        if (OperatingSystem.IsWindows())
        {
            (arch, bitness, archive) = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => ("x64", 64, JavaArchiveKind.Zip),
                System.Runtime.InteropServices.Architecture.X86 => ("x86", 32, JavaArchiveKind.Zip),
                System.Runtime.InteropServices.Architecture.Arm64 => ("arm64", 64, JavaArchiveKind.Zip),
                _ => (string.Empty, 0, JavaArchiveKind.Zip),
            };
            os = "windows";
        }
        else if (OperatingSystem.IsMacOS())
        {
            (arch, bitness, archive) = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => ("x64", 64, JavaArchiveKind.TarGz),
                System.Runtime.InteropServices.Architecture.Arm64 => ("arm64", 64, JavaArchiveKind.TarGz),
                _ => (string.Empty, 0, JavaArchiveKind.TarGz),
            };
            os = "macos";
        }
        else if (OperatingSystem.IsLinux())
        {
            (arch, bitness, archive) = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => ("x64", 64, JavaArchiveKind.TarGz),
                System.Runtime.InteropServices.Architecture.X86 => ("x86", 32, JavaArchiveKind.TarGz),
                System.Runtime.InteropServices.Architecture.Arm64 => ("arm64", 64, JavaArchiveKind.TarGz),
                _ => (string.Empty, 0, JavaArchiveKind.TarGz),
            };
            os = "linux";
        }

        platform = new JavaProviderPlatform(os, arch, bitness, archive);
        return arch.Length > 0;
    }
}
