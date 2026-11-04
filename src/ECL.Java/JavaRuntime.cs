// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

public sealed record JavaRuntime(
    string HomePath,
    string ExecutablePath,
    string? Vendor,
    string? Architecture,
    JavaVersion? Version,
    JavaRuntimeSource Source = JavaRuntimeSource.Unknown,
    bool IsManaged = false,
    JavaBitness? Bitness = null)
{
    /// <summary>
    /// 位数优先使用探测时记录的结果；没有记录时再根据架构字符串推断。
    /// 不把 Unknown 当作 32 位或 64 位，避免误放行或误拒绝。
    /// </summary>
    public JavaBitness ResolvedBitness =>
        Bitness is { } known && known != JavaBitness.Unknown
            ? known
            : JavaArchitecture.Classify(Architecture);

    public bool Is64Bit => ResolvedBitness == JavaBitness.Bit64;

    public bool IsKnown32Bit => ResolvedBitness == JavaBitness.Bit32;

    public bool IsSupportedArchitecture => ResolvedBitness != JavaBitness.Bit32;

    public string DisplayName => Version is null
        ? Path.GetFileName(HomePath)
        : $"{Version.Raw} · {Vendor ?? "Java"}";

    public string ArchitectureLabel => Architecture ?? JavaArchitecture.Describe(ResolvedBitness);
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
