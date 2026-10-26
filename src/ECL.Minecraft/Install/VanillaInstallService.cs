// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Localization;
using Ecl.Download;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Versions;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Install;

public sealed class VanillaInstallService : IVersionInstallService
{
    private const string OfficialLibrariesRoot = "https://libraries.minecraft.net/";
    private const string OfficialAssetsRoot = "https://resources.download.minecraft.net/";

    private readonly IInstanceService _instances;
    private readonly IAppPaths _paths;
    private readonly IDownloadService _downloads;
    private readonly DownloadSourceResolver _sources;
    private readonly ILocalizer _localizer;
    private readonly ILogger<VanillaInstallService> _logger;

    public VanillaInstallService(
        IInstanceService instances,
        IAppPaths paths,
        IDownloadService downloads,
        DownloadSourceResolver sources,
        ILocalizer localizer,
        ILogger<VanillaInstallService> logger)
    {
        _instances = instances;
        _paths = paths;
        _downloads = downloads;
        _sources = sources;
        _localizer = localizer;
        _logger = logger;
    }

    public async Task<Result<InstallReport>> InstallAsync(
        string instanceId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstallReport>.Fail("install.instance_missing", _localizer.Get("install.error.instanceMissing", instanceId));
        }

        if (!string.Equals(instance.Loader, "vanilla", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(instance.VersionId, instance.GameVersion, StringComparison.Ordinal))
        {
            return Result<InstallReport>.Fail(
                 "install.unsupported_loader",
                _localizer.Get("install.error.unsupportedLoader"));
        }

        try
        {
            Report(progress, InstallStage.Manifest, "install.stage.manifest", 0, 0, 0, 0, null);

            var manifestText = await _downloads
                .DownloadTextAsync(_sources.GetManifestCandidates(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var manifest = JsonSerializer.Deserialize<VersionManifest>(manifestText, EclJson.Default);
            if (manifest is null || manifest.Versions.Count == 0)
            {
                return Result<InstallReport>.Fail("install.manifest_invalid", _localizer.Get("install.error.manifestInvalid"));
            }

            var entry = manifest.Versions.FirstOrDefault(
                item => string.Equals(item.Id, instance.GameVersion, StringComparison.Ordinal));

            if (entry is null)
            {
                return Result<InstallReport>.Fail(
                    "install.version_not_found",
                    _localizer.Get("install.error.versionNotFound", instance.GameVersion));
            }

            Report(progress, InstallStage.Version, "install.stage.version", 0, 0, 0, 0, null);

            var version = await ResolveVersionChainAsync(entry, manifest, cancellationToken)
                .ConfigureAwait(false);
            if (version is null)
            {
                return Result<InstallReport>.Fail("install.version_invalid", _localizer.Get("install.error.versionInvalid"));
            }

            var downloadedFiles = 0;
            var skippedFiles = 0;
            long bytes = 0;

            var clientResult = await InstallClientAsync(instance, version, progress, cancellationToken).ConfigureAwait(false);
            if (clientResult is null)
            {
                return Result<InstallReport>.Fail("install.client_missing", _localizer.Get("install.error.clientMissing"));
            }

            Add(clientResult, ref downloadedFiles, ref skippedFiles, ref bytes);

            var libraryResult = await InstallLibrariesAsync(version, progress, cancellationToken).ConfigureAwait(false);
            Add(libraryResult, ref downloadedFiles, ref skippedFiles, ref bytes);

            var assetResult = await InstallAssetsAsync(version, progress, cancellationToken).ConfigureAwait(false);
            if (assetResult is null)
            {
                return Result<InstallReport>.Fail("install.assets_missing", _localizer.Get("install.error.assetsMissing"));
            }

            Add(assetResult, ref downloadedFiles, ref skippedFiles, ref bytes);

            var loggingResult = await InstallLoggingAsync(version, progress, cancellationToken).ConfigureAwait(false);
            Add(loggingResult, ref downloadedFiles, ref skippedFiles, ref bytes);

            total.Stop();
            var report = new InstallReport(instance.VersionId, downloadedFiles, skippedFiles, bytes, total.Elapsed);

            Report(
                progress,
                InstallStage.Completed,
                "install.stage.completed",
                downloadedFiles + skippedFiles,
                downloadedFiles + skippedFiles,
                bytes,
                bytes,
                null);

            _logger.LogInformation(
                "实例 {InstanceId} 安装完成：下载 {Downloaded}，跳过 {Skipped}，{Bytes} 字节，耗时 {Duration}。",
                instance.Id,
                downloadedFiles,
                skippedFiles,
                bytes,
                total.Elapsed);

            return Result<InstallReport>.Ok(report);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadException exception)
        {
            _logger.LogError(exception, "安装实例 {InstanceId} 时下载失败。", instance.Id);
            return Result<InstallReport>.Fail(
                "install.download_failed",
                _localizer.Get("install.downloadFailed", exception.Message),
                string.Join("; ", exception.Failures.Take(5)));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(exception, "安装实例 {InstanceId} 失败。", instance.Id);
            return Result<InstallReport>.Fail(
                "install.failed",
                _localizer.Get("install.failed", exception.Message));
        }
    }

    private async Task<MinecraftVersion?> ResolveVersionChainAsync(
        VersionManifestEntry entry,
        VersionManifest manifest,
        CancellationToken cancellationToken)
    {
        var chain = new List<MinecraftVersion>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = entry;

        while (true)
        {
            if (!visited.Add(current.Id))
            {
                throw new InvalidOperationException("版本继承链存在循环。");
            }

            var destination = VersionFilePlanner.GetVersionJsonPath(_paths, current.Id);
            var json = await EnsureTextFileAsync(
                    _sources.GetCandidates(current.Url),
                    destination,
                    current.Sha1,
                    current.Size,
                    cancellationToken)
                .ConfigureAwait(false);

            var version = JsonSerializer.Deserialize<MinecraftVersion>(json, EclJson.Default)
                          ?? throw new InvalidOperationException($"版本文件无效：{current.Id}");
            if (string.IsNullOrWhiteSpace(version.Id))
            {
                version.Id = current.Id;
            }

            chain.Add(version);

            if (string.IsNullOrWhiteSpace(version.InheritsFrom))
            {
                break;
            }

            var parent = manifest.Versions.FirstOrDefault(
                item => string.Equals(item.Id, version.InheritsFrom, StringComparison.Ordinal));

            if (parent is null)
            {
                throw new InvalidOperationException($"版本清单中找不到父版本：{version.InheritsFrom}");
            }

            current = parent;
        }

        var merged = chain[^1];
        for (var index = chain.Count - 2; index >= 0; index--)
        {
            merged = VersionMerger.Merge(merged, chain[index]);
        }

        return merged;
    }

    private async Task<DownloadBatchResult?> InstallClientAsync(
        GameInstance instance,
        MinecraftVersion version,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var client = version.Downloads?.Client;
        if (string.IsNullOrWhiteSpace(client?.Url))
        {
            return null;
        }

        var request = CreateRequest(
            VersionFilePlanner.GetClientJarPath(_paths, instance.VersionId),
            client.Url,
            client.Sha1,
            client.Size,
            $"{instance.VersionId}.jar");

        return await DownloadBatchAsync(
                InstallStage.Client,
                "install.stage.client",
                [request],
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DownloadBatchResult> InstallLibrariesAsync(
        MinecraftVersion version,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = PlatformLibrarySelector.Plan(
            version.Libraries,
            LibraryRules.CurrentOsName,
            LibraryRules.CurrentArch);

        var requests = new List<DownloadRequest>();
        foreach (var artifact in PlatformLibrarySelector.GetDownloadArtifacts(plan))
        {
            if (artifact.Path is null)
            {
                continue;
            }

            var url = artifact.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                url = new Uri(new Uri(OfficialLibrariesRoot), artifact.Path.Replace('\\', '/')).ToString();
            }

            requests.Add(CreateRequest(
                VersionFilePlanner.GetLibraryPath(_paths, artifact),
                url,
                artifact.Sha1,
                artifact.Size,
                Path.GetFileName(artifact.Path)));
        }

        return await DownloadBatchAsync(
                InstallStage.Libraries,
                "install.stage.libraries",
                requests,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DownloadBatchResult?> InstallAssetsAsync(
        MinecraftVersion version,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var assetIndex = version.AssetIndex;
        if (assetIndex is null || string.IsNullOrWhiteSpace(assetIndex.Id) || string.IsNullOrWhiteSpace(assetIndex.Url))
        {
            return null;
        }

        var indexPath = VersionFilePlanner.GetAssetIndexPath(_paths, assetIndex.Id);
        var indexJson = await EnsureTextFileAsync(
                _sources.GetCandidates(assetIndex.Url),
                indexPath,
                assetIndex.Sha1,
                assetIndex.Size,
                cancellationToken)
            .ConfigureAwait(false);

        var index = JsonSerializer.Deserialize<AssetIndexFile>(indexJson, EclJson.Default);
        if (index is null)
        {
            throw new InvalidOperationException("资源索引无效。");
        }

        var requests = new List<DownloadRequest>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in index.Objects.Values)
        {
            if (string.IsNullOrWhiteSpace(asset.Hash) || !seen.Add(asset.Hash))
            {
                continue;
            }

            var relative = $"{asset.Hash[..2]}/{asset.Hash}";
            var officialUrl = OfficialAssetsRoot + relative;
            var destination = VersionFilePlanner.GetAssetObjectPath(_paths, asset.Hash);
            requests.Add(CreateRequest(destination, officialUrl, asset.Hash, asset.Size, relative));
        }

        return await DownloadBatchAsync(
                InstallStage.Assets,
                "install.stage.assets",
                requests,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DownloadBatchResult> InstallLoggingAsync(
        MinecraftVersion version,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var file = version.Logging?.Client?.File;
        if (file is null || string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.Url))
        {
            return new DownloadBatchResult(0, 0, 0);
        }

        var request = CreateRequest(
            VersionFilePlanner.GetLoggingFilePath(_paths, file.Id),
            file.Url,
            file.Sha1,
            file.Size,
            file.Id);

        return await DownloadBatchAsync(
                InstallStage.Logging,
                "install.stage.logging",
                [request],
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private DownloadRequest CreateRequest(
        string destination,
        string url,
        string? hash,
        long? size,
        string displayName)
    {
        var candidates = _sources.GetCandidates(url);
        return new DownloadRequest(destination, candidates, hash, HashingAlgorithm.Sha1, size, displayName);
    }

    private async Task<DownloadBatchResult> DownloadBatchAsync(
        InstallStage stage,
        string messageKey,
        IReadOnlyList<DownloadRequest> requests,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return new DownloadBatchResult(0, 0, 0);
        }

        var message = _localizer.Get(messageKey);
        var totalFiles = requests.Count;
        var totalBytes = requests.Sum(request => request.Size ?? 0);
        Report(progress, stage, messageKey, 0, totalFiles, 0, totalBytes, null);

        var forwarding = new ForwardingProgress<DownloadProgress>(download =>
            progress?.Report(new InstallProgress(
                stage,
                message,
                download.CompletedFiles,
                download.TotalFiles,
                download.CompletedBytes,
                download.TotalBytes,
                download.CurrentFile)));

        return await _downloads
            .DownloadAsync(requests, forwarding, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> EnsureTextFileAsync(
        IReadOnlyList<Uri> urls,
        string destination,
        string? expectedHash,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination) && MatchesHash(destination, expectedHash, expectedSize))
        {
            return await File.ReadAllTextAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        var text = await _downloads
            .DownloadTextAsync(urls, expectedHash, expectedSize, HashingAlgorithm.Sha1, cancellationToken)
            .ConfigureAwait(false);

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, text, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, destination, overwrite: true);
        return text;
    }

    private static bool MatchesHash(string path, string? expectedHash, long? expectedSize)
    {
        var info = new FileInfo(path);
        if (expectedSize.HasValue && info.Length != expectedSize.Value)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return true;
        }

        var actual = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void Add(DownloadBatchResult result, ref int downloaded, ref int skipped, ref long bytes)
    {
        downloaded += result.DownloadedFiles;
        skipped += result.SkippedFiles;
        bytes += result.Bytes;
    }

    private void Report(
        IProgress<InstallProgress>? progress,
        InstallStage stage,
        string messageKey,
        int completedFiles,
        int totalFiles,
        long completedBytes,
        long totalBytes,
        string? currentFile)
    {
        progress?.Report(new InstallProgress(
            stage,
            _localizer.Get(messageKey),
            completedFiles,
            totalFiles,
            completedBytes,
            totalBytes,
            currentFile));
    }

    private sealed class ForwardingProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
