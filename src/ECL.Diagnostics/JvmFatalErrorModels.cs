// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using Ecl.Core;

namespace Ecl.Diagnostics;

/// <summary>
/// JVM 致命错误（hs_err_pid*.log）的结构化结果。
/// <para>
/// 只提取文件头、信号、Problematic frame、JRE 版本和少量线程/本地库行；
/// 不把“出现 SIGSEGV”直接推断为某个模组的根因，证据与推断仍由规则引擎负责区分。
/// </para>
/// </summary>
public sealed record JvmFatalErrorReport
{
    /// <summary>来源文件名（不保存完整本机路径）。</summary>
    public string File { get; init; } = "";

    /// <summary>是否识别到 <c># A fatal error has been detected</c> 文件头。</summary>
    public bool HasHeader { get; init; }

    /// <summary>进程收到的信号/异常，例如 <c>SIGSEGV</c> 或 <c>EXCEPTION_ACCESS_VIOLATION</c>。</summary>
    public string? Signal { get; init; }

    /// <summary><c># Problematic frame:</c> 后一行的内容（去掉行首注释符号）。</summary>
    public string? ProblematicFrame { get; init; }

    /// <summary>Problematic frame 在原始文件中的 1 基行号；无法定位时为 <c>null</c>。</summary>
    public int? ProblematicFrameLine { get; init; }

    /// <summary><c># JRE version:</c> 的值。</summary>
    public string? JvmVersion { get; init; }

    /// <summary><c># Native libraries:</c> 区段中的有界摘录。</summary>
    public IReadOnlyList<string> NativeLibraries { get; init; } = [];

    /// <summary>当前线程/JavaThread 行的有界摘录，供规则匹配与人工复核。</summary>
    public IReadOnlyList<string> ThreadLines { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// JVM 致命错误解析器。识别失败时返回 <see cref="JvmFatalErrorReport.HasHeader"/>=false 与警告，
/// 绝不抛异常阻塞诊断；读取上限沿用 <see cref="BoundedTextReader"/>，避免超大 hs_err 耗尽内存。
/// </summary>
public static class JvmFatalErrorParser
{
    public const int DefaultMaxBytes = 4 * 1024 * 1024;
    public const int DefaultMaxLines = 100_000;

    private const int MaxCollectedLines = 64;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex SignalPattern = new(
        @"^#\s*(?<signal>SIGSEGV|SIGBUS|SIGILL|SIGFPE|EXCEPTION_ACCESS_VIOLATION|EXCEPTION_ILLEGAL_INSTRUCTION|EXCEPTION_STACK_OVERFLOW)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex FatalHeaderPattern = new(
        @"^#?\s*A fatal error has been detected",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    /// <summary>读取并解析 hs_err 文件；读取失败返回明确错误，不抛异常。</summary>
    public static Result<JvmFatalErrorReport> ParseFile(
        string path,
        int maxBytes = DefaultMaxBytes,
        int maxLines = DefaultMaxLines)
    {
        var read = BoundedTextReader.ReadFile(path, maxBytes, maxLines);
        if (!read.IsSuccess)
        {
            return Result<JvmFatalErrorReport>.Fail(read.Error!);
        }

        var bounded = read.Value!;
        var report = Parse(path, bounded.Text);
        if (!bounded.Truncated)
        {
            return Result<JvmFatalErrorReport>.Ok(report);
        }

        return Result<JvmFatalErrorReport>.Ok(report with
        {
            Warnings = [.. report.Warnings, "JVM 致命错误文件超过读取上限，诊断只覆盖已读取部分。"],
        });
    }

    /// <summary>解析已读入内存的 hs_err 文本。不会抛异常。</summary>
    public static JvmFatalErrorReport Parse(string sourceName, string text)
    {
        var file = BoundedTextReader.NormalizeSourceName(sourceName);
        var normalized = BoundedTextReader.NormalizeNewlines(text);
        string[] lines = normalized.Length == 0 ? [] : normalized.Split('\n');

        var hasHeader = false;
        string? signal = null;
        string? problematicFrame = null;
        int? problematicFrameLine = null;
        string? jvmVersion = null;
        var nativeLibraries = new List<string>();
        var threadLines = new List<string>();
        var warnings = new List<string>();

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var lineNumber = index + 1;
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!hasHeader && FatalHeaderPattern.IsMatch(trimmed))
            {
                hasHeader = true;
                continue;
            }

            if (signal is null)
            {
                var signalMatch = SignalPattern.Match(trimmed);
                if (signalMatch.Success)
                {
                    signal = signalMatch.Groups["signal"].Value;
                }
            }

            if (jvmVersion is null &&
                trimmed.StartsWith("# JRE version:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["# JRE version:".Length..].Trim();
                if (value.Length > 0)
                {
                    jvmVersion = value;
                }
            }

            if (problematicFrame is null &&
                trimmed.StartsWith("# Problematic frame:", StringComparison.OrdinalIgnoreCase))
            {
                for (var next = index + 1; next < lines.Length; next++)
                {
                    var candidate = lines[next].Trim();
                    if (candidate.Length == 0)
                    {
                        continue;
                    }

                    problematicFrame = TrimCommentPrefix(candidate);
                    problematicFrameLine = next + 1;
                    break;
                }

                continue;
            }

            if (trimmed.StartsWith("# Native libraries:", StringComparison.OrdinalIgnoreCase))
            {
                CollectSection(lines, index + 1, nativeLibraries);
                continue;
            }

            if (threadLines.Count < MaxCollectedLines &&
                (trimmed.Contains("Current thread", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.Contains("JavaThread \"", StringComparison.Ordinal) ||
                 trimmed.StartsWith("JavaThread ", StringComparison.Ordinal)))
            {
                threadLines.Add(TrimCommentPrefix(trimmed));
            }
        }

        if (!hasHeader)
        {
            warnings.Add("未找到 JVM 致命错误文件头（# A fatal error has been detected），输入可能不是 hs_err 文件。");
        }
        else if (signal is null)
        {
            warnings.Add("JVM 致命错误文件未包含可识别的信号/异常类型。");
        }

        if (hasHeader && problematicFrame is null)
        {
            warnings.Add("JVM 致命错误文件未包含 Problematic frame。");
        }

        return new JvmFatalErrorReport
        {
            File = file,
            HasHeader = hasHeader,
            Signal = signal,
            ProblematicFrame = problematicFrame,
            ProblematicFrameLine = problematicFrameLine,
            JvmVersion = jvmVersion,
            NativeLibraries = nativeLibraries,
            ThreadLines = threadLines,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// 把致命错误中已识别的行转换成统一事件，供规则引擎按“文件 + 行号”输出证据。
    /// 只有识别到 hs_err 文件头时才产生事件，避免把普通日志当作致命错误。
    /// </summary>
    public static IReadOnlyList<ParsedLogEvent> ToEvents(JvmFatalErrorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.HasHeader)
        {
            return [];
        }

        var lines = new List<SourceLine>
        {
            new(1, "# A fatal error has been detected by the Java Runtime Environment:"),
        };

        if (!string.IsNullOrWhiteSpace(report.Signal))
        {
            lines.Add(new SourceLine(1, $"#  {report.Signal}"));
        }

        if (!string.IsNullOrWhiteSpace(report.JvmVersion))
        {
            lines.Add(new SourceLine(1, $"# JRE version: {report.JvmVersion}"));
        }

        if (!string.IsNullOrWhiteSpace(report.ProblematicFrame))
        {
            var lineNumber = report.ProblematicFrameLine ?? 1;
            lines.Add(new SourceLine(lineNumber, report.ProblematicFrame));
        }

        return
        [
            new ParsedLogEvent
            {
                File = report.File,
                SourceKind = DiagnosticSourceKind.JvmFatalError,
                Line = 1,
                EndLine = lines[^1].Number,
                Level = "FATAL",
                Logger = "jvm-fatal-error",
                Message = "JVM fatal error",
                Lines = lines,
            },
        ];
    }

    private static void CollectSection(string[] lines, int start, List<string> target)
    {
        for (var index = start; index < lines.Length && target.Count < MaxCollectedLines; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.Length == 0)
            {
                break;
            }

            if (!trimmed.StartsWith('#'))
            {
                break;
            }

            target.Add(TrimCommentPrefix(trimmed));
        }
    }

    private static string TrimCommentPrefix(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('#'))
        {
            return trimmed;
        }

        return trimmed[1..].Trim();
    }
}
