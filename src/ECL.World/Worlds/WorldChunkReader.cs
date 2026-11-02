// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.World.Chunks;
using Ecl.World.Region;

namespace Ecl.World.Worlds;

public sealed record ChunkLoadResult(
    int X,
    int Z,
    ChunkData? Chunk,
    RegionChunkStatus Status,
    string? Error)
{
    public bool Success => Status == RegionChunkStatus.Ok && Chunk is not null;
}

/// <summary>
/// 按维度读取区块。Region 文件按需打开并有界缓存；单个 Region/Chunk 失败只影响对应结果。
/// 所有坐标使用 Minecraft 的全局 Chunk 坐标，负数除法采用算术右移。
/// </summary>
public sealed class WorldChunkReader : IDisposable
{
    private const int MaxOpenRegions = 8;

    private readonly WorldSave _world;
    private readonly DimensionInfo _dimension;
    private readonly Dictionary<(int X, int Z), RegionFile?> _regions = [];
    private readonly Queue<(int X, int Z)> _regionOrder = [];
    private readonly object _gate = new();
    private bool _disposed;

    public WorldChunkReader(WorldSave world, DimensionInfo dimension)
    {
        _world = world;
        _dimension = dimension;
    }

    public WorldSave World => _world;

    public DimensionInfo Dimension => _dimension;

    public ChunkLoadResult ReadChunk(int chunkX, int chunkZ)
    {
        if (_dimension.RegionDirectory is null)
        {
            return new ChunkLoadResult(chunkX, chunkZ, null, RegionChunkStatus.Missing, "该维度没有 Region 存储。");
        }

        var region = GetRegion(RegionFile.ChunkToRegion(chunkX), RegionFile.ChunkToRegion(chunkZ));
        if (region is null)
        {
            return new ChunkLoadResult(chunkX, chunkZ, null, RegionChunkStatus.Missing, null);
        }

        var read = region.ReadChunk(RegionFile.ChunkToLocal(chunkX), RegionFile.ChunkToLocal(chunkZ));
        if (read.Status != RegionChunkStatus.Ok || read.Data is null)
        {
            return new ChunkLoadResult(chunkX, chunkZ, null, read.Status, read.Error);
        }

        var parsed = ChunkData.Parse(read.Data, _dimension.Id);
        return parsed.Success
            ? new ChunkLoadResult(chunkX, chunkZ, parsed.Chunk, RegionChunkStatus.Ok, null)
            : new ChunkLoadResult(chunkX, chunkZ, null, RegionChunkStatus.Corrupt, parsed.Error);
    }

    public IReadOnlyList<ChunkLoadResult> ReadArea(
        int centerChunkX,
        int centerChunkZ,
        int radius,
        CancellationToken cancellationToken = default)
    {
        radius = Math.Clamp(radius, 0, 32);
        var result = new List<ChunkLoadResult>((radius * 2 + 1) * (radius * 2 + 1));
        for (var z = centerChunkZ - radius; z <= centerChunkZ + radius; z++)
        {
            for (var x = centerChunkX - radius; x <= centerChunkX + radius; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(ReadChunk(x, z));
            }
        }

        return result;
    }

    public IReadOnlyList<(int X, int Z)> ListPresentChunks()
    {
        if (_dimension.RegionDirectory is null || !Directory.Exists(_dimension.RegionDirectory))
        {
            return [];
        }

        var result = new List<(int, int)>();
        foreach (var path in Directory.EnumerateFiles(_dimension.RegionDirectory, "r.*.*.mca").Take(1024))
        {
            var coordinates = RegionFile.ParseCoordinates(path);
            if (coordinates is null)
            {
                continue;
            }

            using var region = new RegionFile(path);
            foreach (var (localX, localZ) in region.ListPresentChunks())
            {
                result.Add((coordinates.Value.X * 32 + localX, coordinates.Value.Z * 32 + localZ));
            }
        }

        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var region in _regions.Values)
            {
                region?.Dispose();
            }

            _regions.Clear();
        }
    }

    private RegionFile? GetRegion(int regionX, int regionZ)
    {
        lock (_gate)
        {
            if (_regions.TryGetValue((regionX, regionZ), out var cached))
            {
                return cached;
            }

            var path = Path.Combine(_dimension.RegionDirectory!, $"r.{regionX}.{regionZ}.mca");
            RegionFile? region = null;
            if (File.Exists(path))
            {
                try
                {
                    region = new RegionFile(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    region = null;
                }
            }

            _regions[(regionX, regionZ)] = region;
            _regionOrder.Enqueue((regionX, regionZ));
            while (_regionOrder.Count > MaxOpenRegions)
            {
                var old = _regionOrder.Dequeue();
                if (_regions.Remove(old, out var oldRegion))
                {
                    oldRegion?.Dispose();
                }
            }

            return region;
        }
    }
}
