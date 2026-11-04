// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using Ecl.World.Nbt;

namespace Ecl.World.Region;

public enum RegionChunkStatus
{
    Ok,
    Missing,
    Corrupt,
    UnsupportedCompression,
}

public sealed record RegionChunkReadResult(RegionChunkStatus Status, NbtTag? Data, string? Error)
{
    public static RegionChunkReadResult Ok(NbtTag data) => new(RegionChunkStatus.Ok, data, null);

    public static RegionChunkReadResult Missing() => new(RegionChunkStatus.Missing, null, null);

    public static RegionChunkReadResult Corrupt(string error) => new(RegionChunkStatus.Corrupt, null, error);

    public static RegionChunkReadResult Unsupported(string error) =>
        new(RegionChunkStatus.UnsupportedCompression, null, error);
}

/// <summary>
/// Anvil Region（<c>r.x.z.mca</c>）读取器。位置表、扇区边界、压缩流和 NBT 解析均做边界检查；
/// 单个区块损坏只返回隔离结果，不抛出影响整个世界的异常。
/// </summary>
public sealed class RegionFile : IDisposable
{
    public const int SectorSize = 4096;
    public const int ChunkCount = 32 * 32;
    private const int HeaderSize = 2 * SectorSize;
    private const int MaxChunkBytes = 16 * 1024 * 1024;

    private readonly FileStream _stream;
    private readonly object _gate = new();

    public RegionFile(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Coordinates = ParseCoordinates(Path);
        _stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            SectorSize,
            FileOptions.RandomAccess);
        if (_stream.Length < HeaderSize)
        {
            throw new InvalidDataException($"Region 文件小于 8 KiB 头部：{Path}");
        }
    }

    public string Path { get; }

    /// <summary>文件名中的 Region 坐标；无法解析时为 null。</summary>
    public (int X, int Z)? Coordinates { get; }

    public static (int X, int Z)? ParseCoordinates(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('.');
        if (parts.Length == 3 &&
            string.Equals(parts[0], "r", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[1], out var x) &&
            int.TryParse(parts[2], out var z))
        {
            return (x, z);
        }

        return null;
    }

    public RegionChunkReadResult ReadChunk(int localX, int localZ)
    {
        if (localX is < 0 or > 31 || localZ is < 0 or > 31)
        {
            return RegionChunkReadResult.Corrupt("区块局部坐标必须在 0..31。");
        }

        try
        {
            var index = localX + localZ * 32;
            var header = ReadAt(4L * index, 4);
            var location = BinaryPrimitives.ReadUInt32BigEndian(header);
            var sectorOffset = (int)(location >> 8);
            var sectorCount = (int)(location & 0xFF);
            if (sectorOffset == 0 || sectorCount == 0)
            {
                return RegionChunkReadResult.Missing();
            }

            var offset = (long)sectorOffset * SectorSize;
            if (offset < HeaderSize || offset + 5 > _stream.Length)
            {
                return RegionChunkReadResult.Corrupt("区块偏移超出 Region 文件范围。");
            }

            var lengthBytes = ReadAt(offset, 4);
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length <= 0 || length > MaxChunkBytes)
            {
                return RegionChunkReadResult.Corrupt($"区块数据长度无效：{length}。");
            }

            if (offset + 4 + length > _stream.Length)
            {
                return RegionChunkReadResult.Corrupt("区块数据越界。");
            }

            var compressionByte = ReadAt(offset + 4, 1)[0];
            var external = (compressionByte & 0x80) != 0;
            var compression = compressionByte & 0x7F;

            byte[] compressed;
            if (external)
            {
                var externalPath = System.IO.Path.ChangeExtension(Path, ".mcc");
                var externalRead = ReadExternalChunk(externalPath, length, compression);
                if (externalRead is null)
                {
                    return RegionChunkReadResult.Corrupt("外部区块数据缺失或无效。");
                }

                compressed = externalRead;
            }
            else
            {
                compressed = ReadAt(offset + 5, length - 1);
            }

            var raw = Decompress(compressed, compression);
            if (raw is null)
            {
                return RegionChunkReadResult.Unsupported($"不支持的区块压缩类型：{compression}。");
            }

            try
            {
                using var payload = new MemoryStream(raw, writable: false);
                var root = NbtFile.Read(payload, new NbtReadOptions { MaxBytes = MaxChunkBytes });
                return RegionChunkReadResult.Ok(root);
            }
            catch (Exception exception) when (exception is NbtFormatException or InvalidDataException or IOException)
            {
                return RegionChunkReadResult.Corrupt($"区块 NBT 解析失败：{exception.Message}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return RegionChunkReadResult.Corrupt($"读取 Region 失败：{exception.Message}");
        }
    }

    public IReadOnlyList<(int LocalX, int LocalZ)> ListPresentChunks()
    {
        var result = new List<(int, int)>();
        try
        {
            var header = ReadAt(0, HeaderSize);
            for (var index = 0; index < ChunkCount; index++)
            {
                var location = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(index * 4, 4));
                if (location >> 8 != 0)
                {
                    result.Add((index % 32, index / 32));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 无法读取头部时返回空列表，由调用方按损坏 Region 展示。
        }

        return result;
    }

    public static int ChunkToRegion(int chunkCoordinate) => chunkCoordinate >> 5;

    public static int ChunkToLocal(int chunkCoordinate) => chunkCoordinate & 31;

    public void Dispose()
    {
        lock (_gate)
        {
            _stream.Dispose();
        }
    }

    private byte[]? ReadExternalChunk(string path, int declaredLength, int compression)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[5];
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            var type = header[4];
            if (length <= 0 || length > MaxChunkBytes || (type & 0x7F) != compression)
            {
                return null;
            }

            var data = new byte[length - 1];
            stream.ReadExactly(data);
            return data;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private byte[]? Decompress(byte[] compressed, int compression)
    {
        Stream? stream = compression switch
        {
            1 => new GZipStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress),
            2 => new ZLibStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress),
            3 => new MemoryStream(compressed, writable: false),
            _ => null,
        };
        if (stream is null)
        {
            return null;
        }

        using (stream)
        {
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            var total = 0;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaxChunkBytes)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
    }

    private byte[] ReadAt(long offset, int count)
    {
        if (count < 0 || offset < 0 || offset + count > _stream.Length)
        {
            throw new InvalidDataException($"Region 读取越界：offset={offset}, count={count}。");
        }

        lock (_gate)
        {
            _stream.Position = offset;
            var buffer = new byte[count];
            _stream.ReadExactly(buffer);
            return buffer;
        }
    }
}
