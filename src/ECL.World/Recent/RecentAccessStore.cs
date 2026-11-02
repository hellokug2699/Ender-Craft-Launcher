// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecl.World.Recent;

public enum RecentAccessKind
{
    World,
    Server,
    GameLaunch,
}

public sealed record RecentAccessEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public RecentAccessKind Kind { get; init; }

    public required string InstanceId { get; init; }

    /// <summary>稳定的对象标识：存档文件夹名、服务器地址或会话 ID。</summary>
    public required string TargetId { get; init; }

    public required string DisplayName { get; init; }

    public string? Detail { get; init; }

    /// <summary>存档的规范化路径；服务器记录为空。用于重新定位，不作为删除授权。</summary>
    public string? WorldPath { get; init; }

    public string? DimensionId { get; init; }

    public int? ChunkX { get; init; }

    public int? ChunkZ { get; init; }

    public DateTimeOffset VisitedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RecentAccessSnapshot(
    IReadOnlyList<RecentAccessEntry> Entries,
    bool IsCorrupt,
    string? Error)
{
    public static RecentAccessSnapshot Empty => new([], false, null);
}

/// <summary>
/// 本地最近访问记录。只用于导航，不写入游戏存档或 servers.dat，也不进入遥测。
/// 文件损坏时返回空列表，绝不影响 ECL 启动。
/// </summary>
public sealed class RecentAccessStore(string filePath)
{
    public const int CurrentSchemaVersion = 1;
    private const int MaxEntries = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath { get; } = Path.GetFullPath(filePath);

    public async Task<RecentAccessSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return RecentAccessSnapshot.Empty;
        }

        try
        {
            await using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<RecentAccessDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (document is null || document.SchemaVersion > CurrentSchemaVersion)
            {
                return new RecentAccessSnapshot([], true, "最近访问文件版本不受支持。");
            }

            return new RecentAccessSnapshot(
                document.Entries.OrderByDescending(entry => entry.VisitedAtUtc).Take(MaxEntries).ToArray(),
                false,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new RecentAccessSnapshot([], true, $"最近访问读取失败：{exception.Message}");
        }
    }

    public async Task<RecentAccessEntry> RecordAsync(
        RecentAccessEntry entry,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var entries = current.Entries
                .Where(existing =>
                    !(existing.Kind == entry.Kind &&
                      string.Equals(existing.InstanceId, entry.InstanceId, StringComparison.Ordinal) &&
                      string.Equals(existing.TargetId, entry.TargetId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var recorded = entry with { VisitedAtUtc = DateTimeOffset.UtcNow };
            entries.Insert(0, recorded);
            await SaveAsync(entries.Take(MaxEntries).ToArray(), cancellationToken).ConfigureAwait(false);
            return recorded;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            await SaveAsync(
                    current.Entries.Where(entry => !string.Equals(entry.Id, id, StringComparison.Ordinal)).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAsync([], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync(IReadOnlyList<RecentAccessEntry> entries, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var document = new RecentAccessDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Entries = entries,
            };
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // 临时文件清理失败不影响结果。
                }
            }
        }
    }

    private sealed record RecentAccessDocument
    {
        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        public IReadOnlyList<RecentAccessEntry> Entries { get; init; } = [];
    }
}
