// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;

namespace Ecl.Deployment;

public sealed record InstallManifestFile(string Path, long Size, string Sha256);

public sealed record InstallManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "install-manifest.json";
    public const string UninstallerFileName = "ECL.Uninstaller.exe";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Product { get; init; } = "ECL";

    public required string Version { get; init; }

    public required string InstallRoot { get; init; }

    public IReadOnlyList<InstallManifestFile> Files { get; init; } = [];

    public IReadOnlyList<string> Directories { get; init; } = [];

    public string Uninstaller { get; init; } = UninstallerFileName;

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record InstallManifestValidation(
    InstallManifest Manifest,
    IReadOnlyList<InstallManifestFile> MissingFiles,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// 安装清单的写入与读取。清单中的路径必须是相对路径，并在读取和删除前重新校验
/// 仍位于安装根目录内；缺失/损坏/版本不兼容时拒绝自动删除。
/// </summary>
public static class InstallManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string GetPath(string installRoot) =>
        Path.Combine(Path.GetFullPath(installRoot), InstallManifest.FileName);

    public static InstallManifest Create(string installRoot, string version)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var files = new List<InstallManifestFile>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateOwnedFiles(root))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var info = new FileInfo(file);
            files.Add(new InstallManifestFile(relative, info.Length, HashFile(file)));
            var directory = Path.GetDirectoryName(relative)?.Replace('\\', '/');
            while (!string.IsNullOrEmpty(directory) && directory != ".")
            {
                directories.Add(directory);
                directory = Path.GetDirectoryName(directory)?.Replace('\\', '/');
            }
        }

        return new InstallManifest
        {
            Version = version,
            InstallRoot = root,
            Files = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
            Directories = directories.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static async Task<Result<InstallManifest>> WriteAsync(
        string installRoot,
        InstallManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(installRoot);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(manifest, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return Result<InstallManifest>.Ok(manifest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<InstallManifest>.Fail("install.manifest_write_failed", $"写入安装清单失败：{exception.Message}");
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
                    // 临时文件清理失败不覆盖主结果。
                }
            }
        }
    }

    public static Result<InstallManifestValidation> Read(string installRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var path = GetPath(root);
        if (!File.Exists(path))
        {
            return Result<InstallManifestValidation>.Fail(
                "install.manifest_missing",
                "未找到 install-manifest.json，拒绝自动卸载。");
        }

        InstallManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result<InstallManifestValidation>.Fail(
                "install.manifest_corrupt",
                $"安装清单损坏：{exception.Message}");
        }

        if (manifest is null ||
            manifest.SchemaVersion > InstallManifest.CurrentSchemaVersion ||
            !string.Equals(manifest.Product, "ECL", StringComparison.Ordinal))
        {
            return Result<InstallManifestValidation>.Fail(
                "install.manifest_incompatible",
                "安装清单版本或产品标识不受支持。");
        }

        var errors = new List<string>();
        var missing = new List<InstallManifestFile>();
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(manifest.InstallRoot),
                root,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("安装清单记录的安装根目录与当前目录不一致。");
        }

        foreach (var file in manifest.Files)
        {
            if (!UpdatePathSafety.TryNormalizeRelativePath(file.Path, out var relative))
            {
                errors.Add($"安装清单包含越界路径：{file.Path}");
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!UpdatePathSafety.IsContainedIn(root, full))
            {
                errors.Add($"安装清单路径越界：{file.Path}");
                continue;
            }

            if (!File.Exists(full))
            {
                missing.Add(file);
                continue;
            }

            try
            {
                if (new FileInfo(full).Length != file.Size)
                {
                    missing.Add(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                missing.Add(file);
            }
        }

        if (!UpdatePathSafety.TryNormalizeRelativePath(manifest.Uninstaller, out _))
        {
            errors.Add("安装清单中的卸载器路径无效。");
        }

        return Result<InstallManifestValidation>.Ok(new InstallManifestValidation(manifest, missing, errors));
    }

    public static string ResolveFile(string installRoot, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        if (!UpdatePathSafety.TryNormalizeRelativePath(relativePath, out var relative))
        {
            throw new InvalidOperationException($"安装清单路径无效：{relativePath}");
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!UpdatePathSafety.IsContainedIn(root, full))
        {
            throw new InvalidOperationException($"安装清单路径越界：{relativePath}");
        }

        return full;
    }

    private static IEnumerable<string> EnumerateOwnedFiles(string root)
    {
        var rootFiles = new[]
        {
            "current.json",
            "update-trust.json",
            "update-manifest.json",
            "update-manifest.sig",
            "ECL.Bootstrapper.exe",
            InstallManifest.UninstallerFileName,
        };
        foreach (var name in rootFiles)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        foreach (var directory in new[] { "versions" })
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(
                         path,
                         "*",
                         new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                             MaxRecursionDepth = 8,
                         }))
            {
                yield return file;
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
