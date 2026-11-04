// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Diagnostics;

/// <summary>
/// 批量读取本地诊断输入。每个文件独立解析：某个文件缺失、不可读或损坏时，只登记
/// <see cref="DiagnosticInputIssue"/> 并继续处理其他文件，不会让整个诊断失败。
/// <para>该类型只读取本地文件，不访问网络。</para>
/// </summary>
public sealed class DiagnosticInputLoader
{
    private readonly int _maxBytes;
    private readonly int _maxLines;

    public DiagnosticInputLoader(
        int maxBytes = BoundedTextReader.DefaultMaxBytes,
        int maxLines = BoundedTextReader.DefaultMaxLines)
    {
        _maxBytes = maxBytes > 0 ? maxBytes : BoundedTextReader.DefaultMaxBytes;
        _maxLines = maxLines > 0 ? maxLines : BoundedTextReader.DefaultMaxLines;
    }

    /// <summary>
    /// 读取一组 latest.log 与 crash report 文件。路径只用于本地打开；证据中只保留文件名。
    /// </summary>
    public DiagnosticInput Load(
        IEnumerable<string>? logFiles = null,
        IEnumerable<string>? crashReportFiles = null)
    {
        var events = new List<ParsedLogEvent>();
        var crashReports = new List<CrashReport>();
        var issues = new List<DiagnosticInputIssue>();

        foreach (var path in Distinct(logFiles))
        {
            try
            {
                var parsed = LatestLogParser.ParseFile(path, _maxBytes, _maxLines);
                if (parsed.IsSuccess)
                {
                    events.AddRange(parsed.Value!.Events);
                }
                else
                {
                    issues.Add(ToIssue(path, parsed.Error!));
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                issues.Add(new DiagnosticInputIssue(
                    BoundedTextReader.SafeFileName(path),
                    "diagnostics.log_failed",
                    $"解析日志文件失败：{exception.Message}"));
            }
        }

        foreach (var path in Distinct(crashReportFiles))
        {
            try
            {
                var parsed = CrashReportParser.ParseFile(path, _maxBytes, _maxLines);
                if (parsed.IsSuccess)
                {
                    crashReports.Add(parsed.Value!);
                }
                else
                {
                    issues.Add(ToIssue(path, parsed.Error!));
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                issues.Add(new DiagnosticInputIssue(
                    BoundedTextReader.SafeFileName(path),
                    "diagnostics.crash_failed",
                    $"解析崩溃报告失败：{exception.Message}"));
            }
        }

        return new DiagnosticInput
        {
            Events = events,
            CrashReports = crashReports,
            Issues = issues,
        };
    }

    private static DiagnosticInputIssue ToIssue(string path, Ecl.Core.EclError error) =>
        new(BoundedTextReader.SafeFileName(path), error.Code, error.ToString());

    private static IEnumerable<string> Distinct(IEnumerable<string>? paths) =>
        paths is null
            ? Array.Empty<string>()
            : paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
}
