// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using Ecl.Core;

namespace Ecl.Diagnostics;

/// <summary>有界读取的结果。任何上限被触发都会在结果中明确标记，而不是静默截断。</summary>
public sealed record BoundedText(
    string Text,
    long BytesRead,
    int LinesRead,
    bool ByteLimitReached,
    bool LineLimitReached,
    bool LineLengthTruncated)
{
    /// <summary>是否发生了任一种截断。</summary>
    public bool Truncated => ByteLimitReached || LineLimitReached || LineLengthTruncated;
}

/// <summary>
/// 本地诊断文件的有界读取器：限制文件字节数、行数和单行长度，避免异常巨大的日志耗尽内存。
/// 读取失败返回 <see cref="Result{T}"/>，由调用方决定如何记录，不抛异常阻塞宿主。
/// </summary>
public static class BoundedTextReader
{
    public const int DefaultMaxBytes = 8 * 1024 * 1024;
    public const int DefaultMaxLines = 200_000;
    public const int DefaultMaxLineLength = 16_384;

    public static Result<BoundedText> ReadFile(
        string path,
        int maxBytes = DefaultMaxBytes,
        int maxLines = DefaultMaxLines,
        int maxLineLength = DefaultMaxLineLength)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<BoundedText>.Fail("diagnostics.path_empty", "诊断文件路径为空。");
        }

        if (maxBytes <= 0)
        {
            maxBytes = DefaultMaxBytes;
        }

        if (maxLines <= 0)
        {
            maxLines = DefaultMaxLines;
        }

        if (maxLineLength <= 0)
        {
            maxLineLength = DefaultMaxLineLength;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var length = stream.Length;
            var bytesToRead = (int)Math.Min(length, maxBytes);
            var buffer = new byte[bytesToRead];
            var read = 0;
            while (read < bytesToRead)
            {
                var chunk = stream.Read(buffer, read, bytesToRead - read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
                .GetString(buffer, 0, read);

            // 去掉 UTF-8 BOM；如果按字节截断刚好落在多字节字符中间，去掉末尾替换字符。
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text[1..];
            }

            var byteLimitReached = length > maxBytes;
            if (byteLimitReached && text.EndsWith('\uFFFD'))
            {
                text = text[..^1];
            }

            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var allLines = text.Split('\n');
            var lineLimitReached = allLines.Length > maxLines;
            var count = Math.Min(allLines.Length, maxLines);
            var lineLengthTruncated = false;

            for (var index = 0; index < count; index++)
            {
                var line = allLines[index];
                if (line.Length > maxLineLength)
                {
                    // 超长行（例如被拼接成一行的 JSON 或 NBT）只保留前缀，避免正则匹配失控。
                    allLines[index] = line[..maxLineLength];
                    lineLengthTruncated = true;
                }
            }

            var bounded = count == allLines.Length
                ? string.Join('\n', allLines)
                : string.Join('\n', allLines.AsSpan(0, count).ToArray());

            return Result<BoundedText>.Ok(new BoundedText(
                bounded,
                read,
                count,
                byteLimitReached,
                lineLimitReached,
                lineLengthTruncated));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException)
        {
            return Result<BoundedText>.Fail(
                "diagnostics.read_failed",
                "无法读取诊断文件。",
                $"{SafeFileName(path)}: {exception.Message}");
        }
    }

    /// <summary>只暴露文件名，避免错误信息和诊断报告泄露完整本机路径。</summary>
    public static string SafeFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>与 <see cref="SafeFileName"/> 同样的隐私约束，供解析器统一使用。</summary>
    internal static string NormalizeSourceName(string sourceName) => SafeFileName(sourceName);

    /// <summary>把任意文本规范化为 LF 行尾，行号与切片逻辑都依赖这一点。</summary>
    internal static string NormalizeNewlines(string text) =>
        string.IsNullOrEmpty(text)
            ? ""
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>仅用于诊断消息的固定区域性格式化，避免依赖宿主语言。</summary>
    internal static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
