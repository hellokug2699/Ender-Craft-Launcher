// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;

namespace Ecl.Minecraft.Engine.Snapshots;

/// <summary>
/// 本地内容寻址存储。对象按 SHA-256 去重，写入使用临时文件 + 原子移动；
/// 相同哈希但长度不一致视为损坏，绝不覆盖为“可用”。
/// </summary>
public sealed class SnapshotObjectStore(string root)
{
    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public string GetObjectPath(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("对象哈希必须是 64 位十六进制。", nameof(sha256));
        }

        var lower = sha256.ToLowerInvariant();
        return Path.Combine(Root, lower[..2], lower);
    }

    public bool Contains(string sha256, long expectedSize)
    {
        var path = GetObjectPath(sha256);
        return File.Exists(path) && new FileInfo(path).Length == expectedSize;
    }

    public async Task<string> PutAsync(
        string sourcePath,
        string sha256,
        long expectedSize,
        CancellationToken cancellationToken = default)
    {
        var path = GetObjectPath(sha256);
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length == expectedSize)
            {
                return path;
            }

            throw new InvalidDataException($"内容寻址对象长度不一致：{sha256}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             bufferSize: 81920,
                             useAsync: true))
            await using (var target = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(temporary).Length != expectedSize)
            {
                throw new InvalidDataException($"复制后长度不一致：{sha256}");
            }

            File.Move(temporary, path, overwrite: false);
            return path;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 临时对象残留不影响已有对象。
                }
            }
        }
    }

    public bool Verify(string sha256, long expectedSize, out string? error)
    {
        var path = GetObjectPath(sha256);
        if (!File.Exists(path))
        {
            error = "对象缺失。";
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length != expectedSize)
            {
                error = $"对象长度 {info.Length} 与清单 {expectedSize} 不一致。";
                return false;
            }

            var actual = ComputeHash(path);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "对象内容哈希与清单不一致。";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"对象无法读取：{exception.Message}";
            return false;
        }
    }

    public IEnumerable<string> EnumerateObjectHashes()
    {
        if (!Directory.Exists(Root))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name.Length == 64 && name.All(Uri.IsHexDigit))
                {
                    yield return name.ToLowerInvariant();
                }
            }
        }
    }

    public long GetObjectSize(string sha256) => new FileInfo(GetObjectPath(sha256)).Length;

    public void DeleteObject(string sha256)
    {
        var path = GetObjectPath(sha256);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static string ComputeHash(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            useAsync: false);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
