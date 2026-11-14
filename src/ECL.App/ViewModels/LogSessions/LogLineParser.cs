// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Ecl.App.ViewModels.LogSessions;

/// <summary>
/// 游戏日志行的保守解析。只识别常见且可验证的时间戳/级别/线程/Logger 形态；
/// 无法解析时保留原始整行，不猜测字段，也不隐藏内容（logs.md §3.4）。
/// </summary>
public static partial class LogLineParser
{
    public static ParsedLogLine Parse(string raw, DateTimeOffset sessionCreatedAt)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var match = BracketedPattern().Match(raw);
        if (!match.Success)
        {
            match = BracketedBracketTimePattern().Match(raw);
        }

        if (!match.Success)
        {
            match = TimestampFirstPattern().Match(raw);
        }

        if (!match.Success)
        {
            match = LevelFirstPattern().Match(raw);
        }

        if (!match.Success)
        {
            return new ParsedLogLine(raw, null, LogLevel.None, null, null, false);
        }

        var level = ParseLevel(match.Groups["level"].Value);
        var timestamp = ParseTimestamp(match.Groups["time"].Value, sessionCreatedAt);
        var thread = match.Groups["thread"].Success ? match.Groups["thread"].Value : null;
        var logger = match.Groups["logger"].Success ? match.Groups["logger"].Value : null;
        var message = match.Groups["message"].Value;
        return new ParsedLogLine(raw, timestamp, level, thread, logger, true, message);
    }

    public static LogLevel ParseLevel(string text) =>
        text.Trim().ToUpperInvariant() switch
        {
            "TRACE" => LogLevel.Trace,
            "DEBUG" => LogLevel.Debug,
            "INFO" => LogLevel.Information,
            "WARN" or "WARNING" => LogLevel.Warning,
            "ERROR" or "SEVERE" => LogLevel.Error,
            "FATAL" or "CRITICAL" => LogLevel.Critical,
            _ => LogLevel.None,
        };

    public static string LevelText(LogLevel level) =>
        level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => string.Empty,
        };

    private static DateTimeOffset? ParseTimestamp(string value, DateTimeOffset fallbackDate)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var full) &&
            (value.Contains('-', StringComparison.Ordinal) || value.Contains('/', StringComparison.Ordinal)))
        {
            return full;
        }

        var formats = new[]
        {
            "HH:mm:ss.fff",
            "HH:mm:ss,fff",
            "HH:mm:ss",
        };

        if (DateTime.TryParseExact(
                value.Trim(),
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            // 只有时间时以会话创建日期补全日期；跨午夜的交接日无法从行本身证明，
            // 因此不把时间范围过滤当作崩溃判定依据。
            var local = fallbackDate.ToLocalTime().Date.Add(time.TimeOfDay);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }

        return null;
    }

    // [12:34:56] [main/INFO]: message
    [GeneratedRegex(
        @"^\[(?<time>\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,3})?)\]\s+\[(?<thread>[^\]/]+)/(?<level>[A-Za-z]+)\](?:\s+\[(?<logger>[^\]]+)\])?:\s?(?<message>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BracketedPattern();

    // 12:34:56.123 [main/INFO] [Logger]: message
    [GeneratedRegex(
        @"^(?<time>\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,3})?)\s+\[(?<thread>[^\]/]+)/(?<level>[A-Za-z]+)\](?:\s+\[(?<logger>[^\]]+)\])?:\s?(?<message>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BracketedBracketTimePattern();

    // 2026-11-14 12:34:56 [main/INFO] message / 2026-11-14T12:34:56.123 [INFO] message
    [GeneratedRegex(
        @"^(?<time>\d{4}-\d{2}-\d{2}[T ]\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,3})?)\s+\[?(?:(?<thread>[^\]/]+)/(?<level>[A-Za-z]+)|(?<level>[A-Za-z]+))\]?(?:\s+\[(?<logger>[^\]]+)\])?:?\s?(?<message>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimestampFirstPattern();

    // 12:34:56 [INFO] [Logger] message
    [GeneratedRegex(
        @"^(?<time>\d{1,2}:\d{2}:\d{2}(?:[.,]\d{1,3})?)\s+\[(?<level>[A-Za-z]+)\](?:\s+\[(?<logger>[^\]]+)\])?:?\s?(?<message>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LevelFirstPattern();
}

/// <summary>一行日志的解析结果；字段缺失时保留原始文本。</summary>
public sealed record ParsedLogLine(
    string Raw,
    DateTimeOffset? Timestamp,
    LogLevel Level,
    string? Thread,
    string? Logger,
    bool IsParsed,
    string? Message = null)
{
    public string DisplayMessage => Message ?? Raw;
}
