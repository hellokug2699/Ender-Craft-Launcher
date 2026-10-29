// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ecl.Core;
using IOPath = System.IO.Path;

namespace Ecl.Minecraft.Mods;

/// <summary>
/// Minecraft <c>options.txt</c> 的读写封装。
/// <para>
/// 文件格式是 Java Properties 风格的 <c>key:value</c> 行。本类只修改调用方显式请求的键，
/// 保留其它键、注释、空行、原始换行风格与 BOM；写入时先写临时文件、再备份原文件，
/// 最后原子替换，避免异常中断把用户配置写坏。
/// </para>
/// </summary>
public sealed class GameOptionsFile
{
    private const string DefaultNewLine = "\r\n";

    private static readonly JsonSerializerOptions ListJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<OptionLine> _lines;
    private readonly string _newLine;
    private readonly bool _hasByteOrderMark;
    private bool _dirty;

    private GameOptionsFile(
        string path,
        bool exists,
        List<OptionLine> lines,
        string newLine,
        bool hasByteOrderMark)
    {
        Path = path;
        Exists = exists;
        _lines = lines;
        _newLine = newLine;
        _hasByteOrderMark = hasByteOrderMark;
    }

    /// <summary>options.txt 的完整路径。</summary>
    public string Path { get; }

    /// <summary>读取时文件是否存在。不存在时返回空文档，写入会新建文件。</summary>
    public bool Exists { get; }

    /// <summary>自读取后是否有尚未保存的修改。</summary>
    public bool IsDirty => _dirty;

    /// <summary>备份文件路径：与 options.txt 同目录、后缀 <c>.bak</c>。</summary>
    public string BackupPath => Path + ".bak";

    /// <summary>
    /// 读取 options.txt。文件不存在视为空文档；文件存在但无法读取时返回失败，
    /// 调用方不得在失败时覆盖原文件。
    /// </summary>
    public static Result<GameOptionsFile> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<GameOptionsFile>.Fail("options.invalid_path", "options.txt 路径为空。");
        }

        if (!File.Exists(path))
        {
            return Result<GameOptionsFile>.Ok(
                new GameOptionsFile(IOPath.GetFullPath(path), false, [], DefaultNewLine, false));
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[stream.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var chunk = stream.Read(bytes, read, bytes.Length - read);
                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<GameOptionsFile>.Fail(
                "options.unreadable",
                $"无法读取 options.txt：{exception.Message}",
                path);
        }

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false, false).GetString(
            bytes,
            hasBom ? 3 : 0,
            bytes.Length - (hasBom ? 3 : 0));

        return Result<GameOptionsFile>.Ok(
            new GameOptionsFile(
                IOPath.GetFullPath(path),
                true,
                ParseLines(text),
                DetectNewLine(text),
                hasBom));
    }

    /// <summary>读取键的原始值（最后一次出现的定义生效）。键不存在时返回 <c>null</c>。</summary>
    public string? GetValue(string key)
    {
        for (var index = _lines.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_lines[index].Key, key, StringComparison.Ordinal))
            {
                return _lines[index].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// 读取 JSON 字符串数组值，例如 <c>resourcePacks:["vanilla","file/a.zip"]</c>。
    /// 键不存在或值为空时返回空列表；值无法解析时返回失败，调用方不得覆盖原值。
    /// </summary>
    public Result<IReadOnlyList<string>> GetStringList(string key)
    {
        var raw = GetValue(key);
        if (raw is null)
        {
            return Result<IReadOnlyList<string>>.Ok([]);
        }

        var value = raw.Trim();
        if (value.Length == 0)
        {
            return Result<IReadOnlyList<string>>.Ok([]);
        }

        if (!value.StartsWith('['))
        {
            // 兼容极旧版本的单个裸值写法。
            return Result<IReadOnlyList<string>>.Ok([Unquote(value)]);
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return InvalidList(key, "值不是 JSON 数组。");
            }

            var items = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return InvalidList(key, "数组包含非字符串元素。");
                }

                items.Add(item.GetString() ?? string.Empty);
            }

            return Result<IReadOnlyList<string>>.Ok(items);
        }
        catch (JsonException exception)
        {
            return InvalidList(key, exception.Message);
        }
    }

    /// <summary>读取单个字符串值（去掉可能的成对引号）。键不存在时返回 <c>null</c>。</summary>
    public string? GetString(string key) => GetValue(key) is { } raw ? Unquote(raw.Trim()) : null;

    /// <summary>写入 JSON 字符串数组值。</summary>
    public void SetStringList(string key, IReadOnlyList<string> values) =>
        SetValue(key, JsonSerializer.Serialize(values, ListJsonOptions));

    /// <summary>写入单个字符串值。</summary>
    public void SetString(string key, string value) => SetValue(key, value);

    /// <summary>写入或替换键值；键已存在时更新最后一次出现的行。</summary>
    public void SetValue(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        for (var index = _lines.Count - 1; index >= 0; index--)
        {
            var line = _lines[index];
            if (!string.Equals(line.Key, key, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(line.Value, value, StringComparison.Ordinal))
            {
                return;
            }

            line.Value = value;
            line.Changed = true;
            _dirty = true;
            return;
        }

        _lines.Add(new OptionLine
        {
            Key = key,
            Value = value,
            Changed = true,
        });
        _dirty = true;
    }

    /// <summary>删除键的所有出现（包括重复定义）。键不存在时无操作。</summary>
    public void Remove(string key)
    {
        var removed = _lines.RemoveAll(line => string.Equals(line.Key, key, StringComparison.Ordinal));
        if (removed > 0)
        {
            _dirty = true;
        }
    }

    /// <summary>
    /// 保存到 <see cref="Path"/>：写入同目录临时文件，覆盖式备份原文件到 <see cref="BackupPath"/>，
    /// 再原子替换。没有任何修改且文件已存在时不写盘。
    /// </summary>
    public Result Save()
    {
        if (!_dirty && Exists)
        {
            return Result.Ok();
        }

        var directory = IOPath.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return WriteFailed(exception);
            }
        }

        var builder = new StringBuilder();
        foreach (var line in _lines)
        {
            builder.Append(line.Changed ? $"{line.Key}:{line.Value}" : line.RawText);
            builder.Append(_newLine);
        }

        var encoding = new UTF8Encoding(_hasByteOrderMark);
        var temporaryPath = $"{Path}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(temporaryPath, builder.ToString(), encoding);

            if (Exists)
            {
                File.Copy(Path, BackupPath, overwrite: true);
            }

            File.Move(temporaryPath, Path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            return WriteFailed(exception);
        }

        return Result.Ok();
    }

    private static Result<IReadOnlyList<string>> InvalidList(string key, string detail) =>
        Result<IReadOnlyList<string>>.Fail(
            "options.invalid_value",
            $"无法解析 options.txt 中的 {key}：{detail}",
            key);

    private Result WriteFailed(Exception exception) =>
        Result.Fail("options.write_failed", $"无法写入 options.txt：{exception.Message}", Path);

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
            // 临时文件清理失败不应掩盖原始错误。
        }
    }

    private static string DetectNewLine(string text)
    {
        var index = text.IndexOf('\n');
        if (index > 0 && text[index - 1] == '\r')
        {
            return "\r\n";
        }

        if (index >= 0)
        {
            return "\n";
        }

        return text.Contains('\r') ? "\r" : DefaultNewLine;
    }

    private static List<OptionLine> ParseLines(string text)
    {
        var lines = new List<OptionLine>();
        if (text.Length == 0)
        {
            return lines;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var segments = normalized.Split('\n');
        var count = segments.Length;
        if (count > 0 && segments[^1].Length == 0)
        {
            count--;
        }

        for (var index = 0; index < count; index++)
        {
            lines.Add(ParseLine(segments[index]));
        }

        return lines;
    }

    private static OptionLine ParseLine(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed[0] is '#' or '!')
        {
            return new OptionLine { RawText = text };
        }

        var separator = text.IndexOf(':');
        if (separator < 0)
        {
            separator = text.IndexOf('=');
        }

        if (separator <= 0)
        {
            // 无法识别的行原样保留，不做猜测。
            return new OptionLine { RawText = text };
        }

        return new OptionLine
        {
            Key = text[..separator].Trim(),
            Value = text[(separator + 1)..],
            RawText = text,
        };
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;

    private sealed class OptionLine
    {
        public string? Key { get; init; }

        public string? Value { get; set; }

        public string RawText { get; init; } = string.Empty;

        public bool Changed { get; set; }
    }
}
