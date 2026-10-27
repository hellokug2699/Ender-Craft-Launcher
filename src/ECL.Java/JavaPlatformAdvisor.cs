// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Ecl.Java;

public enum JavaPlatformIssueKind
{
    Java32BitOn64BitOs,
    Java64BitOn32BitOs,
    X64JavaOnArm64Os,
    MemoryTooHighFor32BitJava,
    MemoryExceedsPhysical,
}

public sealed record JavaPlatformIssue(
    JavaPlatformIssueKind Kind,
    long? MemoryMb = null,
    long? LimitMb = null);

/// <summary>
/// 32/64 位与内存需求校验。只产生警告，不阻止用户启动；真正的 Java 兼容性仍由启动流水线决定。
/// </summary>
public static class JavaPlatformAdvisor
{
    public static IReadOnlyList<JavaPlatformIssue> Assess(
        JavaRuntime? runtime,
        int memoryMb,
        Architecture? osArchitecture = null,
        long? physicalMemoryMb = null)
    {
        if (runtime is null)
        {
            return [];
        }

        var issues = new List<JavaPlatformIssue>();
        var osArch = osArchitecture ?? RuntimeInformation.OSArchitecture;
        var javaArch = runtime.Architecture;

        var os64 = osArch is Architecture.X64 or Architecture.Arm64;
        var os32 = osArch is Architecture.X86 or Architecture.Arm;
        var osArm64 = osArch == Architecture.Arm64;

        if (Is32Bit(javaArch) && os64)
        {
            issues.Add(new JavaPlatformIssue(JavaPlatformIssueKind.Java32BitOn64BitOs));
        }

        if (Is64Bit(javaArch) && os32)
        {
            issues.Add(new JavaPlatformIssue(JavaPlatformIssueKind.Java64BitOn32BitOs));
        }

        if (osArm64 && IsX64(javaArch))
        {
            issues.Add(new JavaPlatformIssue(JavaPlatformIssueKind.X64JavaOnArm64Os));
        }

        if (Is32Bit(javaArch) && memoryMb > 1024)
        {
            issues.Add(new JavaPlatformIssue(
                JavaPlatformIssueKind.MemoryTooHighFor32BitJava,
                memoryMb,
                LimitMb: 1024));
        }

        var physical = physicalMemoryMb ?? DetectPhysicalMemoryMb();
        if (physical > 0 && memoryMb > physical)
        {
            issues.Add(new JavaPlatformIssue(
                JavaPlatformIssueKind.MemoryExceedsPhysical,
                memoryMb,
                physical));
        }

        return issues;
    }

    public static long DetectPhysicalMemoryMb()
    {
        try
        {
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return bytes > 0 ? bytes / 1024 / 1024 : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static bool Is32Bit(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
        {
            return false;
        }

        var value = architecture.ToLowerInvariant();
        return value.Contains("32", StringComparison.Ordinal) ||
               value.Contains("i386", StringComparison.Ordinal) ||
               value.Contains("i686", StringComparison.Ordinal) ||
               (value.Contains("x86", StringComparison.Ordinal) && !value.Contains("64", StringComparison.Ordinal)) ||
               (value.Contains("arm", StringComparison.Ordinal) &&
                !value.Contains("64", StringComparison.Ordinal) &&
                !value.Contains("aarch64", StringComparison.Ordinal));
    }

    public static bool Is64Bit(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
        {
            return false;
        }

        var value = architecture.ToLowerInvariant();
        return value.Contains("64", StringComparison.Ordinal) ||
               value.Contains("aarch64", StringComparison.Ordinal);
    }

    public static bool IsX64(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
        {
            return false;
        }

        var value = architecture.ToLowerInvariant();
        return value.Contains("x86_64", StringComparison.Ordinal) ||
               value.Contains("amd64", StringComparison.Ordinal) ||
               value.Contains("x64", StringComparison.Ordinal);
    }
}
