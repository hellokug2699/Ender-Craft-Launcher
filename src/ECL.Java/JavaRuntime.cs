// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

public sealed record JavaRuntime(
    string HomePath,
    string ExecutablePath,
    string? Vendor,
    string? Architecture,
    JavaVersion? Version,
    JavaRuntimeSource Source = JavaRuntimeSource.Unknown,
    bool IsManaged = false)
{
    public bool Is64Bit => Architecture is not null &&
        (Architecture.Contains("64", StringComparison.OrdinalIgnoreCase) ||
         Architecture.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
         Architecture.Contains("arm64", StringComparison.OrdinalIgnoreCase));

    public bool IsKnown32Bit => Architecture is not null &&
        (Architecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
         Architecture.Equals("i386", StringComparison.OrdinalIgnoreCase) ||
         Architecture.Equals("i486", StringComparison.OrdinalIgnoreCase) ||
         Architecture.Equals("i586", StringComparison.OrdinalIgnoreCase) ||
         Architecture.Equals("i686", StringComparison.OrdinalIgnoreCase) ||
         Architecture.Equals("x86_32", StringComparison.OrdinalIgnoreCase) ||
         Architecture.Equals("x86-32", StringComparison.OrdinalIgnoreCase));

    public bool IsSupportedArchitecture => !IsKnown32Bit;

    public string DisplayName => Version is null
        ? Path.GetFileName(HomePath)
        : $"{Version.Raw} · {Vendor ?? "Java"}";
}

public enum JavaRuntimeSource
{
    Unknown,
    Path,
    JavaHome,
    SystemLocation,
    EclManaged,
    UserImported,
    LauncherBundled,
}
