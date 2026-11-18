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
/// schema 2 起，带 <see cref="DiagnosticReport.InstanceId"/> 的报告保存到
/// <c>&lt;root&gt;/&lt;instanceId&gt;/&lt;reportId&gt;.json</c>；没有实例归属的旧报告仍保存在根目录。
/// 每个实例最多保留 <see cref="MaxReportsPerInstance"/> 条，按 <see cref="DiagnosticReport.GeneratedAtUtc"/> 裁剪。
/// </para>
/// <para>
/// 写入采用“同目录临时文件 + 原子替换”，避免中断留下半份报告；读取按文件隔离，
/// 损坏或版本不兼容的单条记录只产生问题项，不影响其他历史记录。读取 schema 1/2 旧报告，
/// 但不做迁移写入。只保存诊断报告，不保存原始日志副本；默认完全本地。
/// </para>
/// </summary>
public sealed class DiagnosticHistoryStore
{
    public const int CurrentSchemaVersion = DiagnosticReport.CurrentSchemaVersion;

    /// <summary>仍可读取的最旧 schema；更旧的报告不会被猜测解析。</summary>
    public const int MinSupportedSchemaVersion = DiagnosticReport.MinSupportedSchemaVersion;

    /// <summary>每个实例目录保留的最大报告数。</summary>
    public const int MaxReportsPerInstance = 50;

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

    /// <summary>原子写入一条诊断报告，返回实际文件路径；写入后按实例裁剪历史。</summary>
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

        if (report.SchemaVersion < MinSupportedSchemaVersion || report.SchemaVersion > CurrentSchemaVersion)
        {
            return Result<string>.Fail(
                "history.schema_incompatible",
                $"诊断报告 schemaVersion {report.SchemaVersion} 不受支持。",
                $"当前支持 {MinSupportedSchemaVersion}..{CurrentSchemaVersion}。");
        }

        var instanceId = NormalizeInstanceId(report.InstanceId);
        if (instanceId.Error is not null)
        {
            return Result<string>.Fail(instanceId.Error!);
        }

        var directory = ResolveDirectory(instanceId.Value);
        var filePath = Path.Combine(directory, report.ReportId + ".json");
        var temporaryPath = filePath + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(report, EclJson.Indented);
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, filePath, overwrite: true);

            if (instanceId.Value is not null)
            {
                // 裁剪是尽力而为：失败不能把已成功写入的报告变成失败。
                TryPrune(directory);
            }

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
    /// 读取历史。<paramref name="instanceId"/> 为空时只读取根目录旧记录；
    /// 指定实例时只读取该实例目录，不把其他实例或根目录旧报告混入。
    /// 损坏、超限或 schema 不兼容的文件会被跳过并报告，不会抛异常。
    /// </summary>
    public Result<DiagnosticHistoryLoadResult> LoadAll(string? instanceId = null)
    {
        var normalized = NormalizeInstanceId(instanceId);
        if (normalized.Error is not null)
        {
            return Result<DiagnosticHistoryLoadResult>.Fail(normalized.Error!);
        }

        var directory = ResolveDirectory(normalized.Value);
        var reports = new List<DiagnosticReport>();
        var issues = new List<DiagnosticInputIssue>();

        try
        {
            if (!Directory.Exists(directory))
            {
                return Result<DiagnosticHistoryLoadResult>.Ok(
                    new DiagnosticHistoryLoadResult(reports, issues));
            }

            foreach (var path in Directory
                         .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (TryLoadFile(path, out var report, out var issue))
                {
                    reports.Add(report!);
                }
                else if (issue is not null)
                {
                    issues.Add(issue);
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

    /// <summary>读取根目录单条历史；文件不存在返回成功且值为 <c>null</c>，损坏返回明确错误。</summary>
    public Result<DiagnosticReport?> Load(string reportId) => Load(instanceId: null, reportId);

    /// <summary>读取指定实例（或根目录）的单条历史。</summary>
    public Result<DiagnosticReport?> Load(string? instanceId, string reportId)
    {
        if (!IsSafeReportId(reportId))
        {
            return Result<DiagnosticReport?>.Fail("history.invalid_id", "诊断报告 ID 无效。", reportId);
        }

        var normalized = NormalizeInstanceId(instanceId);
        if (normalized.Error is not null)
        {
            return Result<DiagnosticReport?>.Fail(normalized.Error!);
        }

        var path = Path.Combine(ResolveDirectory(normalized.Value), reportId + ".json");
        if (!File.Exists(path))
        {
            return Result<DiagnosticReport?>.Ok(null);
        }

        if (TryLoadFile(path, out var report, out var issue))
        {
            return Result<DiagnosticReport?>.Ok(report);
        }

        return Result<DiagnosticReport?>.Fail(
            issue?.Code ?? "history.corrupt",
            issue?.Message ?? "诊断历史无法读取。",
            issue?.File);
    }

    /// <summary>删除根目录单条历史。不存在视为成功。</summary>
    public Result Delete(string reportId) => Delete(instanceId: null, reportId);

    /// <summary>删除指定实例（或根目录）的单条历史。不存在视为成功。</summary>
    public Result Delete(string? instanceId, string reportId)
    {
        if (!IsSafeReportId(reportId))
        {
            return Result.Fail("history.invalid_id", "诊断报告 ID 无效。", reportId);
        }

        var normalized = NormalizeInstanceId(instanceId);
        if (normalized.Error is not null)
        {
            return Result.Fail(normalized.Error!);
        }

        try
        {
            var path = Path.Combine(ResolveDirectory(normalized.Value), reportId + ".json");
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

    private string ResolveDirectory(string? instanceId) =>
        instanceId is null ? _directory : Path.Combine(_directory, instanceId);

    /// <summary>读取单个文件；失败时返回 <c>false</c> 并通过 <paramref name="issue"/> 说明原因。</summary>
    private static bool TryLoadFile(
        string path,
        out DiagnosticReport? report,
        out DiagnosticInputIssue? issue)
    {
        report = null;
        issue = null;
        var fileName = Path.GetFileName(path);
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxReportBytes)
            {
                issue = new DiagnosticInputIssue(
                    fileName,
                    "history.too_large",
                    "诊断历史文件超过读取上限，已跳过。");
                return false;
            }

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<DiagnosticReport>(json, EclJson.Default);
            if (loaded is null)
            {
                issue = new DiagnosticInputIssue(
                    fileName,
                    "history.corrupt",
                    "诊断历史文件内容为空，已跳过。");
                return false;
            }

            if (loaded.SchemaVersion < MinSupportedSchemaVersion || loaded.SchemaVersion > CurrentSchemaVersion)
            {
                issue = new DiagnosticInputIssue(
                    fileName,
                    "history.schema_incompatible",
                    $"诊断历史 schemaVersion {loaded.SchemaVersion} 不受支持，已跳过。");
                return false;
            }

            if (string.IsNullOrWhiteSpace(loaded.ReportId) && loaded.GeneratedAtUtc == default)
            {
                // 同一 config/diagnostics 目录也保存世界诊断事件；缺少报告标识与生成时间的 JSON
                // 不是诊断历史，不能仅凭文件名补成一条“空报告”。
                issue = new DiagnosticInputIssue(
                    fileName,
                    "history.corrupt",
                    "诊断历史文件缺少报告标识与生成时间，已跳过。");
                return false;
            }

            if (string.IsNullOrWhiteSpace(loaded.ReportId))
            {
                // 容忍旧记录缺少 id：用文件名补全，但不改写原文件。
                loaded = loaded with { ReportId = Path.GetFileNameWithoutExtension(fileName) };
            }

            report = loaded;
            return true;
        }
        catch (JsonException exception)
        {
            issue = new DiagnosticInputIssue(
                fileName,
                "history.corrupt",
                $"诊断历史 JSON 无法解析，已跳过：{exception.Message}");
            return false;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            issue = new DiagnosticInputIssue(
                fileName,
                "history.read_failed",
                $"无法读取诊断历史，已跳过：{exception.Message}");
            return false;
        }
    }

    /// <summary>按生成时间保留最新 N 条；损坏/版本不兼容的文件不参与裁剪，也不会被删除。</summary>
    private static void TryPrune(string directory)
    {
        try
        {
            var files = Directory
                .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .ToArray();
            if (files.Length <= MaxReportsPerInstance)
            {
                return;
            }

            var entries = new List<(string Path, DateTimeOffset GeneratedAtUtc)>();
            foreach (var path in files)
            {
                if (TryReadGeneratedAt(path, out var generatedAt))
                {
                    entries.Add((path, generatedAt));
                }
            }

            if (entries.Count <= MaxReportsPerInstance)
            {
                return;
            }

            foreach (var entry in entries
                         .OrderByDescending(item => item.GeneratedAtUtc)
                         .ThenBy(item => item.Path, StringComparer.Ordinal)
                         .Skip(MaxReportsPerInstance))
            {
                TryDelete(entry.Path);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            // 裁剪失败只影响保留数量，不影响已写入报告的可读性。
        }
    }

    private static bool TryReadGeneratedAt(string path, out DateTimeOffset generatedAt)
    {
        generatedAt = default;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxReportBytes)
            {
                return false;
            }

            var report = JsonSerializer.Deserialize<DiagnosticReport>(
                File.ReadAllText(path),
                EclJson.Default);
            if (report is null ||
                report.SchemaVersion < MinSupportedSchemaVersion ||
                report.SchemaVersion > CurrentSchemaVersion)
            {
                return false;
            }

            generatedAt = report.GeneratedAtUtc;
            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>校验实例 ID；空值表示根目录（旧版兼容），非法值明确失败而不是拼出越界路径。</summary>
    private static (string? Value, EclError? Error) NormalizeInstanceId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return (null, null);
        }

        var trimmed = instanceId.Trim();
        if (!AppPaths.IsValidInstanceId(trimmed))
        {
            return (null, new EclError("history.invalid_instance_id", "诊断历史实例 ID 无效。", trimmed));
        }

        return (trimmed, null);
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
            // 清理失败不应覆盖原始写入/裁剪错误。
        }
    }
}
