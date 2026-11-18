// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Diagnostics;
using Xunit;

namespace Ecl.Tests.Diagnostics;

/// <summary>
/// hs_err 解析契约：识别文件头/信号/Problematic frame 与行号；不是 hs_err 或空文件时
/// 只返回 HasHeader=false 与警告，不抛异常阻塞诊断。
/// </summary>
public sealed class JvmFatalErrorParserTests
{
    [Fact]
    public void ParsesSigSegvProblematicFrameAndLineNumbers()
    {
        const string text = """
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  SIGSEGV (0xb) at pc=0x00007f1234567890, pid=1234, tid=5678
            #
            # JRE version: OpenJDK Runtime Environment (21.0.1+12) (build 21.0.1+12)
            # Java VM: OpenJDK 64-Bit Server VM (21.0.1+12, mixed mode, sharing)
            #
            # Problematic frame:
            # C  [libfoo.so+0x1234]  some_native+0x10
            #
            # Core dump will be written. Default location: /tmp/core
            """;

        var report = JvmFatalErrorParser.Parse("hs_err_pid1234.log", text);

        Assert.True(report.HasHeader);
        Assert.Equal("SIGSEGV", report.Signal);
        Assert.Equal("C  [libfoo.so+0x1234]  some_native+0x10", report.ProblematicFrame);
        Assert.Equal(9, report.ProblematicFrameLine);
        Assert.Contains("21.0.1", report.JvmVersion);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void ParsesWindowsAccessViolation()
    {
        const string text = """
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ff6aabbccdd, pid=999, tid=888
            #
            # JRE version: Java(TM) SE Runtime Environment (17.0.9+9) (build 17.0.9+9-LTS)
            """;

        var report = JvmFatalErrorParser.Parse("hs_err_pid999.log", text);

        Assert.True(report.HasHeader);
        Assert.Equal("EXCEPTION_ACCESS_VIOLATION", report.Signal);
        Assert.Null(report.ProblematicFrame);
        Assert.Contains(report.Warnings, warning => warning.Contains("Problematic frame", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("this is just a normal text file\nwith two lines")]
    public void NonHsErrInputIsNotRecognizedAndDoesNotThrow(string text)
    {
        var report = JvmFatalErrorParser.Parse("notes.txt", text);

        Assert.False(report.HasHeader);
        Assert.NotEmpty(report.Warnings);
        Assert.Null(report.Signal);
        Assert.Null(report.ProblematicFrame);
    }

    [Fact]
    public void ParseFileReadsBoundedContentAndKeepsSourceNameOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ecl-jvm-fatal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "hs_err_pid42.log");
            File.WriteAllText(path, """
                # A fatal error has been detected by the Java Runtime Environment:
                #
                #  SIGBUS (0x7) at pc=0x1, pid=42, tid=43
                #
                # Problematic frame:
                # C  [libbar.so+0x1]
                """);

            var result = JvmFatalErrorParser.ParseFile(path);

            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.True(result.Value!.HasHeader);
            Assert.Equal("hs_err_pid42.log", result.Value.File);
            Assert.Equal("SIGBUS", result.Value.Signal);
            Assert.Equal("C  [libbar.so+0x1]", result.Value.ProblematicFrame);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
