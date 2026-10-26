// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

public sealed record JavaRuntime(
    string HomePath,
    string ExecutablePath,
    string? Vendor,
    string? Architecture,
    JavaVersion? Version)
{
    public bool Is64Bit =>
        Architecture is not null &&
        (Architecture.Contains("64", StringComparison.OrdinalIgnoreCase) ||
         Architecture.Contains("aarch64", StringComparison.OrdinalIgnoreCase));

    public string DisplayName => Version is null
        ? Path.GetFileName(HomePath)
        : $"{Version.Raw} · {Vendor ?? "Java"}";
}
