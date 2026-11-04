// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Ecl.Core;
using Ecl.Minecraft.Engine.Identity;
using Ecl.Minecraft.Instances;

namespace Ecl.Minecraft.Engine.Files;

public sealed class InstancePathException(string message) : Exception(message);

/// <summary>文件类型识别结果。扩展名只是提示，签名与内容探测优先（ADR 0042 §9）。</summary>
public sealed record FileTypeInfo(string Id, string Category, bool IsText)
{
    public static FileTypeInfo Unknown { get; } = new("application/octet-stream", "binary", false);
}

/// <summary>
/// 文件类型注册表。稳定的语义 ID 供 UI、处理器注册表和 Addition 授权共同使用，
/// 不允许把扩展名直接绑定到某个处理器。
/// </summary>
public static class FileTypeRegistry
{
    private static readonly Dictionary<string, FileTypeInfo> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = new("image/png", "image", false),
        [".jpg"] = new("image/jpeg", "image", false),
        [".jpeg"] = new("image/jpeg", "image", false),
        [".gif"] = new("image/gif", "image", false),
        [".webp"] = new("image/webp", "image", false),
        [".txt"] = new("text/plain", "text", true),
        [".log"] = new("text/plain", "text", true),
        [".md"] = new("text/markdown", "text", true),
        [".json"] = new("application/json", "text", true),
        [".json5"] = new("application/json", "text", true),
        [".mcmeta"] = new("application/json", "text", true),
        [".toml"] = new("application/toml", "text", true),
        [".yaml"] = new("application/yaml", "text", true),
        [".yml"] = new("application/yaml", "text", true),
        [".xml"] = new("application/xml", "text", true),
        [".properties"] = new("text/plain", "text", true),
        [".cfg"] = new("text/plain", "text", true),
        [".conf"] = new("text/plain", "text", true),
        [".ini"] = new("text/plain", "text", true),
        [".lang"] = new("text/plain", "text", true),
        [".csv"] = new("text/csv", "text", true),
        [".jar"] = new("application/java-archive", "archive", false),
        [".zip"] = new("application/zip", "archive", false),
        [".mrpack"] = new("application/zip", "archive", false),
        [".7z"] = new("application/x-7z-compressed", "archive", false),
        [".rar"] = new("application/vnd.rar", "archive", false),
        [".nbt"] = new("minecraft/nbt", "minecraft", false),
        [".mca"] = new("minecraft/region", "minecraft", false),
        [".mcr"] = new("minecraft/region", "minecraft", false),
        [".schem"] = new("minecraft/schematic", "minecraft", false),
        [".schematic"] = new("minecraft/schematic", "minecraft", false),
        [".litematic"] = new("minecraft/litematic", "minecraft", false),
        [".dat"] = new("application/octet-stream", "binary", false),
        [".dat_old"] = new("application/octet-stream", "binary", false),
    };

    public static FileTypeInfo Detect(string path, ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
        {
            return new FileTypeInfo("image/png", "image", false);
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return new FileTypeInfo("image/jpeg", "image", false);
        }

        if (header.Length >= 6 &&
            header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F')
        {
            return new FileTypeInfo("image/gif", "image", false);
        }

        var extension = Path.GetExtension(path);
        var hasZipSignature = header.Length >= 4 &&
                              header[0] == (byte)'P' && header[1] == (byte)'K' &&
                              header[2] is 0x03 or 0x05 or 0x07 && header[3] is 0x04 or 0x06 or 0x08;
        if (hasZipSignature)
        {
            return string.Equals(extension, ".jar", StringComparison.OrdinalIgnoreCase)
                ? new FileTypeInfo("application/java-archive", "archive", false)
                : new FileTypeInfo("application/zip", "archive", false);
        }

        if (header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B)
        {
            // .nbt 使用 gzip 容器；其它 gzip 仍按通用归档显示。
            return string.Equals(extension, ".nbt", StringComparison.OrdinalIgnoreCase)
                ? new FileTypeInfo("minecraft/nbt", "minecraft", false)
                : new FileTypeInfo("application/gzip", "archive", false);
        }

        if (header.Length >= 6 &&
            header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC &&
            header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C)
        {
            return new FileTypeInfo("application/x-7z-compressed", "archive", false);
        }

        if (header.Length >= 4 &&
            header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21)
        {
            return new FileTypeInfo("application/vnd.rar", "archive", false);
        }

        return ByExtension.TryGetValue(extension, out var type) ? type : FileTypeInfo.Unknown;
    }

    public static FileTypeInfo Detect(string path, ReadOnlySpan<byte> header, bool isDirectory) =>
        isDirectory ? new FileTypeInfo("inode/directory", "directory", false) : Detect(path, header);

    public static bool IsKnownTextExtension(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var type) && type.IsText;
}

public enum InstanceFileSort
{
    NameAscending,
    NameDescending,
    ModifiedAscending,
    ModifiedDescending,
    SizeAscending,
    SizeDescending,
}

public sealed record InstanceFileEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long? Size,
    DateTimeOffset ModifiedAt,
    string TypeId,
    bool IsLink,
    string? LinkTarget);

public enum DirectoryStatisticsStatus
{
    Complete,
    Partial,
    Failed,
    NotComputed,
}

public sealed record DirectoryStatistics(
    string RelativePath,
    bool Recursive,
    int FileCount,
    int DirectoryCount,
    long TotalBytes,
    int InaccessibleCount,
    DirectoryStatisticsStatus Status,
    string? Message = null);

public sealed record InstanceFilePreview(
    string RelativePath,
    string TypeId,
    string? Text,
    string EncodingName,
    long TotalBytes,
    int BytesRead,
    bool Truncated,
    bool IsBinary,
    string? Warning = null);

public sealed record InstanceFileCopyResult(
    string SourceRelativePath,
    string DestinationPath,
    long BytesCopied,
    bool SourceWasChanging,
    string? Warning = null);

/// <summary>
/// 以实例根目录为强制边界的文件访问（ADR 0042 §2/§12）。所有 UI 与 Addition 访问都必须经过这里，
/// 不允许它们自行拼接路径。
/// </summary>
public sealed class InstanceFileWorkspace
{
    private const int MaxPreviewBytes = 1024 * 1024;
    private const int MaxSignatureBytes = 8;

    public InstanceFileWorkspace(string rootPath)
    {
        RootPath = InstancePathIdentity.Normalize(rootPath);
    }

    public string RootPath { get; }

    /// <summary>把 UI/API 提供的相对路径规范化。拒绝绝对路径、盘符、UNC、设备路径、..、ADS 和空段。</summary>
    public string NormalizeRelative(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return string.Empty;
        }

        var candidate = relative.Trim().Replace('\\', '/');
        if (candidate is "." or "./")
        {
            return string.Empty;
        }

        if (candidate.StartsWith('/') ||
            candidate.StartsWith("//", StringComparison.Ordinal) ||
            candidate.Contains(':', StringComparison.Ordinal) ||
            candidate.Contains('\0'))
        {
            throw new InstancePathException("路径必须是实例根目录内的相对路径。");
        }

        var parts = candidate.Split('/');
        var normalized = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                throw new InstancePathException("路径包含空段或重复分隔符。");
            }

            if (part is ".")
            {
                throw new InstancePathException("路径包含歧义段（.）。");
            }

            if (part is "..")
            {
                throw new InstancePathException("路径穿越被拒绝。");
            }

            if (part.EndsWith('.') || part.EndsWith(' '))
            {
                throw new InstancePathException("路径段以点或空格结尾，在 Windows 上存在歧义。");
            }

            if (IsReservedDeviceName(part))
            {
                throw new InstancePathException("路径包含 Windows 保留设备名。");
            }

            foreach (var character in part)
            {
                if (character < 0x20)
                {
                    throw new InstancePathException("路径包含控制字符。");
                }
            }

            normalized.Add(part);
        }

        return string.Join(Path.DirectorySeparatorChar, normalized);
    }

    public string Resolve(string? relative)
    {
        var normalized = NormalizeRelative(relative);
        var full = Path.GetFullPath(
            normalized.Length == 0 ? RootPath : Path.Combine(RootPath, normalized));
        if (!InstancePathIdentity.IsContained(RootPath, full))
        {
            throw new InstancePathException("路径逃逸出实例根目录。");
        }

        EnsureNoReparseEscape(full);
        return full;
    }

    public InstanceFileEntry? TryGetEntry(string? relative)
    {
        var full = Resolve(relative);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return null;
        }

        return CreateEntry(full);
    }

    public IReadOnlyList<InstanceFileEntry> List(
        string? relative = null,
        InstanceFileSort sort = InstanceFileSort.NameAscending)
    {
        var full = Resolve(relative);
        if (!Directory.Exists(full))
        {
            throw new InstancePathException("目标不是目录。");
        }

        var entries = new List<InstanceFileEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(full))
        {
            try
            {
                entries.Add(CreateEntry(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 单个不可访问的条目不应让整个目录浏览失败；调用方可通过统计看到缺口。
            }
        }

        IOrderedEnumerable<InstanceFileEntry> ordered = sort switch
        {
            InstanceFileSort.NameDescending => entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            InstanceFileSort.ModifiedAscending => entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.ModifiedAt)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            InstanceFileSort.ModifiedDescending => entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenByDescending(entry => entry.ModifiedAt)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            InstanceFileSort.SizeAscending => entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Size ?? 0)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            InstanceFileSort.SizeDescending => entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenByDescending(entry => entry.Size ?? 0)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            _ => entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        };

        return ordered.ToArray();
    }

    /// <summary>
    /// 目录统计。递归结果与直接子项分开；不可访问/被截断时返回 Partial，绝不把未知当成 0。
    /// </summary>
    public DirectoryStatistics GetStatistics(
        string? relative = null,
        bool recursive = true,
        int maxEntries = 200_000,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRelative(relative);
        var full = Resolve(relative);
        if (!Directory.Exists(full))
        {
            return new DirectoryStatistics(
                normalized,
                recursive,
                0,
                0,
                0,
                0,
                DirectoryStatisticsStatus.Failed,
                "目标不是目录或无法访问。");
        }

        var files = 0;
        var directories = 0;
        long bytes = 0;
        var inaccessible = 0;
        var truncated = false;

        if (!recursive)
        {
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(full))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(path);
                        if (Directory.Exists(path))
                        {
                            directories++;
                        }
                        else
                        {
                            files++;
                            bytes += info.Length;
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        inaccessible++;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new DirectoryStatistics(
                    normalized,
                    recursive,
                    0,
                    0,
                    0,
                    inaccessible,
                    DirectoryStatisticsStatus.Failed,
                    exception.Message);
            }

            return new DirectoryStatistics(
                normalized,
                recursive,
                files,
                directories,
                bytes,
                inaccessible,
                inaccessible == 0 ? DirectoryStatisticsStatus.Complete : DirectoryStatisticsStatus.Partial,
                inaccessible == 0 ? null : "部分子项不可访问。");
        }

        var pending = new Stack<string>();
        pending.Push(full);
        var visited = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visited > maxEntries)
                    {
                        truncated = true;
                        pending.Clear();
                        break;
                    }

                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            // 统计不跟随链接，避免越界与循环。
                            continue;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            directories++;
                            pending.Push(path);
                        }
                        else
                        {
                            files++;
                            bytes += new FileInfo(path).Length;
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        inaccessible++;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                inaccessible++;
            }
        }

        var status = inaccessible > 0 || truncated
            ? DirectoryStatisticsStatus.Partial
            : DirectoryStatisticsStatus.Complete;
        return new DirectoryStatistics(
            normalized,
            Recursive: true,
            files,
            directories,
            bytes,
            inaccessible,
            status,
            truncated
                ? $"统计超过 {maxEntries} 个条目，结果不完整。"
                : inaccessible > 0 ? "部分子项不可访问，结果不完整。" : null);
    }

    public async Task<Result<InstanceFilePreview>> PreviewTextAsync(
        string relative,
        int maxBytes = MaxPreviewBytes,
        CancellationToken cancellationToken = default)
    {
        var full = Resolve(relative);
        if (!File.Exists(full))
        {
            return Result<InstanceFilePreview>.Fail("files.not_found", "文件不存在。");
        }

        try
        {
            var info = new FileInfo(full);
            maxBytes = Math.Clamp(maxBytes, 1, MaxPreviewBytes);
            var totalBytes = info.Length;
            var readLength = (int)Math.Min(totalBytes, maxBytes);
            var buffer = new byte[readLength];
            var bytesRead = 0;

            await using (var stream = new FileStream(
                             full,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             bufferSize: 4096,
                             useAsync: true))
            {
                while (bytesRead < readLength)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(bytesRead, readLength - bytesRead), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    bytesRead += read;
                }
            }

            var header = buffer.AsSpan(0, Math.Min(bytesRead, MaxSignatureBytes));
            var type = FileTypeRegistry.Detect(full, header);
            var binary = buffer.AsSpan(0, bytesRead).Contains((byte)0) ||
                         (!type.IsText && type.Category is "image" or "archive" or "minecraft");
            if (binary)
            {
                return Result<InstanceFilePreview>.Ok(new InstanceFilePreview(
                    NormalizeRelative(relative),
                    type.Id,
                    null,
                    "binary",
                    totalBytes,
                    bytesRead,
                    Truncated: bytesRead < totalBytes,
                    IsBinary: true,
                    Warning: "二进制文件不提供文本预览；请使用对应的处理器或导出。"));
            }

            var encoding = DetectEncoding(buffer.AsSpan(0, bytesRead), out var preamble);
            var text = encoding.GetString(buffer, preamble, Math.Max(0, bytesRead - preamble));
            if (text.Contains('\uFFFD'))
            {
                return Result<InstanceFilePreview>.Ok(new InstanceFilePreview(
                    NormalizeRelative(relative),
                    type.Id,
                    null,
                    "unknown",
                    totalBytes,
                    bytesRead,
                    Truncated: bytesRead < totalBytes,
                    IsBinary: true,
                    Warning: "文件不是有效的文本编码，无法安全预览。"));
            }

            return Result<InstanceFilePreview>.Ok(new InstanceFilePreview(
                NormalizeRelative(relative),
                type.Id,
                text,
                encoding.WebName,
                totalBytes,
                bytesRead,
                Truncated: bytesRead < totalBytes,
                IsBinary: false,
                Warning: bytesRead < totalBytes ? "预览已截断。" : null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<InstanceFilePreview>.Fail(
                "files.read_failed",
                "读取文件失败；文件可能被占用或没有权限。",
                exception.Message);
        }
    }

    public async Task<Result<byte[]>> ReadBytesAsync(
        string relative,
        int maxBytes = MaxPreviewBytes,
        CancellationToken cancellationToken = default)
    {
        var full = Resolve(relative);
        if (!File.Exists(full))
        {
            return Result<byte[]>.Fail("files.not_found", "文件不存在。");
        }

        try
        {
            maxBytes = Math.Clamp(maxBytes, 1, MaxPreviewBytes);
            var info = new FileInfo(full);
            var length = (int)Math.Min(info.Length, maxBytes);
            var buffer = new byte[length];
            var read = 0;
            await using var stream = new FileStream(
                full,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            while (read < length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            return read == buffer.Length ? Result<byte[]>.Ok(buffer) : Result<byte[]>.Ok(buffer[..read]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<byte[]>.Fail("files.read_failed", "读取文件失败。", exception.Message);
        }
    }

    /// <summary>
    /// 导出到用户明确选择的目标。目标不受实例边界限制，但只用于本次复制；
    /// 不向调用方授予目标目录的持续浏览权限。
    /// </summary>
    public async Task<Result<InstanceFileCopyResult>> ExportAsync(
        string relative,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var full = Resolve(relative);
        if (!File.Exists(full))
        {
            return Result<InstanceFileCopyResult>.Fail("files.not_found", "源文件不存在。");
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return Result<InstanceFileCopyResult>.Fail("files.invalid_destination", "目标路径不能为空。");
        }

        string destination;
        try
        {
            destination = Path.GetFullPath(destinationPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<InstanceFileCopyResult>.Fail("files.invalid_destination", "目标路径不合法。", exception.Message);
        }

        if (Directory.Exists(destination))
        {
            return Result<InstanceFileCopyResult>.Fail("files.invalid_destination", "目标路径是目录，必须包含文件名。");
        }

        if (File.Exists(destination) && !overwrite)
        {
            return Result<InstanceFileCopyResult>.Fail("files.destination_exists", "目标文件已存在。");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var sourceInfoBefore = new FileInfo(full);
            var lengthBefore = sourceInfoBefore.Length;
            var modifiedBefore = sourceInfoBefore.LastWriteTimeUtc;

            await using (var source = new FileStream(
                             full,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             bufferSize: 81920,
                             useAsync: true))
            await using (var target = new FileStream(
                             destination,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var sourceInfoAfter = new FileInfo(full);
            var changing = sourceInfoAfter.Length != lengthBefore ||
                           sourceInfoAfter.LastWriteTimeUtc != modifiedBefore;
            return Result<InstanceFileCopyResult>.Ok(new InstanceFileCopyResult(
                NormalizeRelative(relative),
                destination,
                new FileInfo(destination).Length,
                changing,
                changing ? "源文件在复制期间仍在变化；目标可能不是一致快照。" : null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<InstanceFileCopyResult>.Fail(
                "files.copy_failed",
                "复制文件失败。",
                exception.Message);
        }
    }

    private InstanceFileEntry CreateEntry(string fullPath)
    {
        var normalized = InstancePathIdentity.Normalize(fullPath);
        var relative = Path.GetRelativePath(RootPath, normalized);
        var attributes = File.GetAttributes(normalized);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
        long? size = isDirectory ? null : new FileInfo(normalized).Length;
        var modified = File.GetLastWriteTimeUtc(normalized);

        string? linkTarget = null;
        if (isLink)
        {
            try
            {
                var info = isDirectory
                    ? (FileSystemInfo)new DirectoryInfo(normalized)
                    : new FileInfo(normalized);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    linkTarget = InstancePathIdentity.IsContained(RootPath, target.FullName)
                        ? Path.GetRelativePath(RootPath, target.FullName)
                        : null;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                linkTarget = null;
            }
        }

        var type = FileTypeInfo.Unknown;
        if (!isDirectory)
        {
            try
            {
                Span<byte> header = stackalloc byte[MaxSignatureBytes];
                using var stream = new FileStream(
                    normalized,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var read = stream.Read(header);
                type = FileTypeRegistry.Detect(normalized, header[..read]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                type = FileTypeRegistry.Detect(normalized, ReadOnlySpan<byte>.Empty);
            }
        }
        else
        {
            type = new FileTypeInfo("inode/directory", "directory", false);
        }

        return new InstanceFileEntry(
            Path.GetFileName(normalized),
            relative,
            isDirectory,
            size,
            modified,
            type.Id,
            isLink,
            linkTarget);
    }

    /// <summary>确认路径链上的重解析点都落在实例根目录内；无法确认时拒绝。</summary>
    private void EnsureNoReparseEscape(string fullPath)
    {
        var relative = Path.GetRelativePath(RootPath, fullPath);
        if (relative == ".")
        {
            return;
        }

        var current = RootPath;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // 后续段尚不存在：已检查的父链仍然有效。
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            FileSystemInfo? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InstancePathException("无法解析路径中的链接，拒绝访问。");
            }

            if (target is null || !InstancePathIdentity.IsContained(RootPath, target.FullName))
            {
                throw new InstancePathException("路径中的链接指向实例根目录之外，拒绝访问。");
            }
        }
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var name = segment;
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            name = name[..dot];
        }

        return name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3"
            or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1" or "LPT2" or "LPT3"
            or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    }

    private static Encoding DetectEncoding(ReadOnlySpan<byte> buffer, out int preamble)
    {
        if (buffer.Length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            preamble = 3;
            return new UTF8Encoding(false, true);
        }

        if (buffer.Length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            preamble = 2;
            return Encoding.Unicode;
        }

        if (buffer.Length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            preamble = 2;
            return Encoding.BigEndianUnicode;
        }

        preamble = 0;
        return new UTF8Encoding(false, throwOnInvalidBytes: false);
    }
}

/// <summary>按实例打开受控文件工作区；不存在或已删除的实例不会被静默创建。</summary>
public sealed class InstanceFileAccessService(
    IInstanceService instances,
    IAppPaths paths)
{
    public async Task<Result<InstanceFileWorkspace>> OpenAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceFileWorkspace>.Fail("instance.not_found", "实例不存在。");
        }

        var root = InstancePaths.GetGameDirectory(paths, instance);
        if (!Directory.Exists(root))
        {
            return Result<InstanceFileWorkspace>.Fail(
                "files.root_missing",
                "实例游戏目录不存在或当前不可访问。");
        }

        return Result<InstanceFileWorkspace>.Ok(new InstanceFileWorkspace(root));
    }
}
