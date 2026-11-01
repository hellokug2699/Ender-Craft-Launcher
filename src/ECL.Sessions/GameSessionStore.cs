// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;

namespace Ecl.Sessions;

/// <summary>
/// 会话目录与 manifest 的持久化。manifest 使用同目录临时文件 + 原子替换；
/// 会话证据只能通过显式的保留策略清理，启动新会话绝不会删除旧目录。
/// </summary>
public sealed class GameSessionStore
{
    private readonly string _root;

    public GameSessionStore(IAppPaths paths)
    {
        _root = Path.Combine(paths.LogsDirectory, "sessions");
    }

    public string SessionsRoot => _root;

    public GameLogSession Create(string sessionId)
    {
        var directory = Path.Combine(_root, sessionId);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
        return new GameLogSession(
            new SessionManifest { SessionId = sessionId },
            directory);
    }

    public GameLogSession? Load(string sessionId)
    {
        if (!IsSafeSessionId(sessionId))
        {
            return null;
        }

        var directory = Path.Combine(_root, sessionId);
        return LoadFrom(directory);
    }

    public IReadOnlyList<GameLogSession> LoadAll()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var sessions = new List<GameLogSession>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var session = LoadFrom(directory);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions
            .OrderByDescending(session => session.Manifest.CreatedAtUtc)
            .ToArray();
    }

    public void SaveManifest(GameLogSession session)
    {
        Directory.CreateDirectory(session.DirectoryPath);
        var path = session.ManifestPath;
        var temporary = path + ".tmp";
        var json = JsonSerializer.Serialize(session.Manifest, EclJson.Indented);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public static string CreateSessionId() =>
        $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    public static bool IsSafeSessionId(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) &&
        sessionId.Length <= 64 &&
        sessionId.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static GameLogSession? LoadFrom(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<SessionManifest>(
                File.ReadAllText(manifestPath),
                EclJson.Default);
            if (manifest is null || !IsSafeSessionId(manifest.SessionId))
            {
                return null;
            }

            return new GameLogSession(manifest, directory);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
