// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecl.Download;

/// <summary>与 .part 同目录的身份元数据；不记录本机绝对路径，只保存目标路径哈希与文件名。</summary>
public sealed record DownloadArtifactMetadata
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string DestinationKey { get; init; }

    public required string DestinationName { get; init; }

    public long? ExpectedSize { get; init; }

    public string? ExpectedHash { get; init; }

    public HashingAlgorithm Algorithm { get; init; }

    public string SourceKey { get; init; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastUsedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>由旧版 ECL 留下、首次被新逻辑接管时标记。</summary>
    public bool Migrated { get; init; }
}

public sealed record ArtifactPreparation(
    bool CanResume,
    long ExistingLength,
    bool Restarted,
    string? Reason);

public sealed record ArtifactCleanupReport(
    int Scanned,
    int Deleted,
    long BytesFreed,
    int ActiveSkipped,
    int Retained,
    IReadOnlyList<string> Errors);

/// <summary>活动下载租约：独占锁文件 + 进程内登记，清理器不能删除仍被引用的 .part。</summary>
public sealed class DownloadArtifactLease : IDisposable
{
    private readonly DownloadArtifactLifecycle _owner;
    private readonly FileStream _lock;
    private int _disposed;

    internal DownloadArtifactLease(DownloadArtifactLifecycle owner, string partPath, FileStream lockStream)
    {
        _owner = owner;
        PartPath = partPath;
        _lock = lockStream;
    }

    public string PartPath { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lock.Dispose();
        _owner.Release(this);
        try
        {
            File.Delete(LockPath(PartPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 锁文件清理失败不影响下载；下次获取仍可覆盖。
        }
    }

    internal static string LockPath(string partPath) => partPath + ".lock";
}

/// <summary>
/// 下载临时产物生命周期：身份校验、可恢复保留、活动租约、保留期与容量上限清理。
/// 清理失败只记录诊断，不影响已完成的下载。
/// </summary>
public sealed class DownloadArtifactLifecycle
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.OrdinalIgnoreCase);

    public DownloadArtifactLifecycle(TimeSpan? retention = null, long maxTotalBytes = 2L * 1024 * 1024 * 1024)
    {
        Retention = retention ?? TimeSpan.FromDays(7);
        MaxTotalBytes = Math.Max(maxTotalBytes, 0);
    }

    public TimeSpan Retention { get; }

    public long MaxTotalBytes { get; }

    public static string MetadataPath(string partPath) => partPath + ".json";

    public bool IsActive(string partPath) => _active.ContainsKey(Path.GetFullPath(partPath));

    public DownloadArtifactLease? TryAcquire(string partPath)
    {
        var fullPart = Path.GetFullPath(partPath);
        if (!_active.TryAdd(fullPart, 0))
        {
            return null;
        }

        FileStream? stream = null;
        try
        {
            var lockPath = DownloadArtifactLease.LockPath(fullPart);
            var directory = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            var payload = Encoding.UTF8.GetBytes($"{Environment.ProcessId} {DateTimeOffset.UtcNow:O}");
            stream.SetLength(0);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
            return new DownloadArtifactLease(this, fullPart, stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            _active.TryRemove(fullPart, out _);
            return null;
        }
    }

    internal void Release(DownloadArtifactLease lease) => _active.TryRemove(lease.PartPath, out _);

    public async Task<ArtifactPreparation> PrepareAsync(
        string partPath,
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        var fullPart = Path.GetFullPath(partPath);
        var metadataPath = MetadataPath(fullPart);
        if (!File.Exists(fullPart))
        {
            TryDelete(metadataPath);
            return new ArtifactPreparation(false, 0, false, null);
        }

        var length = new FileInfo(fullPart).Length;
        var expected = BuildMetadata(request);
        var metadata = await ReadMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        if (metadata is not null && !Matches(metadata, expected))
        {
            TryDelete(fullPart);
            TryDelete(metadataPath);
            return new ArtifactPreparation(false, 0, true, "临时文件身份与当前下载目标不一致，已重新开始。");
        }

        if (expected.ExpectedSize is { } maxSize && length > maxSize)
        {
            TryDelete(fullPart);
            TryDelete(metadataPath);
            return new ArtifactPreparation(false, 0, true, "临时文件大于预期大小，已重新开始。");
        }

        if (metadata is null && expected.ExpectedSize is null && string.IsNullOrWhiteSpace(expected.ExpectedHash))
        {
            // 没有大小/Hash 无法确认旧临时文件属于当前目标。
            TryDelete(fullPart);
            return new ArtifactPreparation(false, 0, true, "缺少可验证的下载身份，旧临时文件已丢弃。");
        }

        var current = metadata is null
            ? expected with { Migrated = true }
            : metadata with { LastUsedAtUtc = DateTimeOffset.UtcNow };
        await WriteMetadataAsync(metadataPath, current, cancellationToken).ConfigureAwait(false);
        return new ArtifactPreparation(length > 0, length, false, metadata is null ? "已接管旧版临时文件。" : null);
    }

    public async Task TouchAsync(string partPath, CancellationToken cancellationToken = default)
    {
        var fullPart = Path.GetFullPath(partPath);
        var metadataPath = MetadataPath(fullPart);
        var metadata = await ReadMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return;
        }

        try
        {
            await WriteMetadataAsync(
                    metadataPath,
                    metadata with { LastUsedAtUtc = DateTimeOffset.UtcNow },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 元数据更新失败不应阻断下载。
        }
    }

    public void Complete(string partPath)
    {
        var fullPart = Path.GetFullPath(partPath);
        TryDelete(MetadataPath(fullPart));
        // 成功时 .part 已被移动为最终文件；残留的空文件只可能是异常竞态。
        if (File.Exists(fullPart) && new FileInfo(fullPart).Length == 0)
        {
            TryDelete(fullPart);
        }
    }

    public void Discard(string partPath)
    {
        var fullPart = Path.GetFullPath(partPath);
        TryDelete(fullPart);
        TryDelete(MetadataPath(fullPart));
    }

    public Task<ArtifactCleanupReport> CleanupAsync(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Cleanup(roots, cancellationToken),
            cancellationToken);

    private ArtifactCleanupReport Cleanup(IEnumerable<string> roots, CancellationToken cancellationToken)
    {
        var scanned = 0;
        var deleted = 0;
        long freed = 0;
        var active = 0;
        var retained = 0;
        var errors = new List<string>();
        var now = DateTimeOffset.UtcNow;
        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(
                    root,
                    "*.part",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        MaxRecursionDepth = 8,
                    });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"无法扫描 {root}：{exception.Message}");
                continue;
            }

            foreach (var part in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                TryDeleteIfStaleLock(part);
                var metadata = ReadMetadata(part);
                var lastWrite = File.GetLastWriteTimeUtc(part);
                var lastUsed = metadata?.LastUsedAtUtc.UtcDateTime ?? lastWrite;
                var length = new FileInfo(part).Length;
                if (now.UtcDateTime - lastUsed < Retention)
                {
                    retained++;
                    continue;
                }

                using var lease = TryAcquire(part);
                if (lease is null)
                {
                    active++;
                    continue;
                }

                try
                {
                    if (!File.Exists(part))
                    {
                        continue;
                    }

                    File.Delete(part);
                    TryDelete(MetadataPath(part));
                    deleted++;
                    freed += length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"删除失败：{exception.Message}");
                }

            }
        }

        // 容量上限：优先删除最旧的、不在活动租约中的临时产物。
        var remaining = new List<(string Part, DateTimeOffset LastUsed, long Length)>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var part in Directory.EnumerateFiles(
                             root,
                             "*.part",
                             new EnumerationOptions
                             {
                                 RecurseSubdirectories = true,
                                 IgnoreInaccessible = true,
                                 AttributesToSkip = FileAttributes.ReparsePoint,
                                 MaxRecursionDepth = 8,
                             }))
                {
                    var metadata = ReadMetadata(part);
                    remaining.Add((
                        part,
                        metadata?.LastUsedAtUtc ?? new DateTimeOffset(File.GetLastWriteTimeUtc(part)),
                        new FileInfo(part).Length));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"容量统计失败：{exception.Message}");
            }
        }

        var total = remaining.Sum(item => item.Length);
        foreach (var item in remaining.OrderBy(item => item.LastUsed))
        {
            if (total <= MaxTotalBytes)
            {
                break;
            }

            if (IsActive(item.Part))
            {
                continue;
            }

            using var lease = TryAcquire(item.Part);
            if (lease is null)
            {
                continue;
            }

            try
            {
                File.Delete(item.Part);
                TryDelete(MetadataPath(item.Part));
                deleted++;
                freed += item.Length;
                total -= item.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"容量清理删除失败：{exception.Message}");
            }
        }

        return new ArtifactCleanupReport(scanned, deleted, freed, active, retained, errors);
    }

    private static void TryDeleteIfStaleLock(string partPath)
    {
        var lockPath = DownloadArtifactLease.LockPath(partPath);
        try
        {
            if (!File.Exists(lockPath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) < TimeSpan.FromDays(1))
            {
                return;
            }

            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            File.Delete(lockPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 仍被占用或无法删除，保留。
        }
    }

    private DownloadArtifactMetadata? ReadMetadata(string partPath) =>
        ReadMetadataAsync(MetadataPath(partPath), CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<DownloadArtifactMetadata?> ReadMetadataAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            var metadata = await JsonSerializer.DeserializeAsync<DownloadArtifactMetadata>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return metadata is { SchemaVersion: <= DownloadArtifactMetadata.CurrentSchemaVersion } ? metadata : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task WriteMetadataAsync(
        string path,
        DownloadArtifactMetadata metadata,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static DownloadArtifactMetadata BuildMetadata(DownloadRequest request)
    {
        var destination = Path.GetFullPath(request.DestinationPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination.ToLowerInvariant())))[..32];
        var sourceKey = string.Join(
            ',',
            request.Urls
                .Select(uri => uri.Host.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(host => host, StringComparer.Ordinal));
        return new DownloadArtifactMetadata
        {
            DestinationKey = key,
            DestinationName = Path.GetFileName(destination),
            ExpectedSize = request.Size,
            ExpectedHash = request.Hash,
            Algorithm = request.Algorithm,
            SourceKey = sourceKey,
        };
    }

    private static bool Matches(DownloadArtifactMetadata metadata, DownloadArtifactMetadata expected)
    {
        if (!string.Equals(metadata.DestinationKey, expected.DestinationKey, StringComparison.Ordinal) ||
            !string.Equals(metadata.DestinationName, expected.DestinationName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (metadata.ExpectedSize is { } oldSize && expected.ExpectedSize is { } newSize && oldSize != newSize)
        {
            return false;
        }

        if (metadata.ExpectedHash is { Length: > 0 } oldHash &&
            expected.ExpectedHash is { Length: > 0 } newHash &&
            !string.Equals(oldHash, newHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (metadata.ExpectedHash is { Length: > 0 } &&
            expected.ExpectedHash is { Length: > 0 } &&
            metadata.Algorithm != expected.Algorithm)
        {
            return false;
        }

        return string.IsNullOrEmpty(metadata.SourceKey) ||
               string.IsNullOrEmpty(expected.SourceKey) ||
               string.Equals(metadata.SourceKey, expected.SourceKey, StringComparison.Ordinal);
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
            // 删除失败由后续清理周期重试。
        }
    }
}
