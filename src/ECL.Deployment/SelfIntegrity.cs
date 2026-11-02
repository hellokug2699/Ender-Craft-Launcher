// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecl.Core;

namespace Ecl.Deployment;

/// <summary>单个安装文件的完整性状态。缺失、大小不符与哈希不符必须区分（ADR 0034 §2）。</summary>
public enum SelfIntegrityFileStatus
{
    Ok,
    Missing,
    SizeMismatch,
    HashMismatch,
    Unreadable,
    PathEscape,
}

/// <summary>安装清单的来源信任状态。哈希只能证明内容，不能单独证明发布者身份。</summary>
public enum SelfIntegrityTrustStatus
{
    Verified,
    TrustMissing,
    TrustInvalid,
    SignatureMissing,
    SignatureInvalid,
}

public sealed record SelfIntegrityFileResult(string Path, SelfIntegrityFileStatus Status, string? Detail)
{
    public bool IsIntact => Status == SelfIntegrityFileStatus.Ok;
}

public sealed record SelfIntegrityReport(
    string InstallRoot,
    string Version,
    bool VersionMatchesManifest,
    SelfIntegrityTrustStatus Trust,
    IReadOnlyList<SelfIntegrityFileResult> Files,
    IReadOnlyList<string> Issues,
    bool HashChecked,
    TimeSpan Duration)
{
    /// <summary>内容层面是否完整；来源信任单独表达，不把“签名不可用”伪装成文件损坏。</summary>
    public bool ContentIntact => Files.All(file => file.IsIntact);

    public bool IsHealthy => ContentIntact && VersionMatchesManifest && Trust == SelfIntegrityTrustStatus.Verified;

    public IReadOnlyList<SelfIntegrityFileResult> Damaged => Files.Where(file => !file.IsIntact).ToArray();

    public string Describe()
    {
        var trust = Trust switch
        {
            SelfIntegrityTrustStatus.Verified => "来源签名已验证",
            SelfIntegrityTrustStatus.TrustMissing => "缺少更新公钥，无法验证来源",
            SelfIntegrityTrustStatus.TrustInvalid => "更新公钥文件无效",
            SelfIntegrityTrustStatus.SignatureMissing => "缺少安装清单签名",
            SelfIntegrityTrustStatus.SignatureInvalid => "安装清单签名验证失败",
            _ => Trust.ToString(),
        };
        var content = ContentIntact
            ? $"文件完整（{(HashChecked ? "含 SHA-256" : "仅大小检查")}）"
            : $"发现 {Damaged.Count} 个损坏文件";
        return $"ECL {Version}：{content}；{trust}；版本指针{(VersionMatchesManifest ? "一致" : "与清单不一致")}";
    }
}

/// <summary>
/// ECL 自身完整性验证（ADR 0034）。以当前版本目录中的签名更新清单为基准，
/// 逐文件核对大小与 SHA-256；签名验证复用既有 update-trust.json 信任边界。
/// 只读，不修改安装内容。
/// </summary>
public sealed class SelfIntegrityVerifier
{
    public const string ManifestFileName = "update-manifest.json";
    public const string SignatureFileName = "update-manifest.sig";
    public const string TrustFileName = "update-trust.json";

    public async Task<Result<SelfIntegrityReport>> VerifyAsync(
        string installRoot,
        bool verifyHashes = true,
        CancellationToken cancellationToken = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        UpdateLayout layout;
        try
        {
            layout = new UpdateLayout(installRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<SelfIntegrityReport>.Fail("integrity.install_root_invalid", $"安装目录无效：{exception.Message}");
        }

        var currentResult = await layout.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!currentResult.IsSuccess)
        {
            return Result<SelfIntegrityReport>.Fail(
                "integrity.current_missing",
                "未找到可验证的当前版本指针。",
                currentResult.Error!.Message);
        }

        var current = currentResult.Value!;
        var versionDirectory = layout.GetVersionDirectory(current.ParsedVersion!);
        var manifestPath = FindManifest(versionDirectory, layout.InstallRoot);
        if (manifestPath is null)
        {
            return Result<SelfIntegrityReport>.Fail(
                "integrity.manifest_missing",
                "未找到安装清单，无法验证 ECL 自身完整性。",
                versionDirectory);
        }

        byte[] manifestBytes;
        try
        {
            manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<SelfIntegrityReport>.Fail(
                "integrity.manifest_unreadable",
                "无法读取安装清单。",
                exception.Message);
        }

        var parsed = UpdateManifestParser.Parse(manifestBytes);
        if (!parsed.IsSuccess)
        {
            return Result<SelfIntegrityReport>.Fail(
                "integrity.manifest_invalid",
                "安装清单格式无效。",
                parsed.Error!.Message);
        }

        var manifest = parsed.Value!;
        var versionMatches = manifest.Version.CompareTo(current.ParsedVersion!) == 0;
        var issues = new List<string>();
        if (!versionMatches)
        {
            issues.Add($"当前版本 {current.Version} 与安装清单版本 {manifest.Version} 不一致。");
        }

        var (trust, trustIssue) = await VerifyTrustAsync(layout, manifestPath, manifestBytes, cancellationToken)
            .ConfigureAwait(false);
        if (trustIssue is not null)
        {
            issues.Add(trustIssue);
        }

        var files = new List<SelfIntegrityFileResult>(manifest.Files.Count);
        foreach (var file in manifest.Files)
        {
            files.Add(await VerifyFileAsync(versionDirectory, file, verifyHashes, cancellationToken).ConfigureAwait(false));
        }

        foreach (var damaged in files.Where(file => !file.IsIntact))
        {
            issues.Add($"{damaged.Path}：{StatusText(damaged.Status)}{(damaged.Detail is null ? string.Empty : $"（{damaged.Detail}）")}");
        }

        watch.Stop();
        return Result<SelfIntegrityReport>.Ok(new SelfIntegrityReport(
            layout.InstallRoot,
            current.Version,
            versionMatches,
            trust,
            files,
            issues,
            verifyHashes,
            watch.Elapsed));
    }

    private static string? FindManifest(string versionDirectory, string installRoot)
    {
        var versionManifest = Path.Combine(versionDirectory, ManifestFileName);
        if (File.Exists(versionManifest))
        {
            return versionManifest;
        }

        var rootManifest = Path.Combine(installRoot, ManifestFileName);
        return File.Exists(rootManifest) ? rootManifest : null;
    }

    private static async Task<(SelfIntegrityTrustStatus Status, string? Issue)> VerifyTrustAsync(
        UpdateLayout layout,
        string manifestPath,
        byte[] manifestBytes,
        CancellationToken cancellationToken)
    {
        var trustPath = Path.Combine(layout.InstallRoot, TrustFileName);
        if (!File.Exists(trustPath))
        {
            return (SelfIntegrityTrustStatus.TrustMissing, "缺少 update-trust.json；无法验证安装清单来源，但文件内容仍单独校验。");
        }

        var trust = await UpdateTrustStore.FromFileAsync(trustPath, cancellationToken).ConfigureAwait(false);
        if (!trust.IsSuccess)
        {
            return (SelfIntegrityTrustStatus.TrustInvalid, $"更新公钥不可用：{trust.Error!.Message}");
        }

        var signaturePath = Path.Combine(Path.GetDirectoryName(manifestPath)!, SignatureFileName);
        if (!File.Exists(signaturePath))
        {
            return (SelfIntegrityTrustStatus.SignatureMissing, "安装清单没有签名，无法确认发布者身份。");
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = await File.ReadAllBytesAsync(signaturePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (SelfIntegrityTrustStatus.SignatureInvalid, $"无法读取安装清单签名：{exception.Message}");
        }

        var verified = new UpdateSignatureVerifier(trust.Value!).Verify(manifestBytes, signatureBytes);
        return verified.IsSuccess
            ? (SelfIntegrityTrustStatus.Verified, null)
            : (SelfIntegrityTrustStatus.SignatureInvalid, $"安装清单签名验证失败：{verified.Error!.Message}");
    }

    private static async Task<SelfIntegrityFileResult> VerifyFileAsync(
        string versionDirectory,
        UpdateFile expected,
        bool verifyHashes,
        CancellationToken cancellationToken)
    {
        if (!UpdatePathSafety.TryNormalizeRelativePath(expected.Path, out var relative))
        {
            return new SelfIntegrityFileResult(expected.Path, SelfIntegrityFileStatus.PathEscape, "路径无效或越界。");
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(versionDirectory, relative));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SelfIntegrityFileResult(expected.Path, SelfIntegrityFileStatus.PathEscape, exception.Message);
        }

        if (!UpdatePathSafety.IsContainedIn(versionDirectory, full))
        {
            return new SelfIntegrityFileResult(expected.Path, SelfIntegrityFileStatus.PathEscape, "路径越过当前版本目录。");
        }

        FileInfo info;
        try
        {
            info = new FileInfo(full);
            if (!info.Exists)
            {
                return new SelfIntegrityFileResult(expected.Path, SelfIntegrityFileStatus.Missing, "文件不存在。");
            }

            if (info.Length != expected.Size)
            {
                return new SelfIntegrityFileResult(
                    expected.Path,
                    SelfIntegrityFileStatus.SizeMismatch,
                    $"期望 {expected.Size} 字节，实际 {info.Length} 字节。");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SelfIntegrityFileResult(expected.Path, SelfIntegrityFileStatus.Unreadable, exception.Message);
        }

        if (!verifyHashes)
        {
            return new SelfIntegrityFileResult(expected.Path, SelfIntegrityFileStatus.Ok, null);
        }

        try
        {
            var actual = await UpdateHash.ComputeSha256Async(full, cancellationToken).ConfigureAwait(false);
            return string.Equals(actual, expected.Sha256, StringComparison.OrdinalIgnoreCase)
                ? new SelfIntegrityFileResult(expected.Path, SelfIntegrityFileStatus.Ok, null)
                : new SelfIntegrityFileResult(expected.Path, SelfIntegrityFileStatus.HashMismatch, "SHA-256 与安装清单不符。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new SelfIntegrityFileResult(expected.Path, SelfIntegrityFileStatus.Unreadable, exception.Message);
        }
    }

    public static string StatusText(SelfIntegrityFileStatus status) => status switch
    {
        SelfIntegrityFileStatus.Ok => "完整",
        SelfIntegrityFileStatus.Missing => "缺失",
        SelfIntegrityFileStatus.SizeMismatch => "大小不符",
        SelfIntegrityFileStatus.HashMismatch => "内容哈希不符",
        SelfIntegrityFileStatus.Unreadable => "不可读",
        SelfIntegrityFileStatus.PathEscape => "路径越界",
        _ => status.ToString(),
    };
}

public sealed record RecoveryCacheEntry(
    string Version,
    string Directory,
    DateTimeOffset CreatedAtUtc,
    int FileCount,
    long Bytes);

public sealed record RecoveryRestoreResult(
    IReadOnlyList<string> Restored,
    IReadOnlyList<string> MissingFromCache)
{
    public bool Complete => MissingFromCache.Count == 0;
}

/// <summary>
/// 有界的本地恢复材料（ADR 0034 §5）：按版本保存已验证的文件副本与签名清单，
/// 用于离线修复被误删或损坏的 ECL 自有文件。缓存内容同样必须通过哈希与签名校验，
/// 不因为位于本地目录就被视为可信。
/// </summary>
public sealed class RecoveryCache
{
    public const int DefaultMaxVersions = 2;

    public RecoveryCache(string root, int maxVersions = DefaultMaxVersions)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("恢复缓存目录不能为空。", nameof(root));
        }

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        MaxVersions = Math.Max(1, maxVersions);
    }

    public string Root { get; }

    public int MaxVersions { get; }

    public string VersionsDirectory => Path.Combine(Root, "versions");

    public async Task<Result<RecoveryCacheEntry>> CaptureAsync(
        string installRoot,
        CurrentVersion current,
        CancellationToken cancellationToken = default)
    {
        var verification = await new SelfIntegrityVerifier()
            .VerifyAsync(installRoot, verifyHashes: true, cancellationToken)
            .ConfigureAwait(false);
        if (!verification.IsSuccess)
        {
            return Result<RecoveryCacheEntry>.Fail(verification.Error!);
        }

        var report = verification.Value!;
        if (!report.ContentIntact)
        {
            return Result<RecoveryCacheEntry>.Fail(
                "integrity.cache_source_damaged",
                "当前安装内容不完整，拒绝生成恢复缓存。");
        }

        if (report.Trust != SelfIntegrityTrustStatus.Verified)
        {
            return Result<RecoveryCacheEntry>.Fail(
                "integrity.cache_source_untrusted",
                "无法确认安装清单来源，拒绝生成恢复缓存。");
        }

        var layout = new UpdateLayout(installRoot);
        var versionDirectory = layout.GetVersionDirectory(current.ParsedVersion!);
        var manifestPath = File.Exists(Path.Combine(versionDirectory, SelfIntegrityVerifier.ManifestFileName))
            ? Path.Combine(versionDirectory, SelfIntegrityVerifier.ManifestFileName)
            : Path.Combine(layout.InstallRoot, SelfIntegrityVerifier.ManifestFileName);
        var signaturePath = Path.Combine(
            Path.GetDirectoryName(manifestPath)!,
            SelfIntegrityVerifier.SignatureFileName);
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
        {
            return Result<RecoveryCacheEntry>.Fail(
                "integrity.cache_source_incomplete",
                "缺少签名清单，无法生成恢复缓存。");
        }

        var manifest = UpdateManifestParser.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        if (!manifest.IsSuccess)
        {
            return Result<RecoveryCacheEntry>.Fail(manifest.Error!);
        }

        var destination = Path.Combine(VersionsDirectory, current.Version);
        var staging = $"{destination}.staging-{Guid.NewGuid():N}";
        Directory.CreateDirectory(VersionsDirectory);
        long bytes = 0;
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in manifest.Value!.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!UpdatePathSafety.TryNormalizeRelativePath(file.Path, out var relative))
                {
                    return Result<RecoveryCacheEntry>.Fail("integrity.cache_path_invalid", "清单包含越界路径。", file.Path);
                }

                var source = Path.Combine(versionDirectory, relative);
                var target = Path.Combine(staging, relative);
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.Copy(source, target, overwrite: true);
                var copiedHash = await UpdateHash.ComputeSha256Async(target, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(copiedHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return Result<RecoveryCacheEntry>.Fail(
                        "integrity.cache_copy_mismatch",
                        "恢复缓存副本哈希校验失败。",
                        file.Path);
                }

                bytes += new FileInfo(target).Length;
            }

            File.Copy(manifestPath, Path.Combine(staging, SelfIntegrityVerifier.ManifestFileName), overwrite: true);
            File.Copy(signaturePath, Path.Combine(staging, SelfIntegrityVerifier.SignatureFileName), overwrite: true);
            var entry = new RecoveryCacheEntry(current.Version, destination, DateTimeOffset.UtcNow, manifest.Value.Files.Count, bytes);
            await File.WriteAllTextAsync(
                    Path.Combine(staging, CacheEntryFileName),
                    JsonSerializer.Serialize(entry, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);

            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.Move(staging, destination);
            Prune();
            return Result<RecoveryCacheEntry>.Ok(entry with { Directory = destination });
        }
        catch (OperationCanceledException)
        {
            TryDelete(staging);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            TryDelete(staging);
            return Result<RecoveryCacheEntry>.Fail(
                "integrity.cache_capture_failed",
                "生成恢复缓存失败。",
                exception.Message);
        }
    }

    public async Task<Result<RecoveryRestoreResult>> RestoreAsync(
        string installRoot,
        CurrentVersion current,
        IReadOnlyList<string> damagedPaths,
        CancellationToken cancellationToken = default)
    {
        var layout = new UpdateLayout(installRoot);
        var versionDirectory = layout.GetVersionDirectory(current.ParsedVersion!);
        var entryDirectory = Path.Combine(VersionsDirectory, current.Version);
        if (!Directory.Exists(entryDirectory))
        {
            return Result<RecoveryRestoreResult>.Fail(
                "integrity.cache_missing",
                "本地没有该版本的恢复材料。",
                current.Version);
        }

        var manifestPath = Path.Combine(entryDirectory, SelfIntegrityVerifier.ManifestFileName);
        var signaturePath = Path.Combine(entryDirectory, SelfIntegrityVerifier.SignatureFileName);
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
        {
            return Result<RecoveryRestoreResult>.Fail(
                "integrity.cache_incomplete",
                "恢复缓存缺少签名清单，拒绝使用。");
        }

        var trustPath = Path.Combine(layout.InstallRoot, SelfIntegrityVerifier.TrustFileName);
        var trust = await UpdateTrustStore.FromFileAsync(trustPath, cancellationToken).ConfigureAwait(false);
        if (!trust.IsSuccess)
        {
            return Result<RecoveryRestoreResult>.Fail(
                "integrity.repair_untrusted",
                "缺少可用的更新公钥，无法验证恢复材料来源。",
                trust.Error!.Message);
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var signatureBytes = await File.ReadAllBytesAsync(signaturePath, cancellationToken).ConfigureAwait(false);
        var signature = new UpdateSignatureVerifier(trust.Value!).Verify(manifestBytes, signatureBytes);
        if (!signature.IsSuccess)
        {
            return Result<RecoveryRestoreResult>.Fail(
                "integrity.repair_untrusted",
                "恢复材料签名验证失败，拒绝使用。",
                signature.Error!.Message);
        }

        var parsed = UpdateManifestParser.Parse(manifestBytes);
        if (!parsed.IsSuccess)
        {
            return Result<RecoveryRestoreResult>.Fail(parsed.Error!);
        }

        var manifest = parsed.Value!;
        if (manifest.Version.CompareTo(current.ParsedVersion!) != 0)
        {
            return Result<RecoveryRestoreResult>.Fail(
                "integrity.repair_version_mismatch",
                "恢复材料的版本与当前版本不一致。");
        }

        var expected = manifest.Files.ToDictionary(
            file => file.Path.Replace('\\', '/'),
            file => file,
            StringComparer.OrdinalIgnoreCase);
        var restored = new List<string>();
        var missing = new List<string>();
        foreach (var path in damagedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!expected.TryGetValue(path.Replace('\\', '/'), out var file) ||
                !UpdatePathSafety.TryNormalizeRelativePath(file.Path, out var relative))
            {
                missing.Add(path);
                continue;
            }

            var cached = Path.Combine(entryDirectory, relative);
            if (!File.Exists(cached))
            {
                missing.Add(path);
                continue;
            }

            var cachedHash = await UpdateHash.ComputeSha256Async(cached, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(cachedHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                missing.Add(path);
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(versionDirectory, relative));
            if (!UpdatePathSafety.IsContainedIn(versionDirectory, target))
            {
                missing.Add(path);
                continue;
            }

            var targetDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            var temporary = $"{target}.ecl-restore-{Guid.NewGuid():N}";
            try
            {
                File.Copy(cached, temporary, overwrite: true);
                File.Move(temporary, target, overwrite: true);
                restored.Add(file.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDelete(temporary);
                missing.Add(path);
            }
        }

        return Result<RecoveryRestoreResult>.Ok(new RecoveryRestoreResult(restored, missing));
    }

    /// <summary>按创建时间保留最近的若干版本，避免恢复缓存无限增长。</summary>
    public int Prune()
    {
        if (!Directory.Exists(VersionsDirectory))
        {
            return 0;
        }

        var removed = 0;
        var directories = Directory.EnumerateDirectories(VersionsDirectory)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(info => info.CreationTimeUtc)
            .ToArray();
        foreach (var directory in directories.Skip(MaxVersions))
        {
            TryDelete(directory.FullName);
            removed++;
        }

        return removed;
    }

    private const string CacheEntryFileName = "recovery-entry.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主结果。
        }
    }
}

public sealed record RepairOutcome(
    SelfIntegrityReport Before,
    SelfIntegrityReport? After,
    IReadOnlyList<string> RestoredFiles,
    IReadOnlyList<string> ResidualIssues)
{
    public bool Success => After?.IsHealthy == true;
}

/// <summary>
/// Repair Mode（ADR 0034 §6）：只恢复 ECL 安装清单拥有的文件，绝不删除或重置实例、世界、
/// 配置、日志等用户数据。凭据不足或来源不可信时明确失败，而不是用未验证文件继续启动。
/// </summary>
public sealed class SelfRepairService
{
    private readonly SelfIntegrityVerifier _verifier;
    private readonly RecoveryCache? _cache;

    public SelfRepairService(SelfIntegrityVerifier? verifier = null, RecoveryCache? cache = null)
    {
        _verifier = verifier ?? new SelfIntegrityVerifier();
        _cache = cache;
    }

    public async Task<Result<RepairOutcome>> RepairAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        var verification = await _verifier.VerifyAsync(installRoot, verifyHashes: true, cancellationToken)
            .ConfigureAwait(false);
        if (!verification.IsSuccess)
        {
            return Result<RepairOutcome>.Fail(verification.Error!);
        }

        var before = verification.Value!;
        if (before.ContentIntact)
        {
            return Result<RepairOutcome>.Ok(new RepairOutcome(before, before, [], before.Issues));
        }

        if (before.Trust != SelfIntegrityTrustStatus.Verified)
        {
            return Result<RepairOutcome>.Fail(
                "integrity.repair_untrusted",
                "无法确认 ECL 安装的来源签名，拒绝自动修复。",
                before.Trust.ToString());
        }

        var layout = new UpdateLayout(installRoot);
        var currentResult = await layout.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!currentResult.IsSuccess)
        {
            return Result<RepairOutcome>.Fail(currentResult.Error!);
        }

        var cache = _cache ?? new RecoveryCache(Path.Combine(layout.InstallRoot, "recovery"));
        var restore = await cache
            .RestoreAsync(
                layout.InstallRoot,
                currentResult.Value!,
                before.Damaged.Select(file => file.Path).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        if (!restore.IsSuccess)
        {
            return Result<RepairOutcome>.Fail(
                "integrity.repair_unavailable",
                "本地恢复材料不足，无法在离线状态下完成修复。",
                restore.Error!.Message);
        }

        var afterVerification = await _verifier.VerifyAsync(layout.InstallRoot, verifyHashes: true, cancellationToken)
            .ConfigureAwait(false);
        var after = afterVerification.IsSuccess ? afterVerification.Value : null;
        var residual = new List<string>(restore.Value!.MissingFromCache.Count);
        if (restore.Value.MissingFromCache.Count > 0)
        {
            residual.Add($"本地恢复材料缺失：{string.Join("、", restore.Value.MissingFromCache)}");
        }

        if (after is not null && !after.ContentIntact)
        {
            residual.AddRange(after.Damaged.Select(file => $"{file.Path}：{SelfIntegrityVerifier.StatusText(file.Status)}"));
        }

        return Result<RepairOutcome>.Ok(new RepairOutcome(before, after, restore.Value.Restored, residual));
    }
}
