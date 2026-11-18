// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace Ecl.Diagnostics;

/// <summary>诊断证据文件的分类结果。无法确认时保持 <see cref="Unknown"/>，不从文件名虚构内容。</summary>
public enum DiagnosticArtifactClassification
{
    Unknown,
    Ambiguous,
    GameLog,
    ClientCrashReport,
    ServerCrashReport,
    JvmFatalError,
}

/// <summary>
/// 诊断证据分类器：结合文件名提示与有限内容签名判断文件类型。
/// <para>
/// 文件名本身不是证据：当文件名提示与内容签名冲突时返回 <see cref="DiagnosticArtifactClassification.Ambiguous"/>，
/// 让调用方决定跳过或提示用户，而不是按文件名猜成某一类。内容不足时返回 <see cref="DiagnosticArtifactClassification.Unknown"/>。
/// </para>
/// <para>只检查调用方传入的有界头部文本，不读取文件、不访问网络。</para>
/// </summary>
public static class DiagnosticArtifactClassifier
{
    /// <summary>建议调用方传入的头部字符上限；分类只依赖前部签名。</summary>
    public const int HeadCharLimit = 16 * 1024;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex GameLogHeadPattern = new(
        @"^\[\d{1,2}:\d{2}:\d{2}(?:\.\d{1,3})?\]\s+\[[^\]/]+/(?:INFO|WARN|ERROR|DEBUG|TRACE|FATAL)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline,
        RegexTimeout);

    private static readonly string[] FatalSignalMarkers =
    [
        "SIGSEGV",
        "SIGBUS",
        "SIGILL",
        "SIGFPE",
        "EXCEPTION_ACCESS_VIOLATION",
        "EXCEPTION_ILLEGAL_INSTRUCTION",
        "EXCEPTION_STACK_OVERFLOW",
    ];

    private static readonly string[] ClientMarkers =
    [
        "Side: client",
        "Render thread",
        "net.minecraft.client",
        "Minecraft Client",
        "LWJGL",
    ];

    private static readonly string[] ServerMarkers =
    [
        "Side: server",
        "Dedicated server",
        "DedicatedServer",
        "net.minecraft.server",
        "server.properties",
        "Server thread",
    ];

    /// <summary>分类一个证据文件。<paramref name="head"/> 允许被截断，调用方应只传入前部文本。</summary>
    public static DiagnosticArtifactClassification Classify(string fileName, ReadOnlySpan<char> head)
    {
        var fileNameHint = HintFromFileName(fileName);
        var contentClassification = ClassifyContent(head);

        if (contentClassification == DiagnosticArtifactClassification.Ambiguous)
        {
            return DiagnosticArtifactClassification.Ambiguous;
        }

        if (contentClassification != DiagnosticArtifactClassification.Unknown)
        {
            return HasFileNameConflict(fileNameHint, contentClassification)
                ? DiagnosticArtifactClassification.Ambiguous
                : contentClassification;
        }

        // 崩溃报告头存在但缺少客户端/服务端归属签名时，ClassifyContent 返回 Unknown；
        // 此处不能仅凭文件名把它猜成客户端或服务端。
        return fileNameHint switch
        {
            FileNameHint.GameLog => DiagnosticArtifactClassification.GameLog,
            FileNameHint.JvmFatalError => DiagnosticArtifactClassification.JvmFatalError,
            _ => DiagnosticArtifactClassification.Unknown,
        };
    }

    private static DiagnosticArtifactClassification ClassifyContent(ReadOnlySpan<char> head)
    {
        if (head.IsEmpty || head.IsWhiteSpace())
        {
            return DiagnosticArtifactClassification.Unknown;
        }

        if (LooksLikeJvmFatal(head))
        {
            return DiagnosticArtifactClassification.JvmFatalError;
        }

        if (LooksLikeCrashReport(head))
        {
            var hasClient = ContainsAny(head, ClientMarkers);
            var hasServer = ContainsAny(head, ServerMarkers);
            if (hasClient && hasServer)
            {
                return DiagnosticArtifactClassification.Ambiguous;
            }

            if (hasClient)
            {
                return DiagnosticArtifactClassification.ClientCrashReport;
            }

            if (hasServer)
            {
                return DiagnosticArtifactClassification.ServerCrashReport;
            }

            // 只有崩溃报告头、没有 Side 等归属签名时，无法诚实地区分客户端/服务端。
            return DiagnosticArtifactClassification.Unknown;
        }

        if (LooksLikeGameLog(head))
        {
            return DiagnosticArtifactClassification.GameLog;
        }

        return DiagnosticArtifactClassification.Unknown;
    }

    private static bool LooksLikeJvmFatal(ReadOnlySpan<char> head)
    {
        if (ContainsIgnoreCase(head, "# A fatal error has been detected"))
        {
            return true;
        }

        foreach (var rawLine in head.ToString().Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (!trimmed.StartsWith('#'))
            {
                continue;
            }

            foreach (var marker in FatalSignalMarkers)
            {
                if (trimmed.Contains(marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool LooksLikeCrashReport(ReadOnlySpan<char> head) =>
        ContainsIgnoreCase(head, "---- Minecraft Crash Report ----") ||
        ContainsIgnoreCase(head, "-- System Details --");

    private static bool LooksLikeGameLog(ReadOnlySpan<char> head)
    {
        try
        {
            return GameLogHeadPattern.IsMatch(head.ToString());
        }
        catch (RegexMatchTimeoutException)
        {
            // 分类超时按“未知”处理，不让可疑文本阻塞诊断。
            return false;
        }
    }

    private static bool HasFileNameConflict(
        FileNameHint hint,
        DiagnosticArtifactClassification content) => hint switch
    {
        FileNameHint.GameLog => content is not DiagnosticArtifactClassification.GameLog,
        FileNameHint.CrashReport => content is not (
            DiagnosticArtifactClassification.ClientCrashReport or
            DiagnosticArtifactClassification.ServerCrashReport),
        FileNameHint.JvmFatalError => content != DiagnosticArtifactClassification.JvmFatalError,
        _ => false,
    };

    private static FileNameHint HintFromFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FileNameHint.Unknown;
        }

        var name = fileName.Trim();
        if (name.Contains("hs_err", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("jvm_fatal", StringComparison.OrdinalIgnoreCase))
        {
            return FileNameHint.JvmFatalError;
        }

        if (name.Contains("crash-report", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("crashreport", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase))
        {
            return FileNameHint.CrashReport;
        }

        if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("latest.log", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase))
        {
            return FileNameHint.GameLog;
        }

        return FileNameHint.Unknown;
    }

    private static bool ContainsAny(ReadOnlySpan<char> head, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (ContainsIgnoreCase(head, marker))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsIgnoreCase(ReadOnlySpan<char> haystack, string needle) =>
        haystack.IndexOf(needle.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0;

    private enum FileNameHint
    {
        Unknown,
        GameLog,
        CrashReport,
        JvmFatalError,
    }
}
