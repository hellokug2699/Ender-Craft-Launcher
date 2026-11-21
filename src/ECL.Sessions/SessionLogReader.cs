// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace Ecl.Sessions;

/// <summary>日志阅读器可读取的来源。游戏日志、stdout、stderr 与证据文件是不同的来源，
/// 不得在没有明确时间戳依据时拼接成一条伪造的统一时间线（logs.md §3.3）。</summary>
public enum SessionLogSourceKind
{
    GameLog,
    Stdout,
    Stderr,
    Evidence,
}

/// <summary>来源与证据的实际可读取状态；未知不等于空，也不等于已生成。</summary>
public enum SessionSourceAvailability
{
    Available,
    Partial,
    NotGenerated,
    Missing,
    Unreadable,
}

/// <summary>一个日志来源的当前状态。路径只来自会话服务登记的文件。</summary>
public sealed record SessionLogSourceInfo(
    SessionLogSourceKind Kind,
    string? Path,
    ArtifactOwnership? Ownership,
    SessionSourceAvailability Availability,
    long? Size,
    DateTimeOffset? LastWriteTimeUtc,
    string? Warning);

/// <summary>
/// 证据文件的读取锚点。崩溃报告与 JVM 致命错误日志把结论写在开头
/// （描述、异常类型、问题帧、内存/信号信息），一旦调用栈很长，只看末尾会完全丢失错因；
/// 游戏日志、stdout/stderr 等则以最新内容结尾。两种锚点都支持向更早/更新继续翻阅。
/// </summary>
public enum SessionEvidenceAnchor
{
    /// <summary>从文件末尾开始（最新内容）。</summary>
    Tail,

    /// <summary>从文件开头开始（崩溃类证据的关键信息所在位置）。</summary>
    Head,
}

/// <summary>已登记证据的呈现状态（logs.md §4.2）。</summary>
public sealed record SessionEvidenceItem(
    SessionArtifact Artifact,
    SessionSourceAvailability Availability,
    long? Size,
    DateTimeOffset? LastWriteTimeUtc,
    string? Path,
    string? Detail);

/// <summary>
/// 一次有界读取。行号由阅读器按来源中的顺序计算，不代表原始文件行号；
/// <see cref="StartOffset"/> / <see cref="EndOffset"/> 用于增量跟随与向更早分页。
/// </summary>
public sealed record SessionLogChunk(
    IReadOnlyList<string> Lines,
    long StartOffset,
    long EndOffset,
    bool HasMoreBefore,
    bool HasMoreAfter,
    bool SkippedPartialLine,
    string? Error);

public interface ISessionLogReader
{
    /// <summary>列出当前会话可读取的来源及其真实可用状态。</summary>
    IReadOnlyList<SessionLogSourceInfo> DescribeSources(GameLogSession session);

    /// <summary>列出会话登记的证据；不扫描实例目录推断归属。</summary>
    IReadOnlyList<SessionEvidenceItem> DescribeEvidence(GameLogSession session);

    /// <summary>读取来源尾部窗口（最多 <paramref name="maxBytes"/> 字节）。</summary>
    Task<SessionLogChunk> ReadLatestAsync(
        GameLogSession session,
        SessionLogSourceKind kind,
        int maxBytes = SessionLogReader.DefaultChunkBytes,
        CancellationToken cancellationToken = default);

    /// <summary>从指定偏移继续读取；文件被截断/轮转时从 0 重新读取并标记。</summary>
    Task<SessionLogChunk> ReadFromAsync(
        GameLogSession session,
        SessionLogSourceKind kind,
        long offset,
        int maxBytes = SessionLogReader.DefaultChunkBytes,
        CancellationToken cancellationToken = default);

    /// <summary>读取指定偏移之前的窗口（向更早分页）。</summary>
    Task<SessionLogChunk> ReadBeforeAsync(
        GameLogSession session,
        SessionLogSourceKind kind,
        long beforeOffset,
        int maxBytes = SessionLogReader.DefaultChunkBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取一条已登记证据的原始文本；只读、不修改证据。
    /// <paramref name="anchor"/> 决定首个窗口取开头还是末尾：崩溃报告与 JVM 致命错误日志的关键信息在开头。
    /// </summary>
    Task<SessionLogChunk> ReadEvidenceAsync(
        GameLogSession session,
        SessionArtifact artifact,
        int maxBytes = SessionLogReader.DefaultChunkBytes,
        SessionEvidenceAnchor anchor = SessionEvidenceAnchor.Tail,
        CancellationToken cancellationToken = default);

    /// <summary>读取证据中指定偏移之前的一段（向更早翻阅）；不改变证据文件。</summary>
    Task<SessionLogChunk> ReadEvidenceBeforeAsync(
        GameLogSession session,
        SessionArtifact artifact,
        long beforeOffset,
        int maxBytes = SessionLogReader.DefaultChunkBytes,
        CancellationToken cancellationToken = default);

    /// <summary>读取证据中指定偏移之后的一段（向更新翻阅）；不改变证据文件。</summary>
    Task<SessionLogChunk> ReadEvidenceAfterAsync(
        GameLogSession session,
        SessionArtifact artifact,
        long afterOffset,
        int maxBytes = SessionLogReader.DefaultChunkBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 会话日志与证据的唯一读取入口。所有路径来自会话 manifest 与其登记的
/// <see cref="SessionArtifact"/>，不扫描实例目录、不按文件名猜测归属（ADR 0015）。
/// 读取使用 FileShare.ReadWrite|Delete，不阻塞游戏进程写入，也不锁定证据文件。
/// </summary>
public sealed class SessionLogReader : ISessionLogReader
{
    public const int DefaultChunkBytes = 512 * 1024;
    public const int MaxChunkBytes = 4 * 1024 * 1024;

    public IReadOnlyList<SessionLogSourceInfo> DescribeSources(GameLogSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return
        [
            DescribeGameLog(session),
            DescribeProcessSource(session, SessionLogSourceKind.Stdout, session.StdoutPath),
            DescribeProcessSource(session, SessionLogSourceKind.Stderr, session.StderrPath),
        ];
    }

    public IReadOnlyList<SessionEvidenceItem> DescribeEvidence(GameLogSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var items = new List<SessionEvidenceItem>();
        foreach (var artifact in session.Manifest.Artifacts)
        {
            if (artifact.Kind is SessionArtifactKinds.Stdout or SessionArtifactKinds.Stderr)
            {
                continue;
            }

            items.Add(DescribeArtifact(artifact));
        }

        return items;
    }

    public Task<SessionLogChunk> ReadLatestAsync(
        GameLogSession session,
        SessionLogSourceKind kind,
        int maxBytes = DefaultChunkBytes,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveReadablePath(session, kind);
        if (path is null)
        {
            return Task.FromResult(EmptyChunk(null));
        }

        return Task.Run(
            () => ReadLatest(path, maxBytes, cancellationToken),
            cancellationToken);
    }

    public Task<SessionLogChunk> ReadFromAsync(
        GameLogSession session,
        SessionLogSourceKind kind,
        long offset,
        int maxBytes = DefaultChunkBytes,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveReadablePath(session, kind);
        if (path is null)
        {
            return Task.FromResult(EmptyChunk(null));
        }

        return Task.Run(
            () => ReadFrom(path, offset, maxBytes, cancellationToken),
            cancellationToken);
    }

    public Task<SessionLogChunk> ReadBeforeAsync(
        GameLogSession session,
        SessionLogSourceKind kind,
        long beforeOffset,
        int maxBytes = DefaultChunkBytes,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveReadablePath(session, kind);
        if (path is null)
        {
            return Task.FromResult(EmptyChunk(null));
        }

        return Task.Run(
            () => ReadBefore(path, beforeOffset, maxBytes, cancellationToken),
            cancellationToken);
    }

    public Task<SessionLogChunk> ReadEvidenceAsync(
        GameLogSession session,
        SessionArtifact artifact,
        int maxBytes = DefaultChunkBytes,
        SessionEvidenceAnchor anchor = SessionEvidenceAnchor.Tail,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var path = EvidencePath(artifact);
        if (path is null)
        {
            return Task.FromResult(EmptyChunk(null));
        }

        return Task.Run(
            () => anchor == SessionEvidenceAnchor.Head
                ? ReadHead(path, maxBytes, cancellationToken)
                : ReadLatest(path, maxBytes, cancellationToken),
            cancellationToken);
    }

    public Task<SessionLogChunk> ReadEvidenceBeforeAsync(
        GameLogSession session,
        SessionArtifact artifact,
        long beforeOffset,
        int maxBytes = DefaultChunkBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var path = EvidencePath(artifact);
        if (path is null)
        {
            return Task.FromResult(EmptyChunk(null));
        }

        return Task.Run(
            () => ReadBefore(path, beforeOffset, maxBytes, cancellationToken),
            cancellationToken);
    }

    public Task<SessionLogChunk> ReadEvidenceAfterAsync(
        GameLogSession session,
        SessionArtifact artifact,
        long afterOffset,
        int maxBytes = DefaultChunkBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var path = EvidencePath(artifact);
        if (path is null)
        {
            return Task.FromResult(EmptyChunk(null));
        }

        return Task.Run(
            () => ReadAfter(path, afterOffset, maxBytes, cancellationToken),
            cancellationToken);
    }

    private static string? EvidencePath(SessionArtifact artifact) =>
        ExistingPath(artifact.CapturedPath) ?? ExistingPath(artifact.SourcePath);

    private static SessionLogSourceInfo DescribeGameLog(GameLogSession session)
    {
        var manifest = session.Manifest;
        var artifact = manifest.Artifacts.FirstOrDefault(item =>
            string.Equals(item.Kind, SessionArtifactKinds.GameLog, StringComparison.Ordinal));

        // 已登记的证据优先：它带有归属置信度与采集说明（例如宿主退出后改用会话独立日志），
        // 不能让同一份文件在登记后丢掉这些说明。
        if (artifact is not null)
        {
            var path = ExistingPath(artifact.CapturedPath) ?? ExistingPath(artifact.SourcePath);
            if (path is not null)
            {
                return DescribeFile(
                    SessionLogSourceKind.GameLog,
                    path,
                    artifact.Ownership,
                    string.IsNullOrWhiteSpace(artifact.Warning) ? null : artifact.Warning);
            }

            // 登记的共享日志证据已不可读取；会话独立日志仍然可用时继续往下判断。
            if (!IsolationPrepared(manifest.LogIsolation) ||
                ExistingPath(Path.Combine(session.DirectoryPath, "game.log")) is null)
            {
                return new SessionLogSourceInfo(
                    SessionLogSourceKind.GameLog,
                    null,
                    artifact.Ownership,
                    SessionSourceAvailability.Missing,
                    null,
                    null,
                    string.IsNullOrWhiteSpace(artifact.Warning) ? "共享日志证据已不可读取。" : artifact.Warning);
            }
        }

        // 会话独立日志由游戏进程按会话专属 log4j 配置直接写入，路径本身就是归属证据：
        // 该文件只存在于本次会话目录，只有本次启动的游戏进程会写入它。
        // 隔离处于 pending 只表示“退出后尚未复核”，不代表没有隔离——因此运行期也必须
        // 呈现这份日志，否则会退回到实例共享的 latest.log（上一次启动的内容）。
        if (IsolationPrepared(manifest.LogIsolation) &&
            ExistingPath(Path.Combine(session.DirectoryPath, "game.log")) is { } isolated)
        {
            return DescribeFile(
                SessionLogSourceKind.GameLog,
                isolated,
                ArtifactOwnership.Direct,
                warning: null,
                reportEmptyAsNotGenerated: true);
        }

        // 运行中的共享日志：latest.log 可能由多个进程写入，只作为歧义来源呈现。
        // 但早于本次会话创建的 latest.log 属于上一次启动，不能当作本次会话的游戏日志。
        var latest = string.IsNullOrWhiteSpace(manifest.GameDirectory)
            ? null
            : Path.Combine(manifest.GameDirectory, "logs", "latest.log");
        if (ExistingPath(latest) is { } available)
        {
            if (WrittenSince(available, manifest.CreatedAtUtc))
            {
                return DescribeFile(
                    SessionLogSourceKind.GameLog,
                    available,
                    ArtifactOwnership.SharedOrAmbiguous,
                    "该来源是实例共享的 latest.log，可能由多个进程写入，不能视为本会话的完整日志。");
            }

            return new SessionLogSourceInfo(
                SessionLogSourceKind.GameLog,
                null,
                null,
                SessionSourceAvailability.NotGenerated,
                null,
                null,
                "本次启动尚未写入日志；实例目录中的 latest.log 早于本会话，不能代表本次启动。");
        }

        var state = manifest.State;
        var notGenerated = state is GameSessionState.Preparing or GameSessionState.Starting;
        return new SessionLogSourceInfo(
            SessionLogSourceKind.GameLog,
            null,
            null,
            notGenerated ? SessionSourceAvailability.NotGenerated : SessionSourceAvailability.Missing,
            null,
            null,
            notGenerated ? "游戏日志尚未生成。" : "没有为本次启动登记可读取的游戏日志。");
    }

    /// <summary>只有声明了会话独立日志配置的方式，才可能在会话目录产生 game.log。</summary>
    private static bool IsolationPrepared(string isolation) =>
        string.Equals(isolation, SessionLogIsolationModes.Direct, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(isolation, SessionLogIsolationModes.Pending, StringComparison.OrdinalIgnoreCase);

    /// <summary>文件在会话创建之后是否被写过；更早的内容只属于上一次启动。</summary>
    private static bool WrittenSince(string path, DateTimeOffset since)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) >= since.UtcDateTime;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SessionLogSourceInfo DescribeProcessSource(
        GameLogSession session,
        SessionLogSourceKind kind,
        string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                var state = session.Manifest.State;
                var notGenerated = state is GameSessionState.Preparing or GameSessionState.Starting;
                return new SessionLogSourceInfo(
                    kind,
                    null,
                    ArtifactOwnership.Direct,
                    notGenerated ? SessionSourceAvailability.NotGenerated : SessionSourceAvailability.Missing,
                    null,
                    null,
                    notGenerated ? "该进程输出尚未生成。" : "该进程输出没有捕获到文件。");
            }

            return new SessionLogSourceInfo(
                kind,
                path,
                ArtifactOwnership.Direct,
                info.Length == 0 ? SessionSourceAvailability.NotGenerated : SessionSourceAvailability.Available,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                info.Length == 0 ? "该进程输出已创建但尚未写入内容。" : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SessionLogSourceInfo(
                kind,
                path,
                ArtifactOwnership.Direct,
                SessionSourceAvailability.Unreadable,
                null,
                null,
                exception.Message);
        }
    }

    private static SessionEvidenceItem DescribeArtifact(SessionArtifact artifact)
    {
        var path = ExistingPath(artifact.CapturedPath) ?? ExistingPath(artifact.SourcePath);
        if (path is null)
        {
            return new SessionEvidenceItem(
                artifact,
                SessionSourceAvailability.Missing,
                null,
                null,
                null,
                "原始文件已不存在；登记的元数据仍然保留。");
        }

        try
        {
            var info = new FileInfo(path);
            var partial = !string.IsNullOrWhiteSpace(artifact.Warning);
            return new SessionEvidenceItem(
                artifact,
                partial ? SessionSourceAvailability.Partial : SessionSourceAvailability.Available,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                path,
                artifact.Warning);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SessionEvidenceItem(
                artifact,
                SessionSourceAvailability.Unreadable,
                null,
                null,
                path,
                exception.Message);
        }
    }

    private static SessionLogSourceInfo DescribeFile(
        SessionLogSourceKind kind,
        string path,
        ArtifactOwnership ownership,
        string? warning,
        bool reportEmptyAsNotGenerated = false)
    {
        try
        {
            var info = new FileInfo(path);
            if (reportEmptyAsNotGenerated && info.Length == 0)
            {
                // 文件已创建但还没有写入：这是“尚未生成内容”，不是可用来源。
                return new SessionLogSourceInfo(
                    kind,
                    null,
                    ownership,
                    SessionSourceAvailability.NotGenerated,
                    0,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    "该来源已创建但尚未写入内容。");
            }

            return new SessionLogSourceInfo(
                kind,
                path,
                ownership,
                SessionSourceAvailability.Available,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                warning);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SessionLogSourceInfo(
                kind,
                path,
                ownership,
                SessionSourceAvailability.Unreadable,
                null,
                null,
                exception.Message);
        }
    }

    private string? ResolveReadablePath(GameLogSession session, SessionLogSourceKind kind)
    {
        if (kind == SessionLogSourceKind.Evidence)
        {
            return null;
        }

        var source = DescribeSources(session).FirstOrDefault(item => item.Kind == kind);
        return source?.Availability is SessionSourceAvailability.Available or SessionSourceAvailability.Partial
            ? ExistingPath(source.Path)
            : null;
    }

    private static string? ExistingPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    private static SessionLogChunk EmptyChunk(string? error) =>
        new([], 0, 0, false, false, false, error);

    private static SessionLogChunk ReadLatest(string path, int maxBytes, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxBytes, 4096, MaxChunkBytes);
        try
        {
            using var stream = OpenShared(path);
            var length = stream.Length;
            var start = Math.Max(0, length - limit);
            stream.Seek(start, SeekOrigin.Begin);

            var bytes = ReadBytes(stream, (int)(length - start), cancellationToken);
            var (text, skipped) = start > 0 ? SkipPartialLeadingLine(bytes) : (Decode(bytes), false);
            var lines = SplitLines(text);
            return new SessionLogChunk(
                lines,
                start,
                length,
                start > 0,
                false,
                skipped,
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return EmptyChunk(exception.Message);
        }
    }

    /// <summary>
    /// 读取文件开头的窗口。窗口没有覆盖整个文件时丢掉末尾可能被截断的半行，
    /// 让下一段严格从行首继续，避免把半行伪装成完整行。
    /// </summary>
    private static SessionLogChunk ReadHead(string path, int maxBytes, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxBytes, 4096, MaxChunkBytes);
        try
        {
            using var stream = OpenShared(path);
            var length = stream.Length;
            var count = (int)Math.Min(limit, length);
            stream.Seek(0, SeekOrigin.Begin);
            var raw = ReadBytes(stream, count, cancellationToken);
            var hasMoreAfter = count < length;
            var bytes = hasMoreAfter ? TrimTrailingPartialLine(raw) : raw;
            // 极端情况：首窗口内没有换行（单行巨型文件）。保留原字节继续前进，不制造空窗口。
            if (bytes.Length == 0 && raw.Length > 0)
            {
                bytes = raw;
            }

            return new SessionLogChunk(
                SplitLines(Decode(bytes)),
                0,
                bytes.Length,
                false,
                hasMoreAfter,
                false,
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return EmptyChunk(exception.Message);
        }
    }

    private static SessionLogChunk ReadFrom(string path, long offset, int maxBytes, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxBytes, 4096, MaxChunkBytes);
        try
        {
            using var stream = OpenShared(path);
            var length = stream.Length;
            var truncated = offset < 0 || offset > length;
            var start = truncated ? 0 : offset;
            var count = (int)Math.Min(limit, Math.Max(0, length - start));
            stream.Seek(start, SeekOrigin.Begin);
            var bytes = ReadBytes(stream, count, cancellationToken);
            var text = Decode(bytes);
            if (truncated)
            {
                // 截断/轮转后从文件开头重新读取；调用方必须重建视图而不是拼接旧内容。
                var truncatedLines = SplitLines(text);
                return new SessionLogChunk(
                    truncatedLines,
                    0,
                    bytes.Length,
                    false,
                    bytes.Length < length,
                    false,
                    null);
            }

            return new SessionLogChunk(
                SplitLines(text),
                start,
                start + bytes.Length,
                false,
                start + bytes.Length < length,
                false,
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return EmptyChunk(exception.Message);
        }
    }

    private static SessionLogChunk ReadBefore(string path, long beforeOffset, int maxBytes, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxBytes, 4096, MaxChunkBytes);
        try
        {
            using var stream = OpenShared(path);
            var length = stream.Length;
            var end = Math.Clamp(beforeOffset, 0, length);
            var start = Math.Max(0, end - limit);
            stream.Seek(start, SeekOrigin.Begin);
            var bytes = ReadBytes(stream, (int)(end - start), cancellationToken);
            var (text, skipped) = start > 0 ? SkipPartialLeadingLine(bytes) : (Decode(bytes), false);
            var lines = SplitLines(text);
            return new SessionLogChunk(
                lines,
                start,
                end,
                start > 0,
                end < length,
                skipped,
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return EmptyChunk(exception.Message);
        }
    }

    /// <summary>读取指定偏移之后的一段；偏移本身来自上一次读取的 <see cref="SessionLogChunk.EndOffset"/>，
    /// 因此总是行首对齐。窗口未覆盖文件末尾时丢弃末尾半行。</summary>
    private static SessionLogChunk ReadAfter(string path, long afterOffset, int maxBytes, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxBytes, 4096, MaxChunkBytes);
        try
        {
            using var stream = OpenShared(path);
            var length = stream.Length;
            var start = Math.Clamp(afterOffset, 0, length);
            var count = (int)Math.Min(limit, length - start);
            stream.Seek(start, SeekOrigin.Begin);
            var raw = ReadBytes(stream, count, cancellationToken);
            var hasMoreAfter = start + count < length;
            var bytes = hasMoreAfter ? TrimTrailingPartialLine(raw) : raw;
            if (bytes.Length == 0 && raw.Length > 0)
            {
                bytes = raw;
            }

            return new SessionLogChunk(
                SplitLines(Decode(bytes)),
                start,
                start + bytes.Length,
                start > 0,
                hasMoreAfter,
                false,
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return EmptyChunk(exception.Message);
        }
    }

    /// <summary>把窗口裁剪到最后一个换行之后；窗口内没有换行时返回空，由调用方决定如何前进。</summary>
    private static byte[] TrimTrailingPartialLine(byte[] bytes)
    {
        for (var index = bytes.Length - 1; index >= 0; index--)
        {
            if (bytes[index] == (byte)'\n')
            {
                return bytes[..(index + 1)];
            }
        }

        return [];
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static byte[] ReadBytes(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stream.Read(buffer, read, count - read);
            if (current == 0)
            {
                break;
            }

            read += current;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static (string Text, bool Skipped) SkipPartialLeadingLine(byte[] bytes)
    {
        // 从字节窗口中间开始时，第一行很可能被截断。向文件后面找换行并从下一行开始，
        // 避免把半行伪装成完整日志行；若窗口内没有换行则整段跳过。
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == (byte)'\n')
            {
                var text = Decode(bytes[(index + 1)..]);
                return (text, true);
            }
        }

        return (string.Empty, true);
    }

    private static string Decode(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0)
        {
            count--;
        }

        var result = new string[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = lines[index].TrimEnd('\r');
        }

        return result;
    }
}
