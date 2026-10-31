// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Localization;
using Ecl.Download;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;
using Ecl.Operations;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Install;

public sealed class VanillaInstallService : IVersionInstallService
{
    private const string OfficialLibrariesRoot = "https://libraries.minecraft.net/";
    private const string OfficialAssetsRoot = "https://resources.download.minecraft.net/";
    private const string CleanroomMavenRoot =
        "https://repo.cleanroommc.com/releases/com/cleanroommc/cleanroom";
    private const string OptiFineRoot = "https://bmclapi2.bangbang93.com/optifine";

    private readonly IInstanceService _instances;
    private readonly IAppPaths _paths;
    private readonly IDownloadService _downloads;
    private readonly DownloadSourceResolver _sources;
    private readonly ILoaderCatalog _catalog;
    private readonly IJavaSelectionService _javaSelection;
    private readonly IForgeProcessorRunner _forgeRunner;
    private readonly ILocalizer _localizer;
    private readonly IOperationService? _operations;
    private readonly ILogger<VanillaInstallService> _logger;

    public VanillaInstallService(
        IInstanceService instances,
        IAppPaths paths,
        IDownloadService downloads,
        DownloadSourceResolver sources,
        ILoaderCatalog catalog,
        IJavaSelectionService javaSelection,
        IForgeProcessorRunner forgeRunner,
        ILocalizer localizer,
        ILogger<VanillaInstallService> logger,
        IOperationService? operations = null)
    {
        _instances = instances;
        _paths = paths;
        _downloads = downloads;
        _sources = sources;
        _catalog = catalog;
        _javaSelection = javaSelection;
        _forgeRunner = forgeRunner;
        _localizer = localizer;
        _operations = operations;
        _logger = logger;
    }

    public async Task<Result<InstallReport>> InstallAsync(
        string instanceId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstallReport>.Fail("install.instance_missing", _localizer.Get("install.error.instanceMissing", instanceId));
        }

        if (!LoaderKinds.TryParse(instance.Loader, out var loaderKind))
        {
            return Result<InstallReport>.Fail(
                "install.unsupported_loader",
                _localizer.Get("install.error.unsupportedLoader"));
        }

        if (loaderKind != LoaderKind.Vanilla && string.IsNullOrWhiteSpace(instance.LoaderVersion))
        {
            return Result<InstallReport>.Fail(
                "install.loader_version_missing",
                _localizer.Get("install.error.loaderVersionMissing"));
        }

        var transaction = new InstallTransaction();

        // 整个安装只登记一个任务：下载批次复用该任务，不再产生重复条目。
        using var operation = new OperationProgressBridge<InstallProgress>(
            _operations,
            new OperationStart
            {
                Kind = OperationKind.Install,
                Title = _localizer.Get("install.task.title", instance.Name),
                StageId = "install",
                Source = "Mojang",
                CanCancel = true,
                CanRetry = true,
            },
            progress,
            MapInstallProgress);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = cancellation.Token;
        operation.AttachControl(cancellation, retryToken => InstallAsync(instanceId, progress, retryToken));

        try
        {
            var result = await InstallCoreAsync(instance, loaderKind, transaction, operation, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                transaction.Commit();
                operation.Complete();
            }
            else
            {
                var actions = transaction.Rollback();
                WriteDiagnostics(instance, result.Error!, actions);
                operation.Fail(ToOperationError(result.Error!));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            var error = new EclError("install.cancelled", _localizer.Get("install.cancelled"));
            var actions = transaction.Rollback();
            WriteDiagnostics(instance, error, actions);
            throw;
        }
        catch (DownloadException exception)
        {
            operation.Fail(new OperationError(
                exception.Kind,
                "install.download_failed",
                exception.Message,
                exception.Failures.FirstOrDefault()));
            _logger.LogError(exception, "安装实例 {InstanceId} 时下载失败。", instance.Id);
            var error = new EclError(
                "install.download_failed",
                _localizer.Get("install.downloadFailed", exception.Message),
                string.Join("; ", exception.Failures.Take(5)));
            var actions = transaction.Rollback();
            WriteDiagnostics(instance, error, actions);
            return Result<InstallReport>.Fail(error);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException
                                              or InvalidOperationException or InvalidDataException
                                              or System.ComponentModel.Win32Exception)
        {
            operation.Fail(OperationErrors.FromException("install.failed", exception));
            _logger.LogError(exception, "安装实例 {InstanceId} 失败。", instance.Id);
            var error = new EclError("install.failed", _localizer.Get("install.failed", exception.Message));
            var actions = transaction.Rollback();
            WriteDiagnostics(instance, error, actions);
            return Result<InstallReport>.Fail(error);
        }
    }

    public async Task<Result<InstallReport>> ReconfigureAsync(
        string instanceId,
        string gameVersion,
        string loader,
        string? loaderVersion = null,
        string? fabricApiVersion = null,
        string? optiFineVersion = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var original = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (original is null)
        {
            return Result<InstallReport>.Fail("install.instance_missing", _localizer.Get("install.error.instanceMissing", instanceId));
        }

        var update = await _instances.UpdateConfigurationAsync(
            instanceId, gameVersion, loader, loaderVersion, fabricApiVersion, optiFineVersion, cancellationToken)
            .ConfigureAwait(false);
        if (!update.IsSuccess)
        {
            return Result<InstallReport>.Fail(update.Error!);
        }

        try
        {
            var result = await InstallAsync(instanceId, progress, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "实例 {InstanceId} 已完成原地配置变更；旧版本文件保留以便安全回退。",
                    instanceId);
                return result;
            }

            await _instances.UpdateConfigurationAsync(
                instanceId,
                original.GameVersion,
                original.Loader,
                original.LoaderVersion,
                original.FabricApiVersion,
                original.OptiFineVersion,
                CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await _instances.UpdateConfigurationAsync(
                instanceId,
                original.GameVersion,
                original.Loader,
                original.LoaderVersion,
                original.FabricApiVersion,
                original.OptiFineVersion,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Result<InstallReport>> InstallCoreAsync(
        GameInstance instance,
        LoaderKind loaderKind,
        InstallTransaction transaction,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();

        MinecraftVersion version;
        ForgeInstallPlan? forgePlan = null;

        if (loaderKind == LoaderKind.Cleanroom)
        {
            var cleanroomResult = await InstallCleanroomLoaderAsync(instance, transaction, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!cleanroomResult.IsSuccess)
            {
                return Result<InstallReport>.Fail(cleanroomResult.Error!);
            }

            version = cleanroomResult.Value!;
        }
        else
        {
            var baseResult = await ResolveBaseVersionAsync(instance, loaderKind, transaction, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!baseResult.IsSuccess)
            {
                return Result<InstallReport>.Fail(baseResult.Error!);
            }

            version = baseResult.Value!;

            if (LoaderKinds.UsesInstaller(loaderKind))
            {
                var planResult = await PrepareForgeInstallerAsync(
                        instance,
                        loaderKind,
                        version,
                        transaction,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!planResult.IsSuccess)
                {
                    return Result<InstallReport>.Fail(planResult.Error!);
                }

                forgePlan = planResult.Value!;
                version = forgePlan.Version;
            }
        }

        var downloadedFiles = 0;
        var skippedFiles = 0;
        long bytes = 0;

        var clientResult = await InstallClientAsync(instance, version, transaction, progress, cancellationToken)
            .ConfigureAwait(false);
        if (clientResult is null)
        {
            return Result<InstallReport>.Fail("install.client_missing", _localizer.Get("install.error.clientMissing"));
        }

        Add(clientResult, ref downloadedFiles, ref skippedFiles, ref bytes);

        var libraryResult = await InstallLibrariesAsync(version, transaction, progress, cancellationToken)
            .ConfigureAwait(false);
        Add(libraryResult, ref downloadedFiles, ref skippedFiles, ref bytes);

        var assetResult = await InstallAssetsAsync(version, transaction, progress, cancellationToken).ConfigureAwait(false);
        if (assetResult is null)
        {
            return Result<InstallReport>.Fail("install.assets_missing", _localizer.Get("install.error.assetsMissing"));
        }

        Add(assetResult, ref downloadedFiles, ref skippedFiles, ref bytes);

        var loggingResult = await InstallLoggingAsync(version, transaction, progress, cancellationToken)
            .ConfigureAwait(false);
        Add(loggingResult, ref downloadedFiles, ref skippedFiles, ref bytes);

        var components = new List<string>();

        if (forgePlan is not null)
        {
            if (ForgeProcessorPlanner.HasClientProcessors(forgePlan.Profile))
            {
                var processors = await InstallForgeProcessorLibrariesAsync(
                        forgePlan,
                        transaction,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                Add(processors, ref downloadedFiles, ref skippedFiles, ref bytes);

                var javaResult = await ResolveInstallJavaAsync(instance, version, cancellationToken)
                    .ConfigureAwait(false);
                if (!javaResult.IsSuccess)
                {
                    return Result<InstallReport>.Fail(javaResult.Error!);
                }

                var patchedPath = ForgeProcessorPlanner.ResolveClientPatchedPath(
                    forgePlan.Profile,
                    _paths.LibrariesDirectory);
                if (!string.IsNullOrWhiteSpace(patchedPath))
                {
                    // processors 生成的是派生客户端 jar，不属于下载列表；失败时同样要清理。
                    transaction.TrackFile(patchedPath);
                }

                var runResult = await _forgeRunner
                    .RunAsync(
                        new ForgeProcessorRunRequest(
                            javaResult.Value!,
                            forgePlan.InstallerPath,
                            VersionFilePlanner.GetClientJarPath(_paths, instance.VersionId),
                            instance.GameVersion,
                            ForgeProcessorPlanner.ClientSide,
                            forgePlan.Profile),
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!runResult.IsSuccess)
                {
                    return Result<InstallReport>.Fail(runResult.Error!);
                }
            }

            // spec 0 没有 processors，直接使用 -universal.jar；但仍然算安装了加载器组件。
            components.Add($"{LoaderKinds.ToId(loaderKind)}:{instance.LoaderVersion}");
        }

        if (loaderKind == LoaderKind.Fabric)
        {
            var fabricApi = await InstallFabricApiAsync(instance, transaction, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!fabricApi.IsSuccess)
            {
                return Result<InstallReport>.Fail(fabricApi.Error!);
            }

            Add(fabricApi.Value!.Result, ref downloadedFiles, ref skippedFiles, ref bytes);
            components.Add($"fabric-api:{fabricApi.Value.Version}");
        }

        if (loaderKind is LoaderKind.Cleanroom or LoaderKind.Forge &&
            !string.IsNullOrWhiteSpace(instance.OptiFineVersion))
        {
            var optiFine = await InstallOptiFineAsync(instance, transaction, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!optiFine.IsSuccess)
            {
                return Result<InstallReport>.Fail(optiFine.Error!);
            }

            Add(optiFine.Value!.Result, ref downloadedFiles, ref skippedFiles, ref bytes);
            components.Add($"optifine:{instance.OptiFineVersion}");
        }

        total.Stop();
        var report = new InstallReport(
            instance.VersionId,
            downloadedFiles,
            skippedFiles,
            bytes,
            total.Elapsed,
            components);

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
            "实例 {InstanceId} 安装完成：加载器 {Loader}，下载 {Downloaded}，跳过 {Skipped}，{Bytes} 字节，耗时 {Duration}。",
            instance.Id,
            instance.Loader,
            downloadedFiles,
            skippedFiles,
            bytes,
            total.Elapsed);

        return Result<InstallReport>.Ok(report);
    }

    private void WriteDiagnostics(GameInstance instance, EclError error, IReadOnlyList<string> rollbackActions)
    {
        try
        {
            var path = InstallTransaction.WriteDiagnostics(_paths, instance, error, rollbackActions);
            _logger.LogInformation("安装失败诊断已写入 {Path}。", path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "写入安装失败诊断失败。");
        }
    }

    private async Task<Result<MinecraftVersion>> ResolveBaseVersionAsync(
        GameInstance instance,
        LoaderKind loaderKind,
        InstallTransaction transaction,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, InstallStage.Manifest, "install.stage.manifest", 0, 0, 0, 0, null);

        var manifestText = await _downloads
            .DownloadTextAsync(_sources.GetManifestCandidates(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var manifest = JsonSerializer.Deserialize<VersionManifest>(manifestText, EclJson.Default);
        if (manifest is null || manifest.Versions.Count == 0)
        {
            return Result<MinecraftVersion>.Fail("install.manifest_invalid", _localizer.Get("install.error.manifestInvalid"));
        }

        var entry = manifest.Versions.FirstOrDefault(
            item => string.Equals(item.Id, instance.GameVersion, StringComparison.Ordinal));

        if (entry is null)
        {
            return Result<MinecraftVersion>.Fail(
                "install.version_not_found",
                _localizer.Get("install.error.versionNotFound", instance.GameVersion));
        }

        Report(progress, InstallStage.Version, "install.stage.version", 0, 0, 0, 0, null);

        var baseVersion = await ResolveVersionChainAsync(entry, manifest, cancellationToken).ConfigureAwait(false);
        if (baseVersion is null)
        {
            return Result<MinecraftVersion>.Fail("install.version_invalid", _localizer.Get("install.error.versionInvalid"));
        }

        if (loaderKind != LoaderKind.Fabric)
        {
            return Result<MinecraftVersion>.Ok(baseVersion);
        }

        Report(progress, InstallStage.Loader, "install.stage.loader", 0, 0, 0, 0, null);

        var profile = await _catalog
            .GetFabricProfileAsync(instance.GameVersion, instance.LoaderVersion!, cancellationToken)
            .ConfigureAwait(false);

        if (!profile.IsSuccess)
        {
            return Result<MinecraftVersion>.Fail(profile.Error!);
        }

        var version = JsonSerializer.Deserialize<MinecraftVersion>(profile.Value!, EclJson.Default);
        if (version is null || string.IsNullOrWhiteSpace(version.MainClass))
        {
            return Result<MinecraftVersion>.Fail(
                "install.loader_failed",
                _localizer.Get("install.error.loaderFailed", "Fabric"));
        }

        version.Id = instance.VersionId;
        version.InheritsFrom = instance.GameVersion;
        WriteVersionJson(instance.VersionId, version, transaction);

        var merged = VersionMerger.Merge(baseVersion, version);
        return Result<MinecraftVersion>.Ok(merged);
    }

    private async Task<Result<MinecraftVersion>> InstallCleanroomLoaderAsync(
        GameInstance instance,
        InstallTransaction transaction,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var loaderVersion = instance.LoaderVersion!;
        var installerName = $"cleanroom-{loaderVersion}-installer.jar";
        var installerPath = Path.Combine(_paths.CacheDirectory, "cleanroom", installerName);
        var installerUrl = $"{CleanroomMavenRoot}/{loaderVersion}/{installerName}";

        Report(progress, InstallStage.Loader, "install.stage.loader", 0, 0, 0, 0, null);

        var request = new DownloadRequest(
            installerPath,
            [new Uri(installerUrl)],
            DisplayName: installerName);

        await DownloadBatchAsync(
                InstallStage.Loader,
                "install.stage.loader",
                [request],
                transaction,
                progress,
                cancellationToken,
                trackFiles: false)
            .ConfigureAwait(false);

        try
        {
            var content = CleanroomInstallerReader.Read(installerPath);
            content.Version.Id = instance.VersionId;
            WriteVersionJson(instance.VersionId, content.Version, transaction);
            return Result<MinecraftVersion>.Ok(content.Version);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
        {
            _logger.LogError(exception, "解析 Cleanroom 安装器失败：{Path}", installerPath);
            return Result<MinecraftVersion>.Fail(
                "install.loader_failed",
                _localizer.Get("install.error.loaderFailed", "Cleanroom"));
        }
    }

    private async Task<Result<ForgeInstallPlan>> PrepareForgeInstallerAsync(
        GameInstance instance,
        LoaderKind loaderKind,
        MinecraftVersion baseVersion,
        InstallTransaction transaction,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, InstallStage.Loader, "install.stage.loader", 0, 0, 0, 0, null);

        var mavenRoot = ForgeInstallers.GetMavenRoot(loaderKind);
        var installerUrl = ForgeInstallers.GetInstallerUrl(loaderKind, instance.GameVersion, instance.LoaderVersion!);
        var installerName = Path.GetFileName(new Uri(installerUrl, UriKind.Absolute).AbsolutePath);
        var installerPath = Path.Combine(_paths.CacheDirectory, LoaderKinds.ToId(loaderKind), installerName);

        await DownloadBatchAsync(
                InstallStage.Loader,
                "install.stage.loader",
                [CreateRequest(installerPath, installerUrl, null, null, installerName)],
                transaction,
                progress,
                cancellationToken,
                trackFiles: false)
            .ConfigureAwait(false);

        try
        {
            var content = ForgeInstallerReader.Read(installerPath, mavenRoot);
            var version = content.Version;
            version.Id = instance.VersionId;
            version.InheritsFrom = instance.GameVersion;
            WriteVersionJson(instance.VersionId, version, transaction);

            _logger.LogInformation(
                "已解析 {Loader} 安装器：spec {Spec}，processors {Processors}，libraries {Libraries}。",
                LoaderKinds.ToId(loaderKind),
                content.Profile.Spec,
                content.Profile.Processors.Count,
                content.Profile.Libraries.Count);

            return Result<ForgeInstallPlan>.Ok(new ForgeInstallPlan(
                VersionMerger.Merge(baseVersion, version),
                content.Profile,
                installerPath,
                mavenRoot));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
        {
            _logger.LogError(exception, "解析安装器失败：{Path}", installerPath);
            return Result<ForgeInstallPlan>.Fail(
                "install.loader_failed",
                _localizer.Get("install.error.loaderFailed", LoaderKinds.ToId(loaderKind)));
        }
    }

    private async Task<DownloadBatchResult> InstallForgeProcessorLibrariesAsync(
        ForgeInstallPlan plan,
        InstallTransaction transaction,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var requests = new List<DownloadRequest>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in plan.Profile.Libraries)
        {
            var artifact = library.Downloads?.Artifact;
            var relativePath = artifact?.Path ?? MavenCoordinates.GetPath(library.Name);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var url = artifact?.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                url = library.Url;
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                url = LibraryRules.ResolveArtifactUrl(url, relativePath);
            }
            else
            {
                url = new Uri(
                    new Uri(plan.MavenRoot.EndsWith('/') ? plan.MavenRoot : plan.MavenRoot + "/"),
                    relativePath.Replace('\\', '/')).ToString();
            }

            var destination = Path.Combine(
                _paths.LibrariesDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!seen.Add(destination))
            {
                continue;
            }

            requests.Add(CreateRequest(
                destination,
                url,
                artifact?.Sha1,
                artifact?.Size,
                Path.GetFileName(relativePath)));
        }

        return await DownloadBatchAsync(
                InstallStage.Processors,
                "install.stage.processors",
                requests,
                transaction,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Result<string>> ResolveInstallJavaAsync(
        GameInstance instance,
        MinecraftVersion version,
        CancellationToken cancellationToken)
    {
        var selection = await _javaSelection.ResolveAsync(instance, version, cancellationToken).ConfigureAwait(false);
        if (selection.Runtime is null)
        {
            return Result<string>.Fail("install.java_missing", _localizer.Get("install.error.javaMissing"));
        }

        return Result<string>.Ok(selection.Runtime.ExecutablePath);
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
        InstallTransaction transaction,
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
                transaction,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DownloadBatchResult> InstallLibrariesAsync(
        MinecraftVersion version,
        InstallTransaction transaction,
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
                transaction,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DownloadBatchResult?> InstallAssetsAsync(
        MinecraftVersion version,
        InstallTransaction transaction,
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
                transaction,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DownloadBatchResult> InstallLoggingAsync(
        MinecraftVersion version,
        InstallTransaction transaction,
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
                transaction,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Result<(DownloadBatchResult Result, string Version)>> InstallFabricApiAsync(
        GameInstance instance,
        InstallTransaction transaction,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, InstallStage.FabricApi, "install.stage.fabricApi", 0, 0, 0, 0, null);

        var versions = await _catalog
            .GetFabricApiVersionsAsync(instance.GameVersion, cancellationToken)
            .ConfigureAwait(false);

        if (!versions.IsSuccess)
        {
            return Result<(DownloadBatchResult, string)>.Fail(versions.Error!);
        }

        var selected = string.IsNullOrWhiteSpace(instance.FabricApiVersion)
            ? versions.Value!.FirstOrDefault()
            : versions.Value!.FirstOrDefault(item =>
                string.Equals(item.Version, instance.FabricApiVersion, StringComparison.OrdinalIgnoreCase));

        if (selected is null)
        {
            return Result<(DownloadBatchResult, string)>.Fail(
                "install.fabric_api_missing",
                _localizer.Get("install.error.fabricApiMissing", instance.GameVersion));
        }

        var destination = Path.Combine(
            InstancePaths.GetGameDirectory(_paths, instance),
            "mods",
            selected.FileName);
        transaction.TrackDirectory(Path.GetDirectoryName(destination)!);

        var request = new DownloadRequest(
            destination,
            [new Uri(selected.DownloadUrl)],
            selected.Sha1,
            HashingAlgorithm.Sha1,
            selected.Size,
            selected.FileName);

        var result = await DownloadBatchAsync(
                InstallStage.FabricApi,
                "install.stage.fabricApi",
                [request],
                transaction,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<(DownloadBatchResult, string)>.Ok((result, selected.Version));
    }

    private async Task<Result<(DownloadBatchResult Result, string Version)>> InstallOptiFineAsync(
        GameInstance instance,
        InstallTransaction transaction,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, InstallStage.OptiFine, "install.stage.optifine", 0, 0, 0, 0, null);

        var versions = await _catalog
            .GetOptiFineVersionsAsync(instance.GameVersion, cancellationToken)
            .ConfigureAwait(false);

        if (!versions.IsSuccess)
        {
            return Result<(DownloadBatchResult, string)>.Fail(versions.Error!);
        }

        var selected = versions.Value!.FirstOrDefault(item =>
            string.Equals(item.Key, instance.OptiFineVersion, StringComparison.OrdinalIgnoreCase));

        if (selected is null)
        {
            return Result<(DownloadBatchResult, string)>.Fail(
                "install.optifine_missing",
                _localizer.Get("install.error.optifineMissing", instance.OptiFineVersion ?? string.Empty));
        }

        var url = $"{OptiFineRoot}/{Uri.EscapeDataString(instance.GameVersion)}/" +
                  $"{Uri.EscapeDataString(selected.Type)}/{Uri.EscapeDataString(selected.Patch)}";
        var destination = Path.Combine(
            InstancePaths.GetGameDirectory(_paths, instance),
            "mods",
            selected.FileName);
        transaction.TrackDirectory(Path.GetDirectoryName(destination)!);

        var request = new DownloadRequest(
            destination,
            [new Uri(url)],
            DisplayName: selected.FileName);

        var result = await DownloadBatchAsync(
                InstallStage.OptiFine,
                "install.stage.optifine",
                [request],
                transaction,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<(DownloadBatchResult, string)>.Ok((result, instance.OptiFineVersion!));
    }

    private DownloadRequest CreateRequest(
        string destination,
        string url,
        string? hash,
        long? size,
        string displayName)
    {
        var candidates = _sources.GetCandidates(url);
        if (candidates.Count == 0 && Uri.TryCreate(url, UriKind.Absolute, out var direct))
        {
            // 第三方 Maven / Modrinth / BMCLAPI 等地址没有镜像映射，仍应直接下载。
            candidates = [direct];
        }

        return new DownloadRequest(destination, candidates, hash, HashingAlgorithm.Sha1, size, displayName);
    }

    private async Task<DownloadBatchResult> DownloadBatchAsync(
        InstallStage stage,
        string messageKey,
        IReadOnlyList<DownloadRequest> requests,
        InstallTransaction transaction,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken,
        bool trackFiles = true)
    {
        if (requests.Count == 0)
        {
            return new DownloadBatchResult(0, 0, 0);
        }

        if (trackFiles)
        {
            foreach (var request in requests)
            {
                transaction.TrackFile(request.DestinationPath);
            }
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
                download.CurrentFile)
            {
                BytesPerSecond = download.BytesPerSecond,
                IsPaused = download.IsPaused,
            }));

        return await _downloads
            .DownloadAsync(requests, forwarding, cancellationToken)
            .ConfigureAwait(false);
    }

    private void WriteVersionJson(string versionId, MinecraftVersion version, InstallTransaction transaction)
    {
        var path = VersionFilePlanner.GetVersionJsonPath(_paths, versionId);
        transaction.TrackFileWithBackup(path);
        transaction.TrackDirectory(Path.GetDirectoryName(path)!);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(version, EclJson.Default));
        File.Move(temporary, path, overwrite: true);
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

    /// <summary>
    /// 安装进度按阶段映射到任务中心：字节与项目数量都是当前阶段的值，
    /// 阶段切换由稳定的 StageId 与用户可读的 StageText 表达。
    /// </summary>
    private static OperationProgressUpdate MapInstallProgress(InstallProgress progress) => new()
    {
        StageId = InstallStageId(progress.Stage),
        StageText = progress.Message,
        CurrentItem = progress.CurrentFile,
        Completed = progress.CompletedFiles,
        Total = progress.TotalFiles > 0 ? progress.TotalFiles : null,
        BytesDownloaded = progress.CompletedBytes,
        TotalBytes = progress.TotalBytes > 0 ? progress.TotalBytes : null,
        BytesPerSecond = progress.BytesPerSecond > 0 ? progress.BytesPerSecond : null,
        IsPaused = progress.IsPaused,
    };

    private static string InstallStageId(InstallStage stage) => stage switch
    {
        InstallStage.Manifest => "install.manifest",
        InstallStage.Version => "install.version",
        InstallStage.Loader => "install.loader",
        InstallStage.Processors => "install.processors",
        InstallStage.Client => "install.client",
        InstallStage.Libraries => "install.libraries",
        InstallStage.Assets => "install.assets",
        InstallStage.Logging => "install.logging",
        InstallStage.FabricApi => "install.fabric-api",
        InstallStage.OptiFine => "install.optifine",
        InstallStage.Completed => "install.completed",
        _ => "install",
    };

    private static OperationError ToOperationError(EclError error) =>
        OperationErrors.FromCode(error.Code, error.Message, error.Detail);

    private sealed record ForgeInstallPlan(
        MinecraftVersion Version,
        ForgeInstallProfile Profile,
        string InstallerPath,
        string MavenRoot);

    private sealed class ForwardingProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
