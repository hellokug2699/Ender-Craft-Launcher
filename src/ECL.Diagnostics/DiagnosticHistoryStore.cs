// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;

namespace Ecl.Diagnostics;

/// <summary>加载诊断历史的结果。损坏条目被跳过并记录在 <see cref="Issues"/> 中。</summary>
public sealed record DiagnosticHistoryLoadResult(
    IReadOnlyList<DiagnosticReport> Reports,
    IReadOnlyList<DiagnosticInputIssue> Issues);

/// <summary>
/// 实例级诊断历史存储（本地 JSON）。
/// <para>
/// 写入采用“同目录临时文件 + 原子替换”，避免中断留下半份报告；读取按文件隔离，
/// 损坏或版本不兼容的单条记录只产生问题项，不影响其他历史记录。
/// </para>
/// <para>
/// 只保存诊断报告（环境摘要、知识库版本、结论与证据摘录），不保存原始日志副本；
/// 默认完全本地，不发起网络请求，也不上传任何诊断内容。每条历史记录保留生成当时的
/// 知识库版本，后续知识库更新不会改写旧结论。
/// </para>
/// </summary>
public sealed class DiagnosticHistoryStore
{
    public const int CurrentSchemaVersion = DiagnosticReport.CurrentSchemaVersion;
    public const int MaxReportBytes = 4 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;

    public DiagnosticHistoryStore(string directory)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? throw new ArgumentException("诊断历史目录不能为空。", nameof(directory))
            : directory;
    }

    public string DirectoryPath => _directory;

    /// <summary>原子写入一条诊断报告，返回实际文件路径。</summary>
    public Result<string> Save(DiagnosticReport report)
    {
        if (report is null)
        {
            return Result<string>.Fail("history.invalid_report", "诊断报告为空。");
        }

        if (!IsSafeReportId(report.ReportId))
        {
            return Result<string>.Fail(
                "history.invalid_id",
                "诊断报告 ID 无效，无法保存。",
                report.ReportId);
        }

        if (report.SchemaVersion <= 0 || report.SchemaVersion > CurrentSchemaVersion)
        {
            return Result<string>.Fail(
                "history.schema_incompatible",
                $"诊断报告 schemaVersion {report.SchemaVersion} 不受支持。",
                $"当前支持 {CurrentSchemaVersion}。");
        }

        var filePath = Path.Combine(_directory, report.ReportId + ".json");
        var temporaryPath = filePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(report, EclJson.Indented);
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, filePath, overwrite: true);
            return Result<string>.Ok(filePath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException)
        {
            TryDelete(temporaryPath);
            return Result<string>.Fail(
                "history.write_failed",
                "无法写入诊断历史。",
                exception.Message);
        }
    }

    /// <summary>
    /// 读取全部历史。损坏、超限或 schema 不兼容的文件会被跳过并报告，不会抛异常。
    /// </summary>
    public Result<DiagnosticHistoryLoadResult> LoadAll()
    {
        var reports = new List<DiagnosticReport>();
        var issues = new List<DiagnosticInputIssue>();

        try
        {
            if (!Directory.Exists(_directory))
            {
                return Result<DiagnosticHistoryLoadResult>.Ok(
                    new DiagnosticHistoryLoadResult(reports, issues));
            }

            foreach (var path in Directory
                         .EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var fileName = Path.GetFileName(path);
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > MaxReportBytes)
                    {
                        issues.Add(new DiagnosticInputIssue(
                            fileName,
                            "history.too_large",
                            "诊断历史文件超过读取上限，已跳过。"));
                        continue;
                    }

                    var json = File.ReadAllText(path);
                    var report = JsonSerializer.Deserialize<DiagnosticReport>(json, EclJson.Default);
                    if (report is null)
                    {
                        issues.Add(new DiagnosticInputIssue(
                            fileName,
                            "history.corrupt",
                            "诊断历史文件内容为空，已跳过。"));
                        continue;
                    }

                    if (report.SchemaVersion <= 0 || report.SchemaVersion > CurrentSchemaVersion)
                    {
                        issues.Add(new DiagnosticInputIssue(
                            fileName,
                            "history.schema_incompatible",
                            $"诊断历史 schemaVersion {report.SchemaVersion} 不受支持，已跳过。"));
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(report.ReportId))
                    {
                        // 容忍旧记录缺少 id：用文件名补全，但不改写原文件。
                        report = report with { ReportId = Path.GetFileNameWithoutExtension(fileName) };
                    }

                    reports.Add(report);
                }
                catch (JsonException exception)
                {
                    issues.Add(new DiagnosticInputIssue(
                        fileName,
                        "history.corrupt",
                        $"诊断历史 JSON 无法解析，已跳过：{exception.Message}"));
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or ArgumentException)
                {
                    issues.Add(new DiagnosticInputIssue(
                        fileName,
                        "history.read_failed",
                        $"无法读取诊断历史，已跳过：{exception.Message}"));
                }
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            return Result<DiagnosticHistoryLoadResult>.Fail(
                "history.read_failed",
                "无法枚举诊断历史目录。",
                exception.Message);
        }

        return Result<DiagnosticHistoryLoadResult>.Ok(new DiagnosticHistoryLoadResult(
            reports.OrderByDescending(report => report.GeneratedAtUtc).ToArray(),
            issues));
    }

    /// <summary>读取单条历史；文件不存在返回成功且值为 <c>null</c>，损坏返回明确错误。</summary>
    public Result<DiagnosticReport?> Load(string reportId)
    {
        if (!IsSafeReportId(reportId))
        {
            return Result<DiagnosticReport?>.Fail("history.invalid_id", "诊断报告 ID 无效。", reportId);
        }

        var path = Path.Combine(_directory, reportId + ".json");
        if (!File.Exists(path))
        {
            return Result<DiagnosticReport?>.Ok(null);
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxReportBytes)
            {
                return Result<DiagnosticReport?>.Fail(
                    "history.too_large",
                    "诊断历史文件超过读取上限。",
                    Path.GetFileName(path));
            }

            var json = File.ReadAllText(path);
            var report = JsonSerializer.Deserialize<DiagnosticReport>(json, EclJson.Default);
            if (report is null)
            {
                return Result<DiagnosticReport?>.Fail("history.corrupt", "诊断历史文件内容为空。");
            }

            if (report.SchemaVersion <= 0 || report.SchemaVersion > CurrentSchemaVersion)
            {
                return Result<DiagnosticReport?>.Fail(
                    "history.schema_incompatible",
                    $"诊断历史 schemaVersion {report.SchemaVersion} 不受支持。");
            }

            return Result<DiagnosticReport?>.Ok(report);
        }
        catch (JsonException exception)
        {
            return Result<DiagnosticReport?>.Fail(
                "history.corrupt",
                "诊断历史 JSON 无法解析。",
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            return Result<DiagnosticReport?>.Fail(
                "history.read_failed",
                "无法读取诊断历史。",
                exception.Message);
        }
    }

    /// <summary>删除单条历史。不存在视为成功。</summary>
    public Result Delete(string reportId)
    {
        if (!IsSafeReportId(reportId))
        {
            return Result.Fail("history.invalid_id", "诊断报告 ID 无效。", reportId);
        }

        try
        {
            var path = Path.Combine(_directory, reportId + ".json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            return Result.Fail("history.delete_failed", "无法删除诊断历史。", exception.Message);
        }
    }

    private static bool IsSafeReportId(string? reportId) =>
        !string.IsNullOrWhiteSpace(reportId) &&
        reportId.Length <= 128 &&
        reportId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理临时文件失败不应覆盖原始写入错误。
        }
    }
}
