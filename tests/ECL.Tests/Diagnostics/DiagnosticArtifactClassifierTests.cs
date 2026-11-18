// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Diagnostics;
using Xunit;

namespace Ecl.Tests.Diagnostics;

/// <summary>
/// 证据分类契约：文件名不是证据；与内容签名冲突时必须返回 Ambiguous，
/// 内容不足时返回 Unknown，不能按文件名伪造客户端/服务端归属。
/// </summary>
public sealed class DiagnosticArtifactClassifierTests
{
    private const string GameLog = """
        [10:00:00] [main/INFO]: Starting integrated server
        [10:00:01] [Render thread/ERROR]: Cannot invoke "Foo.bar()"
        """;

    private const string ClientCrash = """
        ---- Minecraft Crash Report ----
        // Don't be sad, have a hug! <3

        Time: 2026-11-01T10:00:00Z
        Description: Rendering entity in world

        java.lang.NullPointerException: test

        -- System Details --
        Details:
            Minecraft Version: 1.21.1
            Side: client
        """;

    private const string ServerCrash = """
        ---- Minecraft Crash Report ----
        // Uh oh.

        Description: Exception ticking world

        java.lang.IllegalStateException: test

        -- System Details --
        Details:
            Minecraft Version: 1.21.1
            Side: server
        """;

    [Fact]
    public void FileNameAndContentConflictIsAmbiguous()
    {
        var classification = DiagnosticArtifactClassifier.Classify("hs_err_pid1234.log", GameLog);

        Assert.Equal(DiagnosticArtifactClassification.Ambiguous, classification);
    }

    [Fact]
    public void UnknownTextWithoutAnySignatureIsUnknown()
    {
        var classification = DiagnosticArtifactClassifier.Classify("evidence.bin", "hello, world");

        Assert.Equal(DiagnosticArtifactClassification.Unknown, classification);
    }

    [Fact]
    public void ClientAndServerCrashReportsAreDistinguished()
    {
        Assert.Equal(
            DiagnosticArtifactClassification.ClientCrashReport,
            DiagnosticArtifactClassifier.Classify("crash-2026-11-01_10.00.00-client.txt", ClientCrash));
        Assert.Equal(
            DiagnosticArtifactClassification.ServerCrashReport,
            DiagnosticArtifactClassifier.Classify("crash-2026-11-01_10.00.00-server.txt", ServerCrash));
    }

    [Fact]
    public void CrashHeaderWithoutSideIsNotGuessedAsClientOrServer()
    {
        const string headerOnly = """
            ---- Minecraft Crash Report ----
            Description: something failed
            """;

        var classification = DiagnosticArtifactClassifier.Classify("crash-2026-11-01.txt", headerOnly);

        Assert.Equal(DiagnosticArtifactClassification.Unknown, classification);
    }

    [Fact]
    public void MatchingFileNameAndContentIsClassifiedByContent()
    {
        Assert.Equal(
            DiagnosticArtifactClassification.GameLog,
            DiagnosticArtifactClassifier.Classify("latest.log", GameLog));
        Assert.Equal(
            DiagnosticArtifactClassification.JvmFatalError,
            DiagnosticArtifactClassifier.Classify(
                "hs_err_pid1.log",
                "# A fatal error has been detected by the Java Runtime Environment:\n#  SIGSEGV (0xb)"));
    }
}
