// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using Ecl.Core;

namespace Ecl.Diagnostics;

/// <summary>
/// Minecraft Crash Report 解析器。
/// <para>
/// 提取 Description、Time、Minecraft/Java/OS 版本、模组列表、异常头与堆栈以及 <c>-- Section --</c> 区段。
/// 所有字段都可能缺失：缺失时保持为 <c>null</c>/空集合并继续解析，不抛异常阻塞宿主。
/// </para>
/// <para>只处理本地文本，不发起网络请求。</para>
/// </summary>
public static class CrashReportParser
{
    public const int DefaultMaxBytes = 4 * 1024 * 1024;
    public const int DefaultMaxLines = 100_000;

    private static readonly Regex SectionPattern = new(
        @"^--\s*(?<name>.+?)\s*--$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>解析崩溃报告文件；读取失败返回可读错误，不影响其他文件。</summary>
    public static Result<CrashReport> ParseFile(
        string path,
        int maxBytes = DefaultMaxBytes,
        int maxLines = DefaultMaxLines)
    {
        var read = BoundedTextReader.ReadFile(path, maxBytes, maxLines);
        if (!read.IsSuccess)
        {
            return Result<CrashReport>.Fail(read.Error!);
        }

        var bounded = read.Value!;
        var report = Parse(path, bounded.Text);
        if (!bounded.Truncated)
        {
            return Result<CrashReport>.Ok(report);
        }

        var warnings = report.Warnings.ToList();
        warnings.Add("崩溃报告超过读取上限，诊断只覆盖已读取部分。");
        return Result<CrashReport>.Ok(report with { Warnings = warnings });
    }

    /// <summary>解析已读入内存的崩溃报告文本。不会抛异常。</summary>
    public static CrashReport Parse(string sourceName, string text)
    {
        var file = BoundedTextReader.NormalizeSourceName(sourceName);
        var normalized = BoundedTextReader.NormalizeNewlines(text);
        string[] lines = normalized.Length == 0 ? [] : normalized.Split('\n');

        var hasHeader = false;
        var warnings = new List<string>();
        DateTimeOffset? time = null;
        string? description = null;
        int? descriptionLine = null;
        string? minecraftVersion = null;
        string? javaVersion = null;
        string? operatingSystem = null;
        string? exceptionType = null;
        string? exceptionMessage = null;
        int? exceptionLine = null;
        var stackTrace = new List<SourceLine>();
        var mods = new List<CrashReportMod>();
        var modIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modListLines = new List<SourceLine>();
        var sections = new List<CrashReportSection>();
        string? sectionName = null;
        var sectionStart = 0;
        var sectionLines = new List<SourceLine>();

        void FlushSection()
        {
            if (sectionName is not null)
            {
                sections.Add(new CrashReportSection(sectionName, sectionStart, sectionLines.ToArray()));
            }

            sectionName = null;
            sectionLines = [];
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var lineNumber = index + 1;
            var trimmed = raw.Trim();

            if (hasHeader is false && trimmed.Contains("Minecraft Crash Report", StringComparison.OrdinalIgnoreCase))
            {
                hasHeader = true;
            }

            var sectionMatch = SectionPattern.Match(trimmed);
            if (sectionMatch.Success)
            {
                FlushSection();
                sectionName = sectionMatch.Groups["name"].Value.Trim();
                sectionStart = lineNumber;
                sectionLines = [new SourceLine(lineNumber, raw)];
                continue;
            }

            if (sectionName is not null)
            {
                sectionLines.Add(new SourceLine(lineNumber, raw));
            }

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (description is null && trimmed.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["Description:".Length..].Trim();
                if (value.Length > 0)
                {
                    description = value;
                    descriptionLine = lineNumber;
                }

                continue;
            }

            if (time is null && TryKeyValue(trimmed, "Time", out var timeText))
            {
                if (DateTimeOffset.TryParse(
                        timeText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal,
                        out var parsedTime))
                {
                    time = parsedTime;
                }
                else
                {
                    warnings.Add($"第 {lineNumber} 行的崩溃时间无法解析：{timeText}");
                }

                continue;
            }

            if (minecraftVersion is null && TryKeyValue(trimmed, "Minecraft Version", out var mcVersion))
            {
                minecraftVersion = mcVersion;
            }

            if (javaVersion is null && TryKeyValue(trimmed, "Java Version", out var java))
            {
                javaVersion = java;
            }

            if (operatingSystem is null && TryKeyValue(trimmed, "Operating System", out var os))
            {
                operatingSystem = os;
            }

            if (IsModHeader(trimmed))
            {
                var headerIndent = CountIndent(raw);
                modListLines.Add(new SourceLine(lineNumber, raw));
                for (var entryIndex = index + 1; entryIndex < lines.Length; entryIndex++)
                {
                    var entryRaw = lines[entryIndex];
                    var entryTrimmed = entryRaw.Trim();
                    if (entryTrimmed.Length == 0 ||
                        entryTrimmed.StartsWith("--", StringComparison.Ordinal) ||
                        CountIndent(entryRaw) <= headerIndent)
                    {
                        break;
                    }

                    var separator = entryTrimmed.IndexOf(':');
                    if (separator <= 0)
                    {
                        break;
                    }

                    var id = entryTrimmed[..separator].Trim();
                    var rest = entryTrimmed[(separator + 1)..].Trim();
                    if (id.Length == 0 || id.Any(char.IsWhiteSpace))
                    {
                        break;
                    }

                    modListLines.Add(new SourceLine(entryIndex + 1, entryRaw));
                    if (modIds.Add(id))
                    {
                        mods.Add(ParseMod(id, rest));
                    }
                }

                continue;
            }

            if (trimmed.StartsWith("at ", StringComparison.Ordinal) ||
                trimmed.StartsWith("...", StringComparison.Ordinal) ||
                trimmed.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Suppressed:", StringComparison.OrdinalIgnoreCase))
            {
                stackTrace.Add(new SourceLine(lineNumber, raw));
                if (exceptionType is null &&
                    (trimmed.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("Suppressed:", StringComparison.OrdinalIgnoreCase)))
                {
                    var match = LatestLogParser.TryMatchException(trimmed);
                    if (match is not null)
                    {
                        exceptionType = match.Groups["type"].Value;
                        exceptionMessage = TrimmedOrNull(match.Groups["message"].Value);
                        exceptionLine = lineNumber;
                    }
                }

                continue;
            }

            if (LatestLogParser.TryMatchException(trimmed) is { } exceptionMatch)
            {
                stackTrace.Add(new SourceLine(lineNumber, raw));
                if (exceptionType is null)
                {
                    exceptionType = exceptionMatch.Groups["type"].Value;
                    exceptionMessage = TrimmedOrNull(exceptionMatch.Groups["message"].Value);
                    exceptionLine = lineNumber;
                }
            }
        }

        FlushSection();

        if (!hasHeader)
        {
            warnings.Add("未找到 Minecraft Crash Report 文件头，输入可能不是崩溃报告。");
        }

        if (exceptionType is null)
        {
            warnings.Add("未找到异常头，无法从该崩溃报告提取堆栈异常类型。");
        }

        if (description is null)
        {
            warnings.Add("崩溃报告缺少 Description 字段。");
        }

        return new CrashReport
        {
            File = file,
            HasHeader = hasHeader,
            Time = time,
            Description = description,
            DescriptionLine = descriptionLine,
            MinecraftVersion = minecraftVersion,
            JavaVersion = javaVersion,
            OperatingSystem = operatingSystem,
            ExceptionType = exceptionType,
            ExceptionMessage = exceptionMessage,
            ExceptionLine = exceptionLine,
            StackTrace = stackTrace,
            Mods = mods,
            ModListLines = modListLines,
            Sections = sections,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// 把崩溃报告中的关键区段转换为统一事件，便于规则引擎在不复制原始日志的前提下匹配。
    /// 事件中的行号仍指向崩溃报告原文。
    /// </summary>
    public static IReadOnlyList<ParsedLogEvent> ToEvents(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var events = new List<ParsedLogEvent>();

        if (!string.IsNullOrWhiteSpace(report.Description) && report.DescriptionLine is { } descriptionLine)
        {
            events.Add(new ParsedLogEvent
            {
                File = report.File,
                SourceKind = DiagnosticSourceKind.CrashReport,
                Line = descriptionLine,
                EndLine = descriptionLine,
                Timestamp = report.Time,
                Level = "ERROR",
                Logger = "crash-report",
                Message = report.Description,
                Lines = [new SourceLine(descriptionLine, $"Description: {report.Description}")],
            });
        }

        if (report.ModListLines.Count > 0)
        {
            events.Add(new ParsedLogEvent
            {
                File = report.File,
                SourceKind = DiagnosticSourceKind.CrashReport,
                Line = report.ModListLines[0].Number,
                EndLine = report.ModListLines[^1].Number,
                Timestamp = report.Time,
                Level = "INFO",
                Logger = "crash-report",
                Message = "Mods",
                Lines = report.ModListLines,
            });
        }

        if (report.ExceptionType is not null && report.ExceptionLine is { } exceptionLine)
        {
            var lines = report.StackTrace
                .Where(line => line.Number >= exceptionLine)
                .OrderBy(line => line.Number)
                .ToArray();
            var message = lines.FirstOrDefault(line => line.Number == exceptionLine)?.Text
                          ?? report.ExceptionType;
            var types = new List<string>();
            foreach (var line in lines)
            {
                if (LatestLogParser.TryMatchException(line.Text) is { } match)
                {
                    var type = match.Groups["type"].Value;
                    if (!types.Contains(type, StringComparer.Ordinal))
                    {
                        types.Add(type);
                    }
                }
            }

            events.Add(new ParsedLogEvent
            {
                File = report.File,
                SourceKind = DiagnosticSourceKind.CrashReport,
                Line = exceptionLine,
                EndLine = lines.Length == 0 ? exceptionLine : lines[^1].Number,
                Timestamp = report.Time,
                Level = "ERROR",
                Logger = "crash-report",
                Message = message,
                ExceptionType = report.ExceptionType,
                ExceptionMessage = report.ExceptionMessage,
                ExceptionTypes = types.Count == 0 ? [report.ExceptionType] : types,
                Lines = lines.Length == 0
                    ? [new SourceLine(exceptionLine, report.ExceptionType)]
                    : lines,
            });
        }

        return events;
    }

    private static bool TryKeyValue(string trimmed, string key, out string value)
    {
        value = "";
        if (!trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = trimmed[(key.Length + 1)..].Trim();
        return value.Length > 0;
    }

    private static bool IsModHeader(string trimmed)
    {
        if (trimmed.Equals("Mod List:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Loaded Mods:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string suffix = " Mods:";
        if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("--", StringComparison.Ordinal) ||
            trimmed.Length <= suffix.Length)
        {
            return false;
        }

        var prefix = trimmed[..^suffix.Length];
        return !prefix.Contains(' ');
    }

    private static CrashReportMod ParseMod(string id, string rest)
    {
        if (rest.Length == 0)
        {
            return new CrashReportMod(id, null, null, id);
        }

        var tokens = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2 && LooksLikeVersion(tokens[^1]))
        {
            return new CrashReportMod(
                id,
                string.Join(" ", tokens.Take(tokens.Length - 1)),
                tokens[^1],
                $"{id}: {rest}");
        }

        return new CrashReportMod(id, rest, null, $"{id}: {rest}");
    }

    private static bool LooksLikeVersion(string token)
    {
        var value = token.TrimStart('v', 'V');
        if (value.Length == 0 || !char.IsAsciiDigit(value[0]))
        {
            return false;
        }

        return value.All(c => char.IsAsciiDigit(c) || c is '.' or '+' or '-' or '_');
    }

    private static int CountIndent(string line)
    {
        var indent = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                indent++;
            }
            else if (c == '\t')
            {
                indent++;
            }
            else
            {
                break;
            }
        }

        return indent;
    }

    private static string? TrimmedOrNull(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
