// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using Ecl.Core;
using Ecl.Deployment;
using Ecl.Download;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Additions;

public sealed record AcquiredAdditionPackage(string Path, bool OwnsFile);

/// <summary>
/// 扩展包获取。实现可以是 GitHub Release 静态资产；安装器只接受最终文件并自行校验，
/// 不信任提供方声称的哈希或大小（ADR 0032 §8）。
/// </summary>
public interface IAdditionPackageProvider
{
    Task<Result<AcquiredAdditionPackage>> AcquireAsync(
        AdditionEntry entry,
        CancellationToken cancellationToken = default);
}

/// <summary>测试与本地目录分发使用：从指定目录读取 <c>&lt;id&gt;-&lt;version&gt;.zip</c>。</summary>
public sealed class LocalAdditionPackageProvider(string directory) : IAdditionPackageProvider
{
    public Task<Result<AcquiredAdditionPackage>> AcquireAsync(
        AdditionEntry entry,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetFullPath(directory), $"{entry.Id.Value}-{entry.Version}.zip");
        return Task.FromResult(
            File.Exists(path)
                ? Result<AcquiredAdditionPackage>.Ok(new AcquiredAdditionPackage(path, OwnsFile: false))
                : Result<AcquiredAdditionPackage>.Fail(
                    "additions.package_not_found",
                    $"未找到扩展包：{entry.Id.Value} {entry.Version}。",
                    path));
    }
}

/// <summary>通过既有下载服务获取扩展包；下载到临时位置，校验由安装器完成。</summary>
public sealed class DownloadAdditionPackageProvider(
    IDownloadService downloadService,
    string temporaryDirectory) : IAdditionPackageProvider
{
    public async Task<Result<AcquiredAdditionPackage>> AcquireAsync(
        AdditionEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(entry.Package.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Result<AcquiredAdditionPackage>.Fail(
                "additions.package_url_insecure",
                "扩展包地址必须是 HTTPS 绝对地址。");
        }

        Directory.CreateDirectory(temporaryDirectory);
        var destination = Path.Combine(
            temporaryDirectory,
            $"addition-{entry.Id.Value}-{entry.Version}-{Guid.NewGuid():N}.zip");
        try
        {
            await downloadService.DownloadAsync(
                    [
                        new DownloadRequest(
                            destination,
                            [uri],
                            entry.Package.Sha256,
                            HashingAlgorithm.Sha256,
                            entry.Package.Size,
                            entry.Name),
                    ],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Result<AcquiredAdditionPackage>.Ok(new AcquiredAdditionPackage(destination, OwnsFile: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteFile(destination);
            throw;
        }
        catch (DownloadException exception)
        {
            TryDeleteFile(destination);
            return Result<AcquiredAdditionPackage>.Fail(
                "additions.package_download_failed",
                "下载扩展包失败；普通游戏启动不受影响。",
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            TryDeleteFile(destination);
            return Result<AcquiredAdditionPackage>.Fail(
                "additions.package_download_failed",
                "下载扩展包失败；普通游戏启动不受影响。",
                exception.Message);
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
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// 扩展安装、更新、回滚与卸载（ADR 0032 §7-8）。包先解压到 staging 并逐文件校验，
/// 之后原子移动到版本目录；状态写入失败会删除新版本并保留原有可用版本。
/// 卸载只移除扩展包，不删除实例、世界、配置或扩展用户数据。
/// </summary>
public sealed class AdditionInstaller(
    IAdditionPackageProvider provider,
    AdditionStateStore stateStore,
    string additionsRoot,
    ILogger<AdditionInstaller>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<AdditionInstaller>.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string AdditionsRoot { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(additionsRoot));

    public string PackagesDirectory => Path.Combine(AdditionsRoot, "packages");

    public string StagingDirectory => Path.Combine(AdditionsRoot, ".staging");

    public string DataDirectory(AdditionId id) => Path.Combine(AdditionsRoot, "data", id.Value);

    public AdditionStateLoad LoadState() => stateStore.Load();

    public async Task<Result<InstalledAddition>> InstallAsync(
        AdditionEntry entry,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InstallCoreAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Result<InstalledAddition>> InstallCoreAsync(
        AdditionEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Withdrawn)
        {
            return Result<InstalledAddition>.Fail(
                "additions.entry_withdrawn",
                $"扩展 {entry.Id.Value} 已停止分发。");
        }

        var acquired = await provider.AcquireAsync(entry, cancellationToken).ConfigureAwait(false);
        if (!acquired.IsSuccess)
        {
            return Result<InstalledAddition>.Fail(acquired.Error!);
        }

        var package = acquired.Value!;
        var staging = Path.Combine(StagingDirectory, $"{entry.Id.Value}-{entry.Version}-{Guid.NewGuid():N}");
        try
        {
            var packageError = await VerifyPackageAsync(package.Path, entry, cancellationToken).ConfigureAwait(false);
            if (packageError is not null)
            {
                return Result<InstalledAddition>.Fail(packageError);
            }

            Directory.CreateDirectory(StagingDirectory);
            var extraction = await SafeZipExtractor
                .ExtractAsync(package.Path, staging, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!extraction.IsSuccess)
            {
                return Result<InstalledAddition>.Fail(extraction.Error!);
            }

            var manifestResult = await VerifyExtractedAsync(staging, entry, cancellationToken).ConfigureAwait(false);
            if (!manifestResult.IsSuccess)
            {
                return Result<InstalledAddition>.Fail(manifestResult.Error!);
            }

            var manifest = manifestResult.Value!;
            var destination = GetVersionDirectory(entry.Id, entry.Version);
            if (!UpdatePathSafety.IsContainedIn(PackagesDirectory, destination))
            {
                return Result<InstalledAddition>.Fail("additions.install_path_invalid", "扩展安装目录越界。");
            }

            var versionParent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(versionParent);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.Move(staging, destination);
            return CommitInstall(entry, manifest, destination);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogWarning(exception, "安装扩展 {Addition} 失败。", entry.Id.Value);
            return Result<InstalledAddition>.Fail("additions.install_failed", "安装扩展失败。", exception.Message);
        }
        finally
        {
            TryDeleteDirectory(staging);
            if (package.OwnsFile)
            {
                TryDeleteFile(package.Path);
            }
        }
    }

    private Result<InstalledAddition> CommitInstall(
        AdditionEntry entry,
        AdditionPackageManifest manifest,
        string destination)
    {
        var load = stateStore.Load();
        if (load.IsCorrupt)
        {
            return Result<InstalledAddition>.Fail(
                "additions.state_corrupt",
                "扩展状态文件损坏，拒绝安装以免丢失已安装记录。",
                load.Error);
        }

        var document = load.Document;
        var existing = document.Additions.FirstOrDefault(item => item.Id == entry.Id);
        var record = new InstalledAddition(
            entry.Id,
            entry.Type,
            entry.Version,
            destination,
            manifest.EntryAssembly.Replace('\\', '/'),
            Enabled: existing?.Enabled ?? true,
            entry.DataSchemaVersion,
            manifest.Files.Select(file => new InstalledAdditionFile(file.Path, file.Size, file.Sha256)).ToArray(),
            DateTimeOffset.UtcNow,
            existing is not null && existing.Version.CompareTo(entry.Version) != 0
                ? existing.Version
                : existing?.PreviousVersion)
        {
            Dependencies = entry.Dependencies.Where(dependency => !dependency.Optional).Select(dependency => dependency.Id).ToArray(),
            CoreApiMin = entry.CoreApi.Min,
            CoreApiMax = entry.CoreApi.Max,
        };

        document.Additions.RemoveAll(item => item.Id == entry.Id);
        document.Additions.Add(record);
        var saved = stateStore.Save(document);
        if (!saved.IsSuccess)
        {
            TryDeleteDirectory(destination);
            return Result<InstalledAddition>.Fail(saved.Error!);
        }

        PruneOldVersions(entry.Id, record.Version, record.PreviousVersion);
        _logger.LogInformation("扩展 {Addition} {Version} 已安装。", entry.Id.Value, entry.Version);
        return Result<InstalledAddition>.Ok(record);
    }

    public async Task<Result> UninstallAsync(
        AdditionId id,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var load = stateStore.Load();
            if (load.IsCorrupt)
            {
                return Result.Fail("additions.state_corrupt", "扩展状态文件损坏，拒绝卸载。", load.Error);
            }

            var document = load.Document;
            var record = document.Additions.FirstOrDefault(item => item.Id == id);
            if (record is null)
            {
                return Result.Fail("additions.not_installed", $"扩展 {id.Value} 未安装。");
            }

            var dependents = document.Additions
                .Where(item => item.Id != id && item.Dependencies.Contains(id))
                .Select(item => item.Id.Value)
                .ToArray();
            if (dependents.Length > 0 && !force)
            {
                return Result.Fail(
                    "additions.dependency_in_use",
                    $"以下扩展依赖 {id.Value}：{string.Join("、", dependents)}；请先卸载或显式强制卸载。");
            }

            var packageDirectory = GetAdditionPackageDirectory(id);
            var quarantine = Path.Combine(AdditionsRoot, ".trash", $"{id.Value}-{Guid.NewGuid():N}");
            var moved = false;
            try
            {
                if (Directory.Exists(packageDirectory))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
                    Directory.Move(packageDirectory, quarantine);
                    moved = true;
                }

                document.Additions.RemoveAll(item => item.Id == id);
                var saved = stateStore.Save(document);
                if (!saved.IsSuccess)
                {
                    if (moved)
                    {
                        Directory.Move(quarantine, packageDirectory);
                    }

                    return Result.Fail(saved.Error!);
                }

                TryDeleteDirectory(quarantine);
                _logger.LogInformation("扩展 {Addition} 已卸载；用户数据目录保留。", id.Value);
                return Result.Ok();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (moved && !Directory.Exists(packageDirectory) && Directory.Exists(quarantine))
                {
                    try
                    {
                        Directory.Move(quarantine, packageDirectory);
                    }
                    catch (IOException)
                    {
                    }
                }

                return Result.Fail("additions.uninstall_failed", "卸载扩展失败。", exception.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result> SetEnabledAsync(
        AdditionId id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var load = stateStore.Load();
            var document = load.Document;
            var index = document.Additions.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return Result.Fail("additions.not_installed", $"扩展 {id.Value} 未安装。");
            }

            var record = document.Additions[index];
            if (record.Enabled == enabled)
            {
                return Result.Ok();
            }

            document.Additions[index] = record with { Enabled = enabled };
            var saved = stateStore.Save(document);
            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<InstalledAddition>> RollbackAsync(
        AdditionId id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var load = stateStore.Load();
            var document = load.Document;
            var record = document.Additions.FirstOrDefault(item => item.Id == id);
            if (record is null || record.PreviousVersion is null)
            {
                return Result<InstalledAddition>.Fail(
                    "additions.no_previous_version",
                    $"扩展 {id.Value} 没有可回滚的上一版本。");
            }

            var previousDirectory = GetVersionDirectory(id, record.PreviousVersion);
            if (!Directory.Exists(previousDirectory))
            {
                return Result<InstalledAddition>.Fail(
                    "additions.previous_missing",
                    $"扩展 {id.Value} 的上一版本目录不存在。");
            }

            var manifestPath = Path.Combine(previousDirectory, AdditionPackageManifestParser.FileName);
            var manifest = AdditionPackageManifestParser.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            if (!manifest.IsSuccess)
            {
                return Result<InstalledAddition>.Fail(manifest.Error!);
            }

            var rolled = record with
            {
                Version = record.PreviousVersion,
                Directory = previousDirectory,
                EntryAssembly = manifest.Value!.EntryAssembly.Replace('\\', '/'),
                Files = manifest.Value.Files.Select(file => new InstalledAdditionFile(file.Path, file.Size, file.Sha256)).ToArray(),
                PreviousVersion = record.Version,
                InstalledAt = DateTimeOffset.UtcNow,
            };
            document.Additions[document.Additions.FindIndex(item => item.Id == id)] = rolled;
            var saved = stateStore.Save(document);
            return saved.IsSuccess ? Result<InstalledAddition>.Ok(rolled) : Result<InstalledAddition>.Fail(saved.Error!);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string GetVersionDirectory(AdditionId id, ReleaseVersion version) =>
        Path.Combine(GetAdditionPackageDirectory(id), version.ToString());

    public string GetAdditionPackageDirectory(AdditionId id) =>
        Path.Combine(PackagesDirectory, id.Value);

    private async Task<EclError?> VerifyPackageAsync(
        string packagePath,
        AdditionEntry entry,
        CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(packagePath);
            if (!info.Exists)
            {
                return new EclError("additions.package_missing", "扩展包文件不存在。", packagePath);
            }

            if (info.Length != entry.Package.Size)
            {
                return new EclError(
                    "additions.package_size_mismatch",
                    "扩展包大小与目录不符，拒绝安装。",
                    $"{info.Length} != {entry.Package.Size}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new EclError("additions.package_unreadable", "无法读取扩展包。", exception.Message);
        }

        var hash = await UpdateHash.ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
        return string.Equals(hash, entry.Package.Sha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : new EclError("additions.package_hash_mismatch", "扩展包 SHA-256 与目录不符，拒绝安装。");
    }

    private static async Task<Result<AdditionPackageManifest>> VerifyExtractedAsync(
        string staging,
        AdditionEntry entry,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(staging, AdditionPackageManifestParser.FileName);
        if (!File.Exists(manifestPath))
        {
            return Result<AdditionPackageManifest>.Fail(
                "additions.package_manifest_missing",
                "扩展包缺少 addition.json，拒绝安装。");
        }

        var parsed = AdditionPackageManifestParser.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        if (!parsed.IsSuccess)
        {
            return parsed;
        }

        var manifest = parsed.Value!;
        if (manifest.Id != entry.Id || manifest.Version.CompareTo(entry.Version) != 0)
        {
            return Result<AdditionPackageManifest>.Fail(
                "additions.package_identity_mismatch",
                "扩展包清单与目录条目不一致。");
        }

        if (entry.DataSchemaVersion is not null && manifest.DataSchemaVersion != entry.DataSchemaVersion)
        {
            return Result<AdditionPackageManifest>.Fail(
                "additions.package_schema_mismatch",
                "扩展包数据 schema 与目录声明不一致。");
        }

        var expected = new Dictionary<string, AdditionPackageFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!UpdatePathSafety.TryNormalizeRelativePath(file.Path, out var relative))
            {
                return Result<AdditionPackageManifest>.Fail(
                    "additions.package_file_invalid",
                    $"扩展包路径越界：{file.Path}");
            }

            var full = Path.GetFullPath(Path.Combine(staging, relative));
            if (!UpdatePathSafety.IsContainedIn(staging, full) || !File.Exists(full))
            {
                return Result<AdditionPackageManifest>.Fail(
                    "additions.package_file_missing",
                    $"扩展包缺少清单文件：{file.Path}");
            }

            var length = new FileInfo(full).Length;
            if (length != file.Size)
            {
                return Result<AdditionPackageManifest>.Fail(
                    "additions.package_file_size_mismatch",
                    $"扩展包文件大小与清单不符：{file.Path}");
            }

            var hash = await UpdateHash.ComputeSha256Async(full, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Result<AdditionPackageManifest>.Fail(
                    "additions.package_file_hash_mismatch",
                    $"扩展包文件 SHA-256 与清单不符：{file.Path}");
            }

            expected[file.Path.Replace('\\', '/')] = file;
        }

        foreach (var actual in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, actual).Replace('\\', '/');
            if (string.Equals(relative, AdditionPackageManifestParser.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!expected.ContainsKey(relative))
            {
                return Result<AdditionPackageManifest>.Fail(
                    "additions.package_unexpected_file",
                    $"扩展包包含清单之外的文件：{relative}");
            }
        }

        return Result<AdditionPackageManifest>.Ok(manifest);
    }

    private void PruneOldVersions(AdditionId id, ReleaseVersion current, ReleaseVersion? previous)
    {
        var directory = GetAdditionPackageDirectory(id);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var versionDirectory in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(versionDirectory);
            if (string.Equals(name, current.ToString(), StringComparison.OrdinalIgnoreCase) ||
                (previous is not null && string.Equals(name, previous.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            TryDeleteDirectory(versionDirectory);
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
        catch (IOException)
        {
        }
    }
}
