// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using Ecl.Core;

namespace Ecl.Diagnostics;

/// <summary>
/// Minecraft <c>latest.log</c> 结构化解析器。
/// <para>
/// 识别 <c>[HH:mm:ss] [Thread/LEVEL]: message</c>（可选 <c>[logger]</c>）形式的日志头，
/// 并把后续堆栈行、<c>Caused by</c>、缩进行合并到同一事件。事件保留每一行的真实行号，
/// 供诊断规则输出“文件 + 行号 + 摘录”的证据链。
/// </para>
/// <para>只读取本地文件，不发起网络请求；读取上限由 <see cref="BoundedTextReader"/> 控制。</para>
/// </summary>
public static class LatestLogParser
{
    private static readonly string[] TimeFormats =
    [
        "H:mm:ss",
        "HH:mm:ss",
        "H:mm:ss.f",
        "HH:mm:ss.f",
        "H:mm:ss.ff",
        "HH:mm:ss.ff",
        "H:mm:ss.fff",
        "HH:mm:ss.fff",
    ];

    private static readonly Regex HeadPattern = new(
        @"^\[(?<time>[0-9]{1,2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,3})?)\]\s+\[(?<thread>[^\]/]+)/(?<level>[A-Za-z]+)\](?:\s+\[(?<logger>[^\]]+)\])?:\s?(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// 匹配 Java 异常类型。只接受以 Exception/Error/Throwable 结尾的类名，
    /// 避免把普通英文单词当作异常；不依赖“最后一个异常类”推断根因。
    /// </summary>
    internal static readonly Regex ExceptionPattern = new(
        @"\b(?<type>(?:[A-Za-z_$][A-Za-z0-9_$]*\.)*[A-Z][A-Za-z0-9_$]*(?:Exception|Error|Throwable))(?::[ \t]*(?<message>[^\r\n]*))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>解析一个日志文件；读取失败返回可读错误，不影响其他文件。</summary>
    public static Result<LogParseResult> ParseFile(
        string path,
        int maxBytes = BoundedTextReader.DefaultMaxBytes,
        int maxLines = BoundedTextReader.DefaultMaxLines)
    {
        var read = BoundedTextReader.ReadFile(path, maxBytes, maxLines);
        if (!read.IsSuccess)
        {
            return Result<LogParseResult>.Fail(read.Error!);
        }

        var bounded = read.Value!;
        var result = Parse(path, bounded.Text);
        var warnings = result.Warnings.ToList();
        if (bounded.Truncated)
        {
            warnings.Add("日志文件超过读取上限，诊断只覆盖已读取部分。");
        }

        return Result<LogParseResult>.Ok(result with
        {
            LinesRead = bounded.LinesRead,
            BytesRead = bounded.BytesRead,
            Truncated = bounded.Truncated,
            Warnings = warnings,
        });
    }

    /// <summary>解析已经读入内存的日志文本。不会抛异常；无法识别的行会被保留并记录警告。</summary>
    public static LogParseResult Parse(string sourceName, string text)
    {
        var file = BoundedTextReader.NormalizeSourceName(sourceName);
        var normalized = BoundedTextReader.NormalizeNewlines(text);
        string[] lines = normalized.Length == 0 ? [] : normalized.Split('\n');
        var events = new List<ParsedLogEvent>();
        var warnings = new List<string>();
        var currentHead = (Head?)null;
        var currentLines = new List<SourceLine>();
        var orphanLines = 0;

        void Flush()
        {
            if (currentHead is null)
            {
                return;
            }

            events.Add(BuildEvent(file, currentHead, currentLines));
            currentHead = null;
            currentLines = [];
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var lineNumber = index + 1;
            var match = HeadPattern.Match(raw);
            if (match.Success)
            {
                Flush();
                currentHead = new Head(
                    lineNumber,
                    ParseTime(match.Groups["time"].Value),
                    match.Groups["thread"].Value.Trim(),
                    NormalizeLevel(match.Groups["level"].Value),
                    match.Groups["logger"].Success ? match.Groups["logger"].Value.Trim() : null,
                    match.Groups["message"].Value);
                currentLines.Add(new SourceLine(lineNumber, raw));
                continue;
            }

            if (raw.Trim().Length == 0)
            {
                // 空行不生成事件，也不打断堆栈归属。
                continue;
            }

            if (currentHead is null)
            {
                // 文件开头或损坏处出现无法识别的行：保留为 UNKNOWN 事件，避免静默丢弃证据。
                orphanLines++;
                currentHead = new Head(lineNumber, null, null, "UNKNOWN", null, raw.Trim());
                currentLines.Add(new SourceLine(lineNumber, raw));
                continue;
            }

            // 堆栈行、Caused by、缩进行以及多行异常消息都归入当前事件。
            currentLines.Add(new SourceLine(lineNumber, raw));
        }

        Flush();

        if (orphanLines > 0)
        {
            warnings.Add($"有 {orphanLines} 行的日志头无法识别，已按 UNKNOWN 事件保留。");
        }

        return new LogParseResult
        {
            File = file,
            Events = events,
            LinesRead = lines.Length,
            BytesRead = 0,
            Truncated = false,
            Warnings = warnings,
        };
    }

    private static ParsedLogEvent BuildEvent(string file, Head head, List<SourceLine> lines)
    {
        var exceptionTypes = new List<string>();
        string? exceptionType = null;
        string? exceptionMessage = null;

        foreach (var line in lines)
        {
            // 首行只检查消息部分，避免把时间/线程中的字符误判为异常。
            var candidate = line.Number == head.Line && head.Message.Length > 0
                ? head.Message
                : line.Text;

            var match = TryMatchException(candidate);
            if (match is null)
            {
                continue;
            }

            var type = match.Groups["type"].Value;
            if (exceptionType is null)
            {
                exceptionType = type;
                var message = match.Groups["message"].Value.Trim();
                exceptionMessage = message.Length == 0 ? null : message;
            }

            if (!exceptionTypes.Contains(type, StringComparer.Ordinal))
            {
                exceptionTypes.Add(type);
            }
        }

        return new ParsedLogEvent
        {
            File = file,
            SourceKind = DiagnosticSourceKind.GameLog,
            Line = head.Line,
            EndLine = lines.Count == 0 ? head.Line : lines[^1].Number,
            Time = head.Time,
            Thread = head.Thread,
            Level = head.Level,
            Logger = head.Logger,
            Message = head.Message,
            ExceptionType = exceptionType,
            ExceptionMessage = exceptionMessage,
            ExceptionTypes = exceptionTypes,
            Lines = lines,
        };
    }

    private static TimeOnly? ParseTime(string value) =>
        TimeOnly.TryParseExact(
            value,
            TimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var time)
            ? time
            : null;

    private static string NormalizeLevel(string value) =>
        string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim().ToUpperInvariant();

    internal static Match? TryMatchException(string text)
    {
        try
        {
            var match = ExceptionPattern.Match(text);
            return match.Success ? match : null;
        }
        catch (RegexMatchTimeoutException)
        {
            // 异常文本过长或构造异常时，放弃这一行的异常识别，不影响事件本身。
            return null;
        }
    }

    private sealed record Head(
        int Line,
        TimeOnly? Time,
        string? Thread,
        string Level,
        string? Logger,
        string Message);
}
