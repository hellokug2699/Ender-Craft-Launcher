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
/// 平台与内存诊断。32 位 Java 的拒绝由选择器和启动步骤负责；
/// 这里保留可解释的平台/内存提示，避免把架构禁止伪装成普通警告。
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
        var java32 = runtime.IsKnown32Bit;
        var java64 = runtime.Is64Bit;

        var os64 = osArch is Architecture.X64 or Architecture.Arm64;
        var os32 = osArch is Architecture.X86 or Architecture.Arm;
        var osArm64 = osArch == Architecture.Arm64;

        if (java32 && os64)
        {
            issues.Add(new JavaPlatformIssue(JavaPlatformIssueKind.Java32BitOn64BitOs));
        }

        if (java64 && os32)
        {
            issues.Add(new JavaPlatformIssue(JavaPlatformIssueKind.Java64BitOn32BitOs));
        }

        if (osArm64 && IsX64(javaArch))
        {
            issues.Add(new JavaPlatformIssue(JavaPlatformIssueKind.X64JavaOnArm64Os));
        }

        if (java32 && memoryMb > 1024)
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

    public static bool Is32Bit(string? architecture) => JavaArchitecture.Is32Bit(architecture);

    public static bool Is64Bit(string? architecture) => JavaArchitecture.Is64Bit(architecture);

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
