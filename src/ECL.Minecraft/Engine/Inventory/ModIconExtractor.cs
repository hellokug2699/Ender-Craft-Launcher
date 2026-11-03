// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Ecl.Minecraft.Mods;

namespace Ecl.Minecraft.Engine.Inventory;

public sealed record ModIconResult(
    string JarPath,
    string? CachedPath,
    string? EntryPath,
    string? Source,
    int Width,
    int Height,
    string? Error)
{
    public bool IsSuccess => CachedPath is not null;
}

/// <summary>
/// 从模组 JAR 中按 Loader 元数据声明提取图标（ADR 0042 §18）。只读取目标条目，
/// 不执行模组代码、不完整解压；任何异常都隔离为该图标的失败，不影响其它扫描。
/// </summary>
public static class ModIconExtractor
{
    private const int MaxMetadataBytes = 1024 * 1024;
    private const int MaxIconBytes = 512 * 1024;
    private const int MaxDimension = 4096;
    private const long MaxPixels = 16_000_000;
    private const int MaxEntriesToInspect = 20_000;

    private static readonly string[] FallbackNames = ["icon.png", "logo.png"];

    public static ModIconResult Extract(string jarPath, string cacheDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = ZipFile.OpenRead(jarPath);
            if (archive.Entries.Count > MaxEntriesToInspect)
            {
                return Failure(jarPath, "JAR 条目过多，拒绝图标提取。");
            }

            var declared = FindDeclaredIcon(archive);
            var candidates = new List<(string Path, string Source)>();
            if (!string.IsNullOrWhiteSpace(declared.Path))
            {
                candidates.Add((declared.Path!, declared.Source!));
            }

            foreach (var fallback in FallbackNames)
            {
                candidates.Add((fallback, "fallback.convention"));
            }

            foreach (var (entryPath, source) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = FindEntry(archive, entryPath);
                if (entry is null || entry.Length is <= 0 or > MaxIconBytes)
                {
                    continue;
                }

                using var stream = entry.Open();
                using var memory = new MemoryStream((int)entry.Length);
                stream.CopyTo(memory);
                var bytes = memory.ToArray();
                if (!TryReadDimensions(bytes, out var width, out var height, out var extension, out var error))
                {
                    if (source.StartsWith("fabric", StringComparison.Ordinal) ||
                        source.StartsWith("forge", StringComparison.Ordinal))
                    {
                        // 声明的图标损坏时继续尝试约定文件名。
                        continue;
                    }

                    return Failure(jarPath, error);
                }

                if (width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
                {
                    if (source.StartsWith("fallback", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return Failure(jarPath, $"图标尺寸超限：{width}×{height}。");
                }

                var jarHash = ComputeFileHash(jarPath);
                var contentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var directory = Path.Combine(cacheDirectory, "instance-engine", "icons");
                Directory.CreateDirectory(directory);
                var cachedPath = Path.Combine(
                    directory,
                    $"{jarHash[..16]}-{contentHash[..12]}{extension}");
                if (!File.Exists(cachedPath))
                {
                    var temporary = $"{cachedPath}.{Guid.NewGuid():N}.tmp";
                    File.WriteAllBytes(temporary, bytes);
                    File.Move(temporary, cachedPath, overwrite: true);
                }

                return new ModIconResult(jarPath, cachedPath, entry.FullName, source, width, height, null);
            }

            return Failure(jarPath, "未找到可安全识别的模组图标。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or JsonException)
        {
            return Failure(jarPath, $"图标提取失败：{exception.Message}");
        }
    }

    private static (string? Path, string? Source) FindDeclaredIcon(ZipArchive archive)
    {
        var fabric = FindEntry(archive, "fabric.mod.json");
        if (fabric is not null && fabric.Length <= MaxMetadataBytes)
        {
            using var stream = fabric.Open();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("icon", out var icon))
            {
                if (icon.ValueKind == JsonValueKind.String)
                {
                    return (NormalizeEntryPath(icon.GetString()), "fabric.mod.json.icon");
                }

                if (icon.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "128", "64", "32", "256", "512" })
                    {
                        if (icon.TryGetProperty(key, out var sized) &&
                            sized.ValueKind == JsonValueKind.String)
                        {
                            return (NormalizeEntryPath(sized.GetString()), $"fabric.mod.json.icon[{key}]");
                        }
                    }

                    foreach (var property in icon.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            return (NormalizeEntryPath(property.Value.GetString()), $"fabric.mod.json.icon[{property.Name}]");
                        }
                    }
                }
            }
        }

        foreach (var metadataName in new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mods.toml" })
        {
            var toml = FindEntry(archive, metadataName);
            if (toml is null || toml.Length > MaxMetadataBytes)
            {
                continue;
            }

            using var stream = toml.Open();
            using var reader = new StreamReader(stream);
            var tables = TomlLite.Parse(reader.ReadToEnd());
            var mod = tables.FirstOrDefault(table =>
                string.Equals(table.Name, "mods", StringComparison.OrdinalIgnoreCase));
            if (mod is not null)
            {
                if (mod.Values.TryGetValue("logoFile", out var logo) && !string.IsNullOrWhiteSpace(logo))
                {
                    return (NormalizeEntryPath(logo), $"{metadataName}.logoFile");
                }

                if (mod.Values.TryGetValue("logoBlur", out _))
                {
                    // logoBlur 与图标无关，不触发回退。
                }
            }
        }

        return (null, null);
    }

    private static string? NormalizeEntryPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim().Replace('\\', '/');
        if (candidate.StartsWith('/') ||
            candidate.Contains(':') ||
            candidate.Contains('\0'))
        {
            return null;
        }

        var parts = candidate.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            return null;
        }

        return string.Join('/', parts);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path) =>
        archive.Entries.FirstOrDefault(entry =>
            !entry.FullName.EndsWith('/') &&
            string.Equals(entry.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));

    private static bool TryReadDimensions(
        byte[] bytes,
        out int width,
        out int height,
        out string extension,
        out string error)
    {
        width = 0;
        height = 0;
        extension = ".png";
        error = string.Empty;

        if (bytes.Length >= 24 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            extension = ".png";
            return width > 0 && height > 0;
        }

        if (bytes.Length >= 10 &&
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
        {
            width = bytes[6] | (bytes[7] << 8);
            height = bytes[8] | (bytes[9] << 8);
            extension = ".gif";
            return width > 0 && height > 0;
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var index = 2;
            while (index + 9 < bytes.Length)
            {
                if (bytes[index] != 0xFF)
                {
                    index++;
                    continue;
                }

                var marker = bytes[index + 1];
                index += 2;
                if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7)
                {
                    continue;
                }

                if (index + 2 > bytes.Length)
                {
                    break;
                }

                var length = (bytes[index] << 8) | bytes[index + 1];
                if (length < 2 || index + length > bytes.Length)
                {
                    break;
                }

                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
                    or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
                {
                    height = (bytes[index + 3] << 8) | bytes[index + 4];
                    width = (bytes[index + 5] << 8) | bytes[index + 6];
                    extension = ".jpg";
                    return width > 0 && height > 0;
                }

                index += length;
            }
        }

        error = "图标不是受支持的 PNG/JPEG/GIF 图像。";
        return false;
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static ModIconResult Failure(string jarPath, string error) =>
        new(jarPath, null, null, null, 0, 0, error);
}
