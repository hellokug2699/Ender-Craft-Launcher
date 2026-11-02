// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Ecl.World.Nbt;

public enum NbtCompression
{
    None,
    Gzip,
    Zlib,
}

/// <summary>解析上限。所有值均为防御异常/恶意文件而设，不参与业务语义。</summary>
public sealed record NbtReadOptions
{
    public int MaxDepth { get; init; } = 512;

    public int MaxArrayLength { get; init; } = 16 * 1024 * 1024;

    public int MaxStringLength { get; init; } = 4 * 1024 * 1024;

    public long MaxBytes { get; init; } = 256L * 1024 * 1024;
}

public static class NbtFile
{
    public static NbtTag ReadFile(string path, NbtReadOptions? options = null)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Read(stream, options);
    }

    public static NbtTag Read(Stream stream, NbtReadOptions? options = null)
    {
        options ??= new NbtReadOptions();
        using var buffered = new BufferedStream(stream, 64 * 1024);
        var header = new byte[2];
        var read = buffered.Read(header, 0, header.Length);
        if (read == 0)
        {
            throw new NbtFormatException("NBT 数据为空。");
        }

        var prefixed = new PrefixedStream(header.AsSpan(0, read).ToArray(), buffered);
        Stream payload;
        if (read == 2 && header[0] == 0x1F && header[1] == 0x8B)
        {
            payload = new GZipStream(prefixed, CompressionMode.Decompress);
        }
        else if (read == 2 && header[0] == 0x78 && header[1] is 0x01 or 0x5E or 0x9C or 0xDA)
        {
            payload = new ZLibStream(prefixed, CompressionMode.Decompress);
        }
        else
        {
            payload = prefixed;
        }

        using var counting = new CountingStream(payload, options.MaxBytes);
        var reader = new NbtBinaryReader(counting, options);
        var type = (NbtTagType)reader.ReadByte();
        if (type == NbtTagType.End)
        {
            return NbtTag.Compound();
        }

        var name = reader.ReadString();
        var root = reader.ReadPayload(type, name, 0, options);
        return root;
    }

    public static void WriteFile(string path, NbtTag root, NbtCompression compression = NbtCompression.Gzip)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.SequentialScan))
            using (var compressed = CreateCompressionStream(stream, compression))
            {
                Write(compressed, root);
            }

            File.Move(temporary, path, overwrite: true);
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
                    // 清理临时文件失败不应覆盖主结果。
                }
            }
        }
    }

    public static void Write(Stream stream, NbtTag root, NbtCompression compression = NbtCompression.None)
    {
        using var compressed = CreateCompressionStream(stream, compression);
        var writer = new NbtBinaryWriter(compressed);
        writer.WriteByte((byte)root.Type);
        writer.WriteString(root.Name);
        writer.WritePayload(root);
        writer.Flush();
    }

    private static Stream CreateCompressionStream(Stream stream, NbtCompression compression) => compression switch
    {
        NbtCompression.Gzip => new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true),
        NbtCompression.Zlib => new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true),
        _ => new PassThroughStream(stream),
    };

    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < prefix.Length)
            {
                var copied = Math.Min(count, prefix.Length - _offset);
                Array.Copy(prefix, _offset, buffer, offset, copied);
                _offset += copied;
                return copied;
            }

            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (_offset < prefix.Length)
            {
                var copied = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsSpan(_offset, copied).CopyTo(buffer);
                _offset += copied;
                return copied;
            }

            return inner.Read(buffer);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PassThroughStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }

    private sealed class CountingStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Count(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Count(read);
            return read;
        }

        private void Count(int read)
        {
            _read += read;
            if (_read > maxBytes)
            {
                throw new NbtFormatException($"NBT 解压后超过上限：{maxBytes} 字节。");
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class NbtBinaryReader(Stream stream, NbtReadOptions options)
{
    private readonly byte[] _buffer = new byte[8];

    public byte ReadByte()
    {
        var value = stream.ReadByte();
        if (value < 0)
        {
            throw new NbtFormatException("NBT 数据意外结束。");
        }

        return (byte)value;
    }

    public short ReadInt16()
    {
        ReadExactly(2);
        return BinaryPrimitives.ReadInt16BigEndian(_buffer);
    }

    public int ReadInt32()
    {
        ReadExactly(4);
        return BinaryPrimitives.ReadInt32BigEndian(_buffer);
    }

    public long ReadInt64()
    {
        ReadExactly(8);
        return BinaryPrimitives.ReadInt64BigEndian(_buffer);
    }

    public float ReadSingle()
    {
        ReadExactly(4);
        return BinaryPrimitives.ReadSingleBigEndian(_buffer);
    }

    public double ReadDouble()
    {
        ReadExactly(8);
        return BinaryPrimitives.ReadDoubleBigEndian(_buffer);
    }

    public string ReadString()
    {
        var length = ReadInt32();
        if (length < 0 || length > options.MaxStringLength)
        {
            throw new NbtFormatException($"NBT 字符串长度无效：{length}。");
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var bytes = new byte[length];
        ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    public NbtTag ReadPayload(NbtTagType type, string name, int depth, NbtReadOptions readOptions)
    {
        if (depth > readOptions.MaxDepth)
        {
            throw new NbtFormatException($"NBT 嵌套超过上限：{readOptions.MaxDepth}。");
        }

        switch (type)
        {
            case NbtTagType.Byte:
                return new NbtTag(type, name, ReadByte());
            case NbtTagType.Short:
                return new NbtTag(type, name, ReadInt16());
            case NbtTagType.Int:
                return new NbtTag(type, name, ReadInt32());
            case NbtTagType.Long:
                return new NbtTag(type, name, ReadInt64());
            case NbtTagType.Float:
                return new NbtTag(type, name, ReadSingle());
            case NbtTagType.Double:
                return new NbtTag(type, name, ReadDouble());
            case NbtTagType.ByteArray:
                {
                    var length = ReadLength(readOptions);
                    var bytes = new byte[length];
                    ReadExactly(bytes);
                    return new NbtTag(type, name, bytes);
                }
            case NbtTagType.String:
                return new NbtTag(type, name, ReadString());
            case NbtTagType.List:
                {
                    var elementType = (NbtTagType)ReadByte();
                    var length = ReadLength(readOptions);
                    if (elementType == NbtTagType.End && length > 0)
                    {
                        throw new NbtFormatException("列表声明了 End 元素类型，但长度不为 0。");
                    }

                    var list = NbtTag.List(name, elementType);
                    for (var index = 0; index < length; index++)
                    {
                        var element = ReadPayload(elementType, string.Empty, depth + 1, readOptions);
                        list.AddTo(element);
                    }

                    return list;
                }
            case NbtTagType.Compound:
                {
                    var compound = NbtTag.Compound(name);
                    while (true)
                    {
                        var childType = (NbtTagType)ReadByte();
                        if (childType == NbtTagType.End)
                        {
                            break;
                        }

                        if (childType > NbtTagType.LongArray)
                        {
                            throw new NbtFormatException($"未知 NBT 标签类型：{(byte)childType}。");
                        }

                        var childName = ReadString();
                        compound.Add(ReadPayload(childType, childName, depth + 1, readOptions));
                    }

                    return compound;
                }
            case NbtTagType.IntArray:
                {
                    var length = ReadLength(readOptions);
                    var values = new int[length];
                    for (var index = 0; index < length; index++)
                    {
                        values[index] = ReadInt32();
                    }

                    return new NbtTag(type, name, values);
                }
            case NbtTagType.LongArray:
                {
                    var length = ReadLength(readOptions);
                    var values = new long[length];
                    for (var index = 0; index < length; index++)
                    {
                        values[index] = ReadInt64();
                    }

                    return new NbtTag(type, name, values);
                }
            case NbtTagType.End:
                throw new NbtFormatException("不能在负载位置读取 End 标签。");
            default:
                throw new NbtFormatException($"未知 NBT 标签类型：{(byte)type}。");
        }
    }

    private int ReadLength(NbtReadOptions readOptions)
    {
        var length = ReadInt32();
        if (length < 0 || length > readOptions.MaxArrayLength)
        {
            throw new NbtFormatException($"NBT 数组长度无效：{length}。");
        }

        return length;
    }

    private void ReadExactly(int count) => ReadExactly(_buffer.AsSpan(0, count));

    private void ReadExactly(Span<byte> target)
    {
        var offset = 0;
        while (offset < target.Length)
        {
            var read = stream.Read(target[offset..]);
            if (read == 0)
            {
                throw new NbtFormatException("NBT 数据意外结束。");
            }

            offset += read;
        }
    }

    private void ReadExactly(byte[] target) => ReadExactly(target.AsSpan());
}

internal sealed class NbtBinaryWriter(Stream stream)
{
    private readonly byte[] _buffer = new byte[8];

    public void WriteByte(byte value) => stream.WriteByte(value);

    public void WriteInt16(short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(_buffer, value);
        stream.Write(_buffer, 0, 2);
    }

    public void WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(_buffer, value);
        stream.Write(_buffer, 0, 4);
    }

    public void WriteInt64(long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(_buffer, value);
        stream.Write(_buffer, 0, 8);
    }

    public void WriteSingle(float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(_buffer, value);
        stream.Write(_buffer, 0, 4);
    }

    public void WriteDouble(double value)
    {
        BinaryPrimitives.WriteDoubleBigEndian(_buffer, value);
        stream.Write(_buffer, 0, 8);
    }

    public void WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(bytes.Length);
        stream.Write(bytes);
    }

    public void WritePayload(NbtTag tag)
    {
        switch (tag.Type)
        {
            case NbtTagType.Byte:
                WriteByte(tag.GetByte());
                break;
            case NbtTagType.Short:
                WriteInt16(tag.GetShort());
                break;
            case NbtTagType.Int:
                WriteInt32(tag.GetInt());
                break;
            case NbtTagType.Long:
                WriteInt64(tag.GetLong());
                break;
            case NbtTagType.Float:
                WriteSingle(tag.GetFloat());
                break;
            case NbtTagType.Double:
                WriteDouble(tag.GetDouble());
                break;
            case NbtTagType.ByteArray:
                {
                    var bytes = tag.GetByteArray();
                    WriteInt32(bytes.Length);
                    stream.Write(bytes);
                    break;
                }
            case NbtTagType.String:
                WriteString(tag.GetString() ?? string.Empty);
                break;
            case NbtTagType.List:
                {
                    WriteByte((byte)tag.ListType);
                    WriteInt32(tag.Children.Count);
                    foreach (var child in tag.Children)
                    {
                        WritePayload(child);
                    }

                    break;
                }
            case NbtTagType.Compound:
                foreach (var child in tag.Children)
                {
                    WriteByte((byte)child.Type);
                    WriteString(child.Name);
                    WritePayload(child);
                }

                WriteByte((byte)NbtTagType.End);
                break;
            case NbtTagType.IntArray:
                {
                    var values = tag.GetIntArray();
                    WriteInt32(values.Length);
                    foreach (var value in values)
                    {
                        WriteInt32(value);
                    }

                    break;
                }
            case NbtTagType.LongArray:
                {
                    var values = tag.GetLongArray();
                    WriteInt32(values.Length);
                    foreach (var value in values)
                    {
                        WriteInt64(value);
                    }

                    break;
                }
            case NbtTagType.End:
                break;
            default:
                throw new NbtFormatException($"无法写入未知标签类型：{tag.Type}。");
        }
    }

    public void Flush() => stream.Flush();
}
