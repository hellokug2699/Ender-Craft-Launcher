// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Ecl.Wardrobe.Png;

/// <summary>本解码器明确支持的 PNG 颜色类型（8-bit、非隔行）。</summary>
public enum PngColorType : byte
{
    Grayscale = 0,
    Rgb = 2,
    Rgba = 6,
}

public enum PngValidationStatus
{
    Valid,

    Empty,
    FileNotFound,
    FileUnavailable,

    /// <summary>文件本身超过字节上限（在解码前拒绝，避免无意义地读入大文件）。</summary>
    TooLarge,

    InvalidSignature,
    Truncated,
    InvalidChunk,
    InvalidHeader,
    UnsupportedFormat,
    DimensionTooLarge,
    PixelCountTooLarge,
    MissingImageData,
    CorruptData,
}

/// <summary>
/// PNG 校验限制。默认值与 ADR 0037/0038 的安全边界一致：
/// 单文件不超过 1 MiB，单边不超过 128，像素总量不超过 128x128。
/// </summary>
public sealed record PngValidationLimits
{
    public static PngValidationLimits Default { get; } = new();

    public int MaxFileBytes { get; init; } = 1024 * 1024;

    public int MaxWidth { get; init; } = 128;

    public int MaxHeight { get; init; } = 128;

    public long MaxPixelCount { get; init; } = 128L * 128L;
}

public readonly record struct PngPixel(byte R, byte G, byte B, byte A);

/// <summary>
/// 解码后的 PNG。像素统一转换为 RGBA，便于皮肤模型启发式和预览使用。
/// </summary>
public sealed class PngImage
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required PngColorType ColorType { get; init; }

    /// <summary>逐行、逐像素的 RGBA 字节，长度为 <c>Width * Height * 4</c>。</summary>
    public required byte[] Rgba { get; init; }

    public PngPixel GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException($"像素坐标超出图像范围：({x}, {y})。");
        }

        var offset = (y * Width + x) * 4;
        return new PngPixel(Rgba[offset], Rgba[offset + 1], Rgba[offset + 2], Rgba[offset + 3]);
    }
}

public sealed record PngValidationResult
{
    public required PngValidationStatus Status { get; init; }

    public string Message { get; init; } = string.Empty;

    public PngImage? Image { get; init; }

    /// <summary>IHDR 中声明的尺寸；即使后续校验失败也尽量保留，便于诊断。</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    public bool IsValid => Status == PngValidationStatus.Valid;

    public bool IsUnsupportedFormat => Status == PngValidationStatus.UnsupportedFormat;
}

/// <summary>
/// 独立的 PNG 校验/最小解码器。刻意不依赖 WPF 或 System.Drawing，
/// 以便在无 UI 的核心程序集与单元测试中运行。
/// <para>
/// 安全边界：先检查文件大小与 IHDR 尺寸，再解压 IDAT；解压时只读取预期长度的数据，
/// 因此即使遇到 PNG 解压炸弹也不会按声明尺寸无限分配内存。
/// </para>
/// </summary>
public static class PngValidator
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static PngValidationResult Validate(ReadOnlySpan<byte> data, PngValidationLimits? limits = null)
    {
        limits ??= PngValidationLimits.Default;

        if (data.IsEmpty)
        {
            return Fail(PngValidationStatus.Empty, "PNG 文件为空。");
        }

        if (data.Length > limits.MaxFileBytes)
        {
            return Fail(
                PngValidationStatus.TooLarge,
                $"PNG 文件超过 {limits.MaxFileBytes} 字节上限（实际 {data.Length} 字节）。");
        }

        if (data.Length < PngSignature.Length)
        {
            return Fail(PngValidationStatus.Truncated, "PNG 文件长度不足，无法包含签名。");
        }

        if (!data[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return Fail(PngValidationStatus.InvalidSignature, "PNG 签名不正确。");
        }

        PngHeader? header = null;
        var idat = new MemoryStream();
        var sawIhdr = false;
        var sawIdat = false;
        var sawIend = false;
        var offset = PngSignature.Length;

        while (offset < data.Length)
        {
            if (offset + 8 > data.Length)
            {
                return Fail(PngValidationStatus.Truncated, "PNG 数据块头不完整。", header);
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (length > int.MaxValue)
            {
                return Fail(PngValidationStatus.InvalidChunk, "PNG 数据块长度超出支持范围。", header);
            }

            var chunkLength = (int)length;
            if (offset + 12L + chunkLength > data.Length)
            {
                return Fail(PngValidationStatus.Truncated, "PNG 数据块被截断。", header);
            }

            var typeBytes = data.Slice(offset + 4, 4);
            var type = Encoding.ASCII.GetString(typeBytes);
            var chunkData = data.Slice(offset + 8, chunkLength);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + chunkLength, 4));
            var actualCrc = ComputeCrc(data.Slice(offset + 4, chunkLength + 4));
            if (expectedCrc != actualCrc)
            {
                return Fail(PngValidationStatus.InvalidChunk, $"PNG 数据块 {type} 的 CRC 校验失败。", header);
            }

            if (!sawIhdr)
            {
                if (!string.Equals(type, "IHDR", StringComparison.Ordinal))
                {
                    return Fail(PngValidationStatus.InvalidHeader, "PNG 第一个数据块必须是 IHDR。", header);
                }

                if (chunkLength != 13)
                {
                    return Fail(PngValidationStatus.InvalidHeader, "PNG IHDR 长度必须为 13 字节。", header);
                }

                var parse = ParseHeader(chunkData, limits);
                if (!parse.IsValid)
                {
                    return parse;
                }

                header = new PngHeader(
                    parse.Width,
                    parse.Height,
                    chunkData[8],
                    chunkData[9],
                    chunkData[10],
                    chunkData[11],
                    chunkData[12]);
                sawIhdr = true;
            }
            else if (string.Equals(type, "IHDR", StringComparison.Ordinal))
            {
                return Fail(PngValidationStatus.InvalidChunk, "PNG 包含多个 IHDR 数据块。", header);
            }
            else if (string.Equals(type, "IDAT", StringComparison.Ordinal))
            {
                if (sawIend)
                {
                    return Fail(PngValidationStatus.InvalidChunk, "PNG 在 IEND 之后仍包含 IDAT。", header);
                }

                idat.Write(chunkData);
                sawIdat = true;
            }
            else if (string.Equals(type, "IEND", StringComparison.Ordinal))
            {
                if (chunkLength != 0)
                {
                    return Fail(PngValidationStatus.InvalidChunk, "PNG IEND 数据块长度必须为 0。", header);
                }

                sawIend = true;
            }
            else if (char.IsUpper(type[0]))
            {
                // 未知的关键块无法安全跳过；未知的辅助块（首字母小写）可以忽略。
                return Fail(PngValidationStatus.UnsupportedFormat, $"PNG 包含未知关键数据块 {type}。", header);
            }

            offset += 12 + chunkLength;

            if (sawIend)
            {
                break;
            }
        }

        if (!sawIhdr || header is null)
        {
            return Fail(PngValidationStatus.MissingImageData, "PNG 缺少 IHDR。", header);
        }

        if (!sawIdat)
        {
            return Fail(PngValidationStatus.MissingImageData, "PNG 缺少 IDAT 图像数据。", header);
        }

        if (!sawIend)
        {
            return Fail(PngValidationStatus.Truncated, "PNG 缺少 IEND 结束块。", header);
        }

        return Decode(header.Value, idat.ToArray());
    }

    public static PngValidationResult ValidateFile(string path, PngValidationLimits? limits = null)
    {
        limits ??= PngValidationLimits.Default;

        if (string.IsNullOrWhiteSpace(path))
        {
            return Fail(PngValidationStatus.FileNotFound, "PNG 文件路径为空。");
        }

        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Fail(PngValidationStatus.FileNotFound, "PNG 文件不存在。");
            }

            if (info.Length > limits.MaxFileBytes)
            {
                return Fail(
                    PngValidationStatus.TooLarge,
                    $"PNG 文件超过 {limits.MaxFileBytes} 字节上限（实际 {info.Length} 字节）。");
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail(PngValidationStatus.FileUnavailable, $"读取 PNG 文件失败：{exception.Message}");
        }

        return Validate(bytes, limits);
    }

    private static PngValidationResult ParseHeader(ReadOnlySpan<byte> data, PngValidationLimits limits)
    {
        var width = BinaryPrimitives.ReadUInt32BigEndian(data);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var bitDepth = data[8];
        var colorType = data[9];
        var compressionMethod = data[10];
        var filterMethod = data[11];
        var interlaceMethod = data[12];

        var header = new PngHeader(
            width > int.MaxValue ? 0 : (int)width,
            height > int.MaxValue ? 0 : (int)height,
            bitDepth,
            colorType,
            compressionMethod,
            filterMethod,
            interlaceMethod);

        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            return Fail(PngValidationStatus.InvalidHeader, "PNG 宽高必须为非零的正整数。", header);
        }

        if (compressionMethod != 0)
        {
            return Fail(PngValidationStatus.InvalidHeader, "PNG 压缩方法必须为 0。", header);
        }

        if (filterMethod != 0)
        {
            return Fail(PngValidationStatus.InvalidHeader, "PNG 滤波方法必须为 0。", header);
        }

        if (width > limits.MaxWidth || height > limits.MaxHeight)
        {
            return Fail(
                PngValidationStatus.DimensionTooLarge,
                $"PNG 尺寸 {width}x{height} 超出 {limits.MaxWidth}x{limits.MaxHeight} 上限。",
                header);
        }

        if ((long)width * height > limits.MaxPixelCount)
        {
            return Fail(
                PngValidationStatus.PixelCountTooLarge,
                $"PNG 像素总量 {width}x{height} 超过 {limits.MaxPixelCount} 上限。",
                header);
        }

        if (bitDepth != 8)
        {
            return Fail(
                PngValidationStatus.UnsupportedFormat,
                $"暂不支持 {bitDepth}-bit PNG；仅支持 8-bit 非隔行 PNG。",
                header);
        }

        if (colorType is not ((byte)PngColorType.Grayscale or (byte)PngColorType.Rgb or (byte)PngColorType.Rgba))
        {
            return Fail(
                PngValidationStatus.UnsupportedFormat,
                $"暂不支持 PNG color type {colorType}；仅支持 0（灰度）、2（RGB）、6（RGBA）。",
                header);
        }

        if (interlaceMethod != 0)
        {
            return Fail(
                PngValidationStatus.UnsupportedFormat,
                "暂不支持隔行扫描（interlaced）PNG。",
                header);
        }

        return new PngValidationResult
        {
            Status = PngValidationStatus.Valid,
            Width = header.Width,
            Height = header.Height,
        };
    }

    private static PngValidationResult Decode(PngHeader header, byte[] compressed)
    {
        var channels = header.ColorType switch
        {
            (byte)PngColorType.Grayscale => 1,
            (byte)PngColorType.Rgb => 3,
            (byte)PngColorType.Rgba => 4,
            _ => 0,
        };

        if (channels == 0)
        {
            return Fail(PngValidationStatus.UnsupportedFormat, "不支持的 PNG 颜色类型。", header);
        }

        var stride = checked(header.Width * channels);
        var expectedRawLength = checked((stride + 1) * header.Height);
        var raw = new byte[expectedRawLength];

        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var read = 0;
            while (read < raw.Length)
            {
                var count = zlib.Read(raw, read, raw.Length - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            if (read != raw.Length)
            {
                return Fail(PngValidationStatus.CorruptData, "PNG 解压后的图像数据长度不足。", header);
            }

            // 多读一个字节，确认压缩流没有声明之外的额外数据。
            if (zlib.ReadByte() != -1)
            {
                return Fail(PngValidationStatus.CorruptData, "PNG 解压后的图像数据超出预期长度。", header);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return Fail(PngValidationStatus.CorruptData, $"PNG zlib 数据损坏：{exception.Message}", header);
        }

        var rgba = new byte[checked(header.Width * header.Height * 4)];
        var rowStart = 0;
        var previousRowStart = -1;

        for (var y = 0; y < header.Height; y++)
        {
            var filter = raw[rowStart];
            if (filter > 4)
            {
                return Fail(PngValidationStatus.CorruptData, $"PNG 行 {y} 使用了未知滤波类型 {filter}。", header);
            }

            for (var x = 0; x < stride; x++)
            {
                var current = raw[rowStart + 1 + x];
                var left = x >= channels ? raw[rowStart + 1 + x - channels] : 0;
                var up = previousRowStart >= 0 ? raw[previousRowStart + 1 + x] : 0;
                var upLeft = previousRowStart >= 0 && x >= channels
                    ? raw[previousRowStart + 1 + x - channels]
                    : 0;

                var value = filter switch
                {
                    0 => current,
                    1 => (current + left) & 0xFF,
                    2 => (current + up) & 0xFF,
                    3 => (current + ((left + up) >> 1)) & 0xFF,
                    4 => (current + Paeth(left, up, upLeft)) & 0xFF,
                    _ => -1,
                };

                if (value < 0)
                {
                    return Fail(PngValidationStatus.CorruptData, $"PNG 行 {y} 的滤波数据无效。", header);
                }

                raw[rowStart + 1 + x] = (byte)value;
            }

            var destination = y * header.Width * 4;
            for (var x = 0; x < header.Width; x++)
            {
                var source = rowStart + 1 + (x * channels);
                switch (header.ColorType)
                {
                    case (byte)PngColorType.Grayscale:
                        {
                            var gray = raw[source];
                            rgba[destination++] = gray;
                            rgba[destination++] = gray;
                            rgba[destination++] = gray;
                            rgba[destination++] = 255;
                            break;
                        }

                    case (byte)PngColorType.Rgb:
                        {
                            rgba[destination++] = raw[source];
                            rgba[destination++] = raw[source + 1];
                            rgba[destination++] = raw[source + 2];
                            rgba[destination++] = 255;
                            break;
                        }

                    case (byte)PngColorType.Rgba:
                        {
                            rgba[destination++] = raw[source];
                            rgba[destination++] = raw[source + 1];
                            rgba[destination++] = raw[source + 2];
                            rgba[destination++] = raw[source + 3];
                            break;
                        }
                }
            }

            previousRowStart = rowStart;
            rowStart += stride + 1;
        }

        return new PngValidationResult
        {
            Status = PngValidationStatus.Valid,
            Width = header.Width,
            Height = header.Height,
            Image = new PngImage
            {
                Width = header.Width,
                Height = header.Height,
                ColorType = (PngColorType)header.ColorType,
                Rgba = rgba,
            },
        };
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static PngValidationResult Fail(
        PngValidationStatus status,
        string message,
        PngHeader? header = null) =>
        new()
        {
            Status = status,
            Message = message,
            Width = header?.Width ?? 0,
            Height = header?.Height ?? 0,
        };

    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < table.Length; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private readonly record struct PngHeader(
        int Width,
        int Height,
        byte BitDepth,
        byte ColorType,
        byte CompressionMethod,
        byte FilterMethod,
        byte InterlaceMethod);
}
