// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Ecl.Sessions;

/// <summary>
/// 游戏退出后的崩溃证据采集。原始文件只复制、不移动、不删除；归属置信度
/// 必须诚实：共享 latest.log 与其他无法唯一归因的文件标记为歧义。
/// </summary>
public sealed class SessionArtifactCollector
{
    private const int MaxScanFiles = 512;
    private const long MaxCopyBytes = 64L * 1024 * 1024;

    private readonly IGameSessionService _sessions;
    private readonly ILogger<SessionArtifactCollector> _logger;

    public SessionArtifactCollector(IGameSessionService sessions, ILogger<SessionArtifactCollector> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public void CollectAfterExit(GameLogSession session)
    {
        var manifest = session.Manifest;
        var gameDirectory = manifest.GameDirectory;
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            return;
        }

        var since = manifest.CreatedAtUtc.UtcDateTime;

        CollectCrashReports(session, gameDirectory, since, "crash-reports", "client", SessionArtifactKinds.ClientCrashReport);
        CollectCrashReports(session, gameDirectory, since, "crash-reports", "server", SessionArtifactKinds.ServerCrashReport);
        CollectFatalErrors(session, gameDirectory, manifest.WorkingDirectory, since);
        CollectSharedLog(session, gameDirectory);
        CollectRotatedLogs(session, gameDirectory);
    }

    private void CollectCrashReports(
        GameLogSession session,
        string gameDirectory,
        DateTime since,
        string folder,
        string side,
        string kind)
    {
        var directory = Path.Combine(gameDirectory, folder);
        if (!Directory.Exists(directory))
        {
            return;
        }

        var candidates = Enumerate(directory, "crash-*-" + side + ".txt", since).ToArray();
        foreach (var file in candidates)
        {
            Register(session, kind, file, ArtifactOwnership.StronglyCorrelated, CopyArtifact(session, kind, file));
        }
    }

    private void CollectFatalErrors(GameLogSession session, string gameDirectory, string? workingDirectory, DateTime since)
    {
        var roots = new[] { gameDirectory, workingDirectory }
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var file in Enumerate(root!, "hs_err_pid*.log", since))
            {
                Register(session, SessionArtifactKinds.JvmFatalError, file, ArtifactOwnership.StronglyCorrelated, CopyArtifact(session, SessionArtifactKinds.JvmFatalError, file));
            }

            foreach (var file in Enumerate(root!, "hs-err-*.log", since))
            {
                Register(session, SessionArtifactKinds.JvmFatalError, file, ArtifactOwnership.Possible, CopyArtifact(session, SessionArtifactKinds.JvmFatalError, file));
            }
        }
    }

    private void CollectSharedLog(GameLogSession session, string gameDirectory)
    {
        var latest = Path.Combine(gameDirectory, "logs", "latest.log");
        if (!File.Exists(latest))
        {
            return;
        }

        // latest.log 可能被同一实例的多个进程同时写入，不能声称属于单一进程。
        Register(
            session,
            SessionArtifactKinds.GameLog,
            latest,
            ArtifactOwnership.SharedOrAmbiguous,
            CopyArtifact(session, SessionArtifactKinds.GameLog, latest),
            "共享 latest.log：可能由多个进程写入，不能视为本会话的完整日志。");
    }

    private void CollectRotatedLogs(GameLogSession session, string gameDirectory)
    {
        var logs = Path.Combine(gameDirectory, "logs");
        if (!Directory.Exists(logs))
        {
            return;
        }

        foreach (var file in Enumerate(logs, "*.log.gz", session.Manifest.CreatedAtUtc.UtcDateTime).Take(32))
        {
            Register(
                session,
                SessionArtifactKinds.RotatedLog,
                file,
                ArtifactOwnership.Possible,
                CopyArtifact(session, SessionArtifactKinds.RotatedLog, file),
                "轮转归档按实际 gzip 格式识别；归属仅为时间相关。");
        }
    }

    private string? CopyArtifact(GameLogSession session, string kind, string source)
    {
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists || info.Length > MaxCopyBytes)
            {
                return null;
            }

            var directory = Path.Combine(session.ArtifactsDirectory, kind);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, $"{DateTimeOffset.UtcNow:HHmmss}-{info.Name}");
            File.Copy(source, destination, overwrite: true);
            return destination;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "复制崩溃证据失败：{Source}", source);
            return null;
        }
    }

    private void Register(
        GameLogSession session,
        string kind,
        string source,
        ArtifactOwnership ownership,
        string? captured,
        string? warning = null)
    {
        var size = 0L;
        string? hash = null;
        try
        {
            var info = new FileInfo(source);
            if (info.Exists)
            {
                size = info.Length;
                hash = info.Length <= 8 * 1024 * 1024
                    ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))
                    : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 哈希失败不影响登记。
        }

        _sessions.RegisterArtifact(session.SessionId, new SessionArtifact
        {
            Id = $"{kind}:{Path.GetFileName(source)}",
            Kind = kind,
            SourcePath = source,
            CapturedPath = captured,
            Size = size,
            Sha256 = hash,
            Ownership = ownership,
            Warning = warning,
            ObservedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    private static IEnumerable<string> Enumerate(string directory, string pattern, DateTime since)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Where(file => File.GetLastWriteTimeUtc(file) >= since)
                .Take(MaxScanFiles)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
