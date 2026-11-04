// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using Ecl.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Deployment;

public sealed record StagedUpdate(UpdateManifest Manifest, string VersionDirectory);

/// <summary>
/// 旁路准备、验证后切换：发布包先解压到 staging 并逐文件校验，完成后才移动到 versions/，
/// 最后原子写入 current.json。运行中的主程序不会被覆盖；失败时旧版本和指针保持不变。
/// </summary>
public sealed class UpdateInstaller(
    UpdateLayout layout,
    ILogger<UpdateInstaller>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<UpdateInstaller>.Instance;

    public async Task<Result<StagedUpdate>> StageAsync(
        UpdateManifest manifest,
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var packageCheck = await UpdatePackageVerifier
            .VerifyAsync(packagePath, manifest.Package, cancellationToken)
            .ConfigureAwait(false);
        if (!packageCheck.IsSuccess)
        {
            return Result<StagedUpdate>.Fail(packageCheck.Error!);
        }

        layout.EnsureCreated();
        var stagingDirectory = Path.Combine(
            layout.StagingDirectory,
            $"{manifest.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var extraction = await SafeZipExtractor
                .ExtractAsync(packagePath, stagingDirectory, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!extraction.IsSuccess)
            {
                return Result<StagedUpdate>.Fail(extraction.Error!);
            }

            if (manifest.Files.Count > 0)
            {
                var verification = await VerifyFilesAsync(stagingDirectory, manifest.Files, cancellationToken)
                    .ConfigureAwait(false);
                if (!verification.IsSuccess)
                {
                    return Result<StagedUpdate>.Fail(verification.Error!);
                }
            }

            var versionDirectory = layout.GetVersionDirectory(manifest.Version);
            if (Directory.Exists(versionDirectory))
            {
                return Result<StagedUpdate>.Fail(
                    "update.version_exists",
                    "目标版本目录已经存在，拒绝覆盖。",
                    versionDirectory);
            }

            Directory.Move(stagingDirectory, versionDirectory);
            _logger.LogInformation(
                "更新 {Version} 已准备到 {Directory}，尚未切换当前版本。",
                manifest.Version,
                versionDirectory);
            return Result<StagedUpdate>.Ok(new StagedUpdate(manifest, versionDirectory));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogWarning(exception, "准备更新 {Version} 失败。", manifest.Version);
            return Result<StagedUpdate>.Fail(
                "update.stage_failed",
                "准备更新失败。",
                exception.Message);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }
    }

    public async Task<Result<CurrentVersion>> ApplyAsync(
        UpdateManifest manifest,
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var currentResult = await layout.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        var previous = currentResult.IsSuccess ? currentResult.Value : null;
        if (!currentResult.IsSuccess && File.Exists(layout.CurrentFile))
        {
            return Result<CurrentVersion>.Fail(currentResult.Error!);
        }

        if (previous is not null &&
            previous.ParsedVersion is { } currentVersion &&
            manifest.Version.CompareTo(currentVersion) < 0)
        {
            return Result<CurrentVersion>.Fail(
                "update.downgrade_refused",
                "自动更新不允许降级；如需回退请使用显式回滚。",
                $"{currentVersion} -> {manifest.Version}");
        }

        var staged = await StageOrReuseAsync(manifest, packagePath, cancellationToken).ConfigureAwait(false);
        if (!staged.IsSuccess)
        {
            return Result<CurrentVersion>.Fail(staged.Error!);
        }

        var current = new CurrentVersion
        {
            Version = manifest.Version.ToString(),
            Executable = await FindExecutableAsync(staged.Value!.VersionDirectory, cancellationToken)
                .ConfigureAwait(false),
            PreviousVersion = previous?.Version,
            InstalledAt = DateTimeOffset.UtcNow,
            ParsedVersion = manifest.Version,
        };

        var write = await layout.WriteCurrentAsync(current, cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess)
        {
            // 指针未切换成功：移除刚准备好的版本目录，避免留下一个“看似已安装”的孤儿版本。
            TryDeleteDirectory(staged.Value.VersionDirectory);
            return Result<CurrentVersion>.Fail(write.Error!);
        }

        _logger.LogInformation(
            "已切换到 ECL {Version}（上一版本：{Previous}）。",
            manifest.Version,
            previous?.Version ?? "无");
        return Result<CurrentVersion>.Ok(current);
    }

    private async Task<Result<StagedUpdate>> StageOrReuseAsync(
        UpdateManifest manifest,
        string packagePath,
        CancellationToken cancellationToken)
    {
        var versionDirectory = layout.GetVersionDirectory(manifest.Version);
        if (!Directory.Exists(versionDirectory))
        {
            return await StageAsync(manifest, packagePath, cancellationToken).ConfigureAwait(false);
        }

        // 允许“先 staging、退出后再切换”的流程：目录已存在但必须通过清单校验。
        if (manifest.Files.Count > 0)
        {
            var verification = await VerifyFilesAsync(versionDirectory, manifest.Files, cancellationToken)
                .ConfigureAwait(false);
            if (!verification.IsSuccess)
            {
                return Result<StagedUpdate>.Fail(verification.Error!);
            }
        }

        return Result<StagedUpdate>.Ok(new StagedUpdate(manifest, versionDirectory));
    }

    /// <summary>
    /// 清理未完成的 staging。staging 是临时区域，允许整体清理；
    /// 但绝不触碰 versions/ 与 current.json 引用的版本。
    /// </summary>
    public int CleanupStaging()
    {
        if (!Directory.Exists(layout.StagingDirectory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(layout.StagingDirectory).ToArray())
        {
            try
            {
                if (UpdatePathSafety.IsContainedIn(layout.StagingDirectory, directory))
                {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "清理 staging 失败：{Directory}", directory);
            }
        }

        return removed;
    }

    public async Task<Result<CurrentVersion>> RollbackAsync(CancellationToken cancellationToken = default)
    {
        var currentResult = await layout.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!currentResult.IsSuccess)
        {
            return currentResult;
        }

        var current = currentResult.Value!;
        if (!ReleaseVersion.TryParse(current.PreviousVersion, out var previousVersion))
        {
            return Result<CurrentVersion>.Fail(
                "update.no_previous_version",
                "没有可回滚的上一版本。");
        }

        var previousDirectory = layout.GetVersionDirectory(previousVersion);
        if (!Directory.Exists(previousDirectory))
        {
            return Result<CurrentVersion>.Fail(
                "update.previous_missing",
                "上一版本目录不存在，无法回滚。",
                previousDirectory);
        }

        var rolledBack = new CurrentVersion
        {
            Version = previousVersion.ToString(),
            Executable = await FindExecutableAsync(previousDirectory, cancellationToken).ConfigureAwait(false),
            PreviousVersion = current.Version,
            InstalledAt = DateTimeOffset.UtcNow,
            ParsedVersion = previousVersion,
        };

        var write = await layout.WriteCurrentAsync(rolledBack, cancellationToken).ConfigureAwait(false);
        if (!write.IsSuccess)
        {
            return Result<CurrentVersion>.Fail(write.Error!);
        }

        _logger.LogInformation("已回滚到 ECL {Version}。", previousVersion);
        return Result<CurrentVersion>.Ok(rolledBack);
    }

    public static async Task<Result> VerifyFilesAsync(
        string directory,
        IReadOnlyList<UpdateFile> files,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            return Result.Ok();
        }

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var path = Path.GetFullPath(Path.Combine(
                directory,
                file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!UpdatePathSafety.IsContainedIn(directory, path))
            {
                return Result.Fail(
                    "update.file_path_escape",
                    "更新文件清单包含越界路径。",
                    file.Path);
            }

            expected.Add(file.Path.Replace('\\', '/'));
            if (!File.Exists(path))
            {
                return Result.Fail(
                    "update.file_missing",
                    "更新包缺少清单中的文件。",
                    file.Path);
            }

            var length = new FileInfo(path).Length;
            if (length != file.Size)
            {
                return Result.Fail(
                    "update.file_size_mismatch",
                    "更新文件大小与清单不符。",
                    file.Path);
            }

            var hash = await UpdateHash.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Fail(
                    "update.file_hash_mismatch",
                    "更新文件 SHA-256 与清单不符。",
                    file.Path);
            }
        }

        foreach (var actual in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, actual).Replace('\\', '/');
            if (!expected.Contains(relative))
            {
                return Result.Fail(
                    "update.unexpected_file",
                    "更新包包含清单之外的文件。",
                    relative);
            }
        }

        return Result.Ok();
    }

    private static async Task<string> FindExecutableAsync(string directory, CancellationToken cancellationToken)
    {
        var candidate = Path.Combine(directory, "ECL.exe");
        if (File.Exists(candidate))
        {
            return "ECL.exe";
        }

        var fallback = Directory
            .EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .FirstOrDefault(name => !string.Equals(name, "ECL.Bootstrapper.exe", StringComparison.OrdinalIgnoreCase));
        return fallback ?? "ECL.exe";
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主结果；残留的 staging 可由后续清理任务处理。
        }
    }
}
