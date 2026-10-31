// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using Ecl.Core;

namespace Ecl.Deployment;

/// <summary>
/// 更新包路径约束：只接受相对、规范化、位于目标根目录内的路径。
/// 拒绝绝对路径、盘符、父目录、空段和 Windows 保留字符。
/// </summary>
public static class UpdatePathSafety
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly char[] ForbiddenPathChars = ['<', '>', ':', '"', '|', '?', '*'];

    public static bool TryNormalizeRelativePath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim().Replace('\\', '/');
        if (candidate.StartsWith('/') ||
            candidate.Contains('\0') ||
            candidate.Contains(':'))
        {
            return false;
        }

        var parts = candidate.Split('/', StringSplitOptions.None);
        if (parts.Length == 0)
        {
            return false;
        }

        var safeParts = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0 ||
                part is "." or ".." ||
                part.IndexOfAny(ForbiddenPathChars) >= 0 ||
                part.IndexOfAny(InvalidFileNameChars) >= 0 ||
                part.EndsWith(' ') ||
                part.EndsWith('.'))
            {
                return false;
            }

            safeParts.Add(part);
        }

        normalized = string.Join('/', safeParts);
        return normalized.Length > 0;
    }

    public static bool IsContainedIn(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        return string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullCandidate.StartsWith(
                   fullRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}

public static class UpdateHash
{
    public static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => char.IsAsciiHexDigit(character));

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeSha256(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

public static class UpdatePackageVerifier
{
    public static async Task<Result> VerifyAsync(
        string packagePath,
        UpdatePackage package,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath))
        {
            return Result.Fail("update.package_missing", "更新包不存在。", packagePath);
        }

        var length = new FileInfo(packagePath).Length;
        if (length != package.Size)
        {
            return Result.Fail(
                "update.package_size_mismatch",
                "更新包大小与清单不符。",
                $"期望 {package.Size}，实际 {length}。");
        }

        var hash = await UpdateHash.ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
        return string.Equals(hash, package.Sha256, StringComparison.OrdinalIgnoreCase)
            ? Result.Ok()
            : Result.Fail(
                "update.package_hash_mismatch",
                "更新包 SHA-256 与清单不符。",
                $"期望 {package.Sha256}，实际 {hash}。");
    }
}
