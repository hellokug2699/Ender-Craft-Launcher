// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Ecl.Java;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class JavaPlatformAdvisorTests
{
    [Fact]
    public void WarnsWhen32BitJavaRunsOn64BitOs()
    {
        var runtime = Runtime("x86");

        var issues = JavaPlatformAdvisor.Assess(runtime, 2048, Architecture.X64, 16384);

        Assert.Contains(issues, issue => issue.Kind == JavaPlatformIssueKind.Java32BitOn64BitOs);
        Assert.Contains(issues, issue => issue.Kind == JavaPlatformIssueKind.MemoryTooHighFor32BitJava);
    }

    [Fact]
    public void WarnsWhen64BitJavaRunsOn32BitOs()
    {
        var runtime = Runtime("x86_64");

        var issues = JavaPlatformAdvisor.Assess(runtime, 2048, Architecture.X86, 8192);

        Assert.Contains(issues, issue => issue.Kind == JavaPlatformIssueKind.Java64BitOn32BitOs);
    }

    [Fact]
    public void WarnsWhenX64JavaRunsOnArm64Os()
    {
        var runtime = Runtime("x86_64");

        var issues = JavaPlatformAdvisor.Assess(runtime, 4096, Architecture.Arm64, 16384);

        Assert.Contains(issues, issue => issue.Kind == JavaPlatformIssueKind.X64JavaOnArm64Os);
    }

    [Fact]
    public void WarnsWhenMemoryExceedsPhysicalMemory()
    {
        var runtime = Runtime("x86_64");

        var issues = JavaPlatformAdvisor.Assess(runtime, 16384, Architecture.X64, 8192);

        var issue = Assert.Single(issues);
        Assert.Equal(JavaPlatformIssueKind.MemoryExceedsPhysical, issue.Kind);
        Assert.Equal(16384, issue.MemoryMb);
        Assert.Equal(8192, issue.LimitMb);
    }

    [Fact]
    public void Matches64BitJavaOn64BitOsWithoutIssues()
    {
        var runtime = Runtime("x86_64");

        Assert.Empty(JavaPlatformAdvisor.Assess(runtime, 4096, Architecture.X64, 16384));
    }

    [Theory]
    [InlineData("x86", 32)]
    [InlineData("i386", 32)]
    [InlineData("x86_64", 64)]
    [InlineData("aarch64", 64)]
    [InlineData("arm", 32)]
    public void DetectsJavaBitness(string architecture, int expected)
    {
        var is32 = JavaPlatformAdvisor.Is32Bit(architecture);
        var is64 = JavaPlatformAdvisor.Is64Bit(architecture);

        Assert.Equal(expected == 32, is32 && !is64);
        Assert.Equal(expected == 64, is64);
    }

    private static JavaRuntime Runtime(string architecture) =>
        new("/java", "/java/bin/java", "Test", architecture, new JavaVersion(21, 0, 0, "21"));
}
