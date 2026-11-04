// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;

namespace Ecl.Sessions;

public interface ISessionExportService
{
    Task<Result<SessionExportResult>> ExportAsync(
        string sessionId,
        string? outputPath = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 按单次 SessionId 导出诊断材料。一次导出只对应一个会话；缺失、歧义与截断
/// 必须显式列出，导出成功不等于证据完整。
/// </summary>
public sealed class SessionExportService : ISessionExportService
{
    private const long MaxTotalBytes = 128L * 1024 * 1024;
    private const long MaxEntryBytes = 16L * 1024 * 1024;

    private readonly IGameSessionService _sessions;
    private readonly string _outputRoot;

    public SessionExportService(IGameSessionService sessions, IAppPaths paths)
    {
        _sessions = sessions;
        _outputRoot = Path.Combine(paths.LogsDirectory, "diagnostics");
    }

    public Task<Result<SessionExportResult>> ExportAsync(
        string sessionId,
        string? outputPath = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Export(sessionId, outputPath), cancellationToken);

    private Result<SessionExportResult> Export(string sessionId, string? outputPath)
    {
        var session = _sessions.Get(sessionId);
        if (session is null)
        {
            return Result<SessionExportResult>.Fail("session.not_found", $"会话不存在：{sessionId}");
        }

        var output = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(_outputRoot, $"ecl-session-{sessionId}.zip")
            : Path.GetFullPath(outputPath);

        var staging = output + ".staging";
        try
        {
            var directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(staging))
            {
                File.Delete(staging);
            }

            var missing = new List<string>();
            long total = 0;
            using (var archive = ZipFile.Open(staging, ZipArchiveMode.Create))
            {
                var manifestJson = JsonSerializer.Serialize(session.Manifest, EclJson.Indented);
                total += AddText(archive, "manifest.json", SecretRedaction.Text(manifestJson), total);

                var events = SessionJournal.ReadAll(session.JournalPath, out var torn);
                var eventLines = string.Join(
                    "\n",
                    events.Select(entry => JsonSerializer.Serialize(entry, EclJson.Default)));
                total += AddText(archive, "events.jsonl", SecretRedaction.Text(eventLines), total);
                if (torn)
                {
                    missing.Add("events.jsonl: 最后一条记录不完整，已忽略");
                }

                total += AddFileIfExists(archive, "logs/game.stdout.log", session.StdoutPath, total);
                if (!File.Exists(session.StdoutPath))
                {
                    missing.Add(SessionArtifactKinds.Stdout);
                }

                total += AddFileIfExists(archive, "logs/game.stderr.log", session.StderrPath, total);
                if (!File.Exists(session.StderrPath))
                {
                    missing.Add(SessionArtifactKinds.Stderr);
                }

                var artifactEntries = new List<object>();
                foreach (var artifact in session.Manifest.Artifacts)
                {
                    var captured = ExportArtifact(archive, session, artifact, ref total, missing);
                    artifactEntries.Add(captured);
                }

                foreach (var required in new[]
                         {
                             SessionArtifactKinds.JvmFatalError,
                             SessionArtifactKinds.ClientCrashReport,
                             SessionArtifactKinds.ServerCrashReport,
                         })
                {
                    if (!session.Manifest.Artifacts.Any(artifact => artifact.Kind == required))
                    {
                        missing.Add(required);
                    }
                }

                total += AddText(
                    archive,
                    "artifacts.json",
                    SecretRedaction.Text(JsonSerializer.Serialize(artifactEntries, EclJson.Indented)),
                    total);
                total += AddText(archive, "session-summary.txt", BuildSummary(session, missing), total);
            }

            var size = new FileInfo(staging).Length;
            if (size > MaxTotalBytes)
            {
                File.Delete(staging);
                return Result<SessionExportResult>.Fail(
                    "session.export.too_large",
                    $"导出内容超过 {MaxTotalBytes / (1024 * 1024)} MB 限制。");
            }

            File.Move(staging, output, overwrite: true);
            return Result<SessionExportResult>.Ok(new SessionExportResult(output, size, missing));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(staging);
            return Result<SessionExportResult>.Fail(
                "session.export.failed",
                $"导出会话 {sessionId} 失败：{exception.Message}");
        }
    }

    private static object ExportArtifact(
        ZipArchive archive,
        GameLogSession session,
        SessionArtifact artifact,
        ref long total,
        List<string> missing)
    {
        var entryName = $"artifacts/{Sanitize(artifact.Kind)}/{Sanitize(Path.GetFileName(artifact.CapturedPath ?? artifact.SourcePath ?? "artifact"))}";
        var source = artifact.CapturedPath ?? artifact.SourcePath;
        long size = 0;
        var hash = artifact.Sha256;
        var included = false;

        if (source is not null && File.Exists(source))
        {
            try
            {
                var bytes = ReadCapped(source, Math.Min(MaxEntryBytes, Math.Max(0, MaxTotalBytes - total)));
                var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(bytes);
                size = bytes.Length;
                hash ??= Convert.ToHexString(SHA256.HashData(bytes));
                total += size;
                included = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                missing.Add($"{artifact.Kind}: 无法读取（{exception.Message}）");
            }
        }
        else
        {
            missing.Add($"{artifact.Kind}: 原始文件已不存在");
        }

        return new
        {
            artifact.Id,
            artifact.Kind,
            artifact.SourcePath,
            artifact.CapturedPath,
            size,
            artifact.ObservedAtUtc,
            sha256 = hash,
            ownership = artifact.Ownership.ToString(),
            artifact.Warning,
            included,
            sessionId = session.SessionId,
        };
    }

    private static string BuildSummary(GameLogSession session, IReadOnlyList<string> missing)
    {
        var manifest = session.Manifest;
        var builder = new StringBuilder();
        builder.AppendLine($"会话: {manifest.SessionId}");
        builder.AppendLine($"实例: {manifest.InstanceName ?? "未知"} ({manifest.InstanceId ?? "未知"})");
        builder.AppendLine($"状态: {manifest.State}");
        builder.AppendLine($"创建: {manifest.CreatedAtUtc:O}");
        builder.AppendLine($"进程: {manifest.ProcessId?.ToString() ?? "未知"}");
        builder.AppendLine($"日志隔离: {manifest.LogIsolation}");
        builder.AppendLine($"退出码: {manifest.ExitCode?.ToString() ?? "未知"}");
        builder.AppendLine($"采集故障: {manifest.CaptureFault ?? "无"}");
        builder.AppendLine();
        builder.AppendLine("缺失或无法关联的证据:");
        if (missing.Count == 0)
        {
            builder.AppendLine("- 无");
        }
        else
        {
            foreach (var item in missing.Distinct(StringComparer.Ordinal))
            {
                builder.AppendLine($"- {item}");
            }
        }

        if (manifest.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("警告:");
            foreach (var warning in manifest.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    private static long AddText(ZipArchive archive, string name, string content, long usedBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (usedBytes + bytes.Length > MaxTotalBytes)
        {
            bytes = Encoding.UTF8.GetBytes("[内容超过导出上限，已省略]\n");
        }

        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(bytes);
        return bytes.Length;
    }

    private static long AddFileIfExists(ZipArchive archive, string name, string path, long usedBytes)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            var bytes = ReadCapped(path, Math.Min(MaxEntryBytes, Math.Max(0, MaxTotalBytes - usedBytes)));
            var text = Encoding.UTF8.GetString(bytes);
            return AddText(archive, name, SecretRedaction.Text(text), usedBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static byte[] ReadCapped(string path, long limit)
    {
        using var stream = File.OpenRead(path);
        var length = (int)Math.Min(stream.Length, Math.Max(0, limit));
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = stream.Read(buffer, read, length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "artifact" : cleaned[..Math.Min(cleaned.Length, 96)];
    }

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
            // 清理失败不改变导出结论。
        }
    }
}
