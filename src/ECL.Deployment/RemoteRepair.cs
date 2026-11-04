// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http;
using System.Security.Cryptography;
using Ecl.Core;

namespace Ecl.Deployment;

/// <summary>远程修复获取到的安装包。调用方负责在完成后删除 OwnsFile 为 true 的临时文件。</summary>
public sealed record RepairPackage(UpdateManifest Manifest, string PackagePath, bool OwnsFile);

/// <summary>
/// 远程修复来源。实现必须返回与当前版本精确一致的已签名清单与包；
/// 不得用“更新到最新版本”替代修复当前版本（ADR 0034 §3）。
/// </summary>
public interface IRepairPackageSource
{
    Task<Result<RepairPackage>> AcquireAsync(
        ReleaseVersion version,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 从现有更新 feed 获取当前版本的完整包。清单与 detached signature 由 <see cref="UpdateClient"/> 验证，
/// 包长度与 SHA-256 在下载完成后再次校验；只接受 HTTPS。
/// </summary>
public sealed class UpdateFeedRepairSource(
    UpdateClient client,
    Uri feedBase,
    UpdateChannel channel,
    string temporaryDirectory) : IRepairPackageSource
{
    public async Task<Result<RepairPackage>> AcquireAsync(
        ReleaseVersion version,
        CancellationToken cancellationToken = default)
    {
        if (feedBase.Scheme != Uri.UriSchemeHttps)
        {
            return Result<RepairPackage>.Fail("update.insecure_feed", "远程修复源必须使用 HTTPS。");
        }

        var check = await client.CheckAsync(feedBase, channel, cancellationToken).ConfigureAwait(false);
        if (!check.IsSuccess)
        {
            return Result<RepairPackage>.Fail(check.Error!);
        }

        var manifest = check.Value!.Manifest;
        if (manifest.Version.CompareTo(version) != 0)
        {
            return Result<RepairPackage>.Fail(
                "repair.version_unavailable",
                "更新源没有提供当前版本的修复包；不会用其他版本替换当前安装。",
                $"当前 {version}，更新源 {manifest.Version}");
        }

        Directory.CreateDirectory(temporaryDirectory);
        var destination = Path.Combine(
            temporaryDirectory,
            $"ecl-repair-{version}-{Guid.NewGuid():N}.zip");
        var download = await client
            .DownloadPackageAsync(manifest, check.Value.PackageUri, destination, cancellationToken)
            .ConfigureAwait(false);
        if (!download.IsSuccess)
        {
            return Result<RepairPackage>.Fail(download.Error!);
        }

        return Result<RepairPackage>.Ok(new RepairPackage(manifest, destination, OwnsFile: true));
    }
}

/// <summary>
/// 联网 Repair Mode（ADR 0034 §3）：从受信任发布源重新获取当前版本的完整包，
/// 解包并逐文件校验后只覆盖损坏/缺失的 ECL 文件；不删除或重置任何用户数据。
/// 下载、解包、清单校验或签名任一失败都拒绝替换文件。
/// </summary>
public sealed class RemoteRepairService(
    IRepairPackageSource packageSource,
    SelfIntegrityVerifier? verifier = null)
{
    private readonly SelfIntegrityVerifier _verifier = verifier ?? new SelfIntegrityVerifier();

    public async Task<Result<RepairOutcome>> RepairAsync(
        string installRoot,
        IReadOnlyList<string>? damagedPaths = null,
        CancellationToken cancellationToken = default)
    {
        UpdateLayout layout;
        try
        {
            layout = new UpdateLayout(installRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<RepairOutcome>.Fail("integrity.install_root_invalid", $"安装目录无效：{exception.Message}");
        }

        var current = await layout.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return Result<RepairOutcome>.Fail(current.Error!);
        }

        var beforeResult = await _verifier.VerifyAsync(layout.InstallRoot, verifyHashes: true, cancellationToken)
            .ConfigureAwait(false);
        if (!beforeResult.IsSuccess)
        {
            return Result<RepairOutcome>.Fail(beforeResult.Error!);
        }

        var before = beforeResult.Value!;
        if (before.Trust != SelfIntegrityTrustStatus.Verified)
        {
            return Result<RepairOutcome>.Fail(
                "integrity.repair_untrusted",
                "无法确认 ECL 安装的来源签名，拒绝联网修复。",
                before.Trust.ToString());
        }

        var damaged = (damagedPaths ?? before.Damaged.Select(file => file.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (damaged.Length == 0)
        {
            return Result<RepairOutcome>.Ok(new RepairOutcome(before, before, [], before.Issues));
        }

        var packageResult = await packageSource
            .AcquireAsync(current.Value!.ParsedVersion!, cancellationToken)
            .ConfigureAwait(false);
        if (!packageResult.IsSuccess)
        {
            return Result<RepairOutcome>.Fail(packageResult.Error!);
        }

        var package = packageResult.Value!;
        var staging = Path.Combine(layout.StagingDirectory, $"repair-{Guid.NewGuid():N}");
        try
        {
            if (package.Manifest.Version.CompareTo(current.Value.ParsedVersion!) != 0)
            {
                return Result<RepairOutcome>.Fail(
                    "repair.version_unavailable",
                    "远程修复包版本与当前版本不一致，拒绝使用。");
            }

            Directory.CreateDirectory(layout.StagingDirectory);
            var extraction = await SafeZipExtractor
                .ExtractAsync(package.PackagePath, staging, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!extraction.IsSuccess)
            {
                return Result<RepairOutcome>.Fail(extraction.Error!);
            }

            var verified = await UpdateInstaller
                .VerifyFilesAsync(staging, package.Manifest.Files, cancellationToken)
                .ConfigureAwait(false);
            if (!verified.IsSuccess)
            {
                return Result<RepairOutcome>.Fail(verified.Error!);
            }

            var expected = package.Manifest.Files.ToDictionary(
                file => file.Path.Replace('\\', '/'),
                file => file,
                StringComparer.OrdinalIgnoreCase);
            var versionDirectory = layout.GetVersionDirectory(current.Value.ParsedVersion!);
            var restored = new List<string>();
            var residual = new List<string>();
            foreach (var path in damaged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!expected.TryGetValue(path.Replace('\\', '/'), out var entry) ||
                    !UpdatePathSafety.TryNormalizeRelativePath(entry.Path, out var relative))
                {
                    residual.Add($"清单中没有 {path}，未恢复。");
                    continue;
                }

                var source = Path.Combine(staging, relative);
                if (!File.Exists(source))
                {
                    residual.Add($"修复包缺少 {entry.Path}。");
                    continue;
                }

                var hash = await UpdateHash.ComputeSha256Async(source, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    residual.Add($"{entry.Path} 哈希校验失败，未恢复。");
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(versionDirectory, relative));
                if (!UpdatePathSafety.IsContainedIn(versionDirectory, target))
                {
                    residual.Add($"{entry.Path} 目标路径越界，未恢复。");
                    continue;
                }

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporary = $"{target}.ecl-remote-repair-{Guid.NewGuid():N}";
                try
                {
                    File.Copy(source, temporary, overwrite: true);
                    File.Move(temporary, target, overwrite: true);
                    restored.Add(entry.Path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    TryDeleteFile(temporary);
                    residual.Add($"{entry.Path} 写入失败：{exception.Message}");
                }
            }

            var afterResult = await _verifier.VerifyAsync(layout.InstallRoot, verifyHashes: true, cancellationToken)
                .ConfigureAwait(false);
            var after = afterResult.IsSuccess ? afterResult.Value : null;
            if (after is not null)
            {
                residual.AddRange(after.Damaged.Select(file =>
                    $"{file.Path}：{SelfIntegrityVerifier.StatusText(file.Status)}"));
            }

            return Result<RepairOutcome>.Ok(new RepairOutcome(before, after, restored, residual));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return Result<RepairOutcome>.Fail(
                "integrity.repair_failed",
                "联网修复失败，当前安装未被完整替换。",
                exception.Message);
        }
        finally
        {
            TryDeleteDirectory(staging);
            if (package.OwnsFile)
            {
                TryDeleteFile(package.PackagePath);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
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
        }
    }
}
