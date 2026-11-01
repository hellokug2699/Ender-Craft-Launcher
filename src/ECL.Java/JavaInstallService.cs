// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Localization;
using Ecl.Download;
using Ecl.Operations;
using Microsoft.Extensions.Logging;

namespace Ecl.Java;

public sealed class JavaInstallService : IJavaInstallService
{
    private readonly IDownloadService _downloads;
    private readonly DownloadSourceResolver _sources;
    private readonly IJavaLocator _locator;
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly IReadOnlyDictionary<string, IJavaRuntimeProvider> _providers;
    private readonly ILocalizer _localizer;
    private readonly IOperationService? _operations;
    private readonly ILogger<JavaInstallService> _logger;

    public JavaInstallService(
        IDownloadService downloads,
        DownloadSourceResolver sources,
        IJavaLocator locator,
        IAppPaths paths,
        ISettingsService settings,
        IEnumerable<IJavaRuntimeProvider> providers,
        ILocalizer localizer,
        ILogger<JavaInstallService> logger,
        IOperationService? operations = null)
    {
        _downloads = downloads;
        _sources = sources;
        _locator = locator;
        _paths = paths;
        _settings = settings;
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _localizer = localizer;
        _operations = operations;
        _logger = logger;
    }

    public async Task<Result<JavaRuntime>> EnsureAsync(
        JavaRequirement requirement,
        IProgress<JavaInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _locator.FindAllAsync(cancellationToken).ConfigureAwait(false);
        var selected = JavaSelector.Select(existing, requirement);
        if (selected is not null)
        {
            return Result<JavaRuntime>.Ok(selected);
        }

        return await DownloadAsync(requirement, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<JavaRuntime>> DownloadAsync(
        JavaRequirement requirement,
        IProgress<JavaInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var providerId = _settings.Get<string>("java.runtimeProvider");
        IJavaRuntimeProvider? provider = null;
        if (!string.IsNullOrWhiteSpace(providerId) &&
            !string.Equals(providerId, "mojang", StringComparison.OrdinalIgnoreCase))
        {
            if (!_providers.TryGetValue(providerId, out provider))
            {
                return Result<JavaRuntime>.Fail(
                    "java.provider_unsupported",
                    _localizer.Get("java.error.providerUnsupported", providerId));
            }
        }

        // Java 下载只登记一个任务：内置下载批次复用该任务，不再产生重复条目。
        using var operation = new OperationProgressBridge<JavaInstallProgress>(
            _operations,
            new OperationStart
            {
                Kind = OperationKind.Java,
                Title = _localizer.Get("java.task.title", requirement.MinimumMajor),
                StageId = "java",
                Source = provider?.DisplayName ?? "Mojang",
                CanCancel = true,
                CanRetry = true,
            },
            progress,
            MapJavaProgress);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = cancellation.Token;
        operation.AttachControl(cancellation, retryToken => DownloadAsync(requirement, progress, retryToken));

        try
        {
            var result = provider is null
                ? await DownloadFromMojangAsync(requirement, operation, cancellationToken).ConfigureAwait(false)
                : await DownloadFromProviderAsync(provider, requirement, operation, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                operation.Complete();
            }
            else
            {
                operation.Fail(OperationErrors.FromCode(result.Error!.Code, result.Error.Message, result.Error.Detail));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            operation.Fail(OperationErrors.FromException("java.install_failed", exception));
            throw;
        }
    }

    private async Task<Result<JavaRuntime>> DownloadFromMojangAsync(
        JavaRequirement requirement,
        IProgress<JavaInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Report(progress, JavaInstallStage.Index, "java.stage.index", 0, 0, 0, 0, null);

            var indexJson = await _downloads
                .DownloadTextAsync(_sources.GetCandidates(JavaPlatform.OfficialRuntimeIndexUrl), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var index = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<JavaRuntimeIndexEntry>>>>(
                indexJson,
                EclJson.Default);

            if (index is null)
            {
                return Result<JavaRuntime>.Fail("java.index_invalid", _localizer.Get("java.error.indexInvalid"));
            }

            if (!JavaPlatform.TryGetIndexPlatformKey(out var platformKey) ||
                !index.TryGetValue(platformKey, out var components) ||
                components.Count == 0)
            {
                return Result<JavaRuntime>.Fail(
                    "java.platform_unsupported",
                    _localizer.Get("java.error.platformUnsupported", JavaPlatform.DescribeCurrentPlatform()));
            }

            var selected = SelectComponent(components, requirement);
            if (selected is null)
            {
                return Result<JavaRuntime>.Fail(
                    "java.component_not_found",
                    _localizer.Get("java.error.componentNotFound", requirement.Describe()));
            }

            var (component, entry, entryVersion) = selected.Value;
            if (string.IsNullOrWhiteSpace(component) || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return Result<JavaRuntime>.Fail("java.component_invalid", _localizer.Get("java.error.componentInvalid"));
            }

            if (string.IsNullOrWhiteSpace(entry.Manifest?.Url))
            {
                return Result<JavaRuntime>.Fail("java.manifest_missing", _localizer.Get("java.error.manifestMissing"));
            }

            Report(progress, JavaInstallStage.Manifest, "java.stage.manifest", 0, 0, 0, 0, null);

            var manifestJson = await _downloads
                .DownloadTextAsync(
                    _sources.GetCandidates(entry.Manifest.Url),
                    entry.Manifest.Sha1,
                    entry.Manifest.Size,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var manifest = JsonSerializer.Deserialize<JavaRuntimeManifest>(manifestJson, EclJson.Default);
            if (manifest is null || manifest.Files.Count == 0)
            {
                return Result<JavaRuntime>.Fail("java.manifest_invalid", _localizer.Get("java.error.manifestInvalid"));
            }

            var runtimeDirectory = Path.Combine(_paths.JavaDirectory, component);
            Directory.CreateDirectory(runtimeDirectory);
            var root = Path.GetFullPath(runtimeDirectory) + Path.DirectorySeparatorChar;

            var requests = new List<DownloadRequest>();
            var executableFiles = new List<string>();

            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveDestination(root, runtimeDirectory, file.Key);
                if (destination is null)
                {
                    continue;
                }

                if (string.Equals(file.Value.Type, "directory", StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                if (string.Equals(file.Value.Type, "link", StringComparison.OrdinalIgnoreCase))
                {
                    CreateLink(file.Key, file.Value.Target, destination);
                    continue;
                }

                var raw = file.Value.Downloads?.Raw;
                if (raw is null || string.IsNullOrWhiteSpace(raw.Url))
                {
                    continue;
                }

                requests.Add(new DownloadRequest(
                    destination,
                    _sources.GetCandidates(raw.Url),
                    raw.Sha1,
                    HashingAlgorithm.Sha1,
                    raw.Size,
                    file.Key));

                if (file.Value.Executable)
                {
                    executableFiles.Add(destination);
                }
            }

            if (requests.Count == 0)
            {
                return Result<JavaRuntime>.Fail("java.files_empty", _localizer.Get("java.error.filesEmpty"));
            }

            var totalFiles = requests.Count;
            var totalBytes = requests.Sum(request => request.Size ?? 0);
            Report(progress, JavaInstallStage.Files, "java.stage.files", 0, totalFiles, 0, totalBytes, null);

            var forwarding = new ForwardingProgress<DownloadProgress>(update =>
                progress?.Report(new JavaInstallProgress(
                    JavaInstallStage.Files,
                    _localizer.Get("java.stage.files"),
                    update.CompletedFiles,
                    update.TotalFiles,
                    update.CompletedBytes,
                    update.TotalBytes,
                    update.CurrentFile)
                {
                    BytesPerSecond = update.BytesPerSecond,
                    IsPaused = update.IsPaused,
                }));

            var result = await _downloads
                .DownloadAsync(requests, forwarding, cancellationToken)
                .ConfigureAwait(false);

            MarkExecutable(executableFiles);

            var runtime = _locator.FromPath(runtimeDirectory);
            if (runtime is null)
            {
                return Result<JavaRuntime>.Fail("java.runtime_invalid", _localizer.Get("java.error.runtimeInvalid"));
            }

            if (runtime.Version is null && entryVersion is not null)
            {
                runtime = runtime with { Version = entryVersion };
            }

            Report(
                progress,
                JavaInstallStage.Completed,
                "java.stage.completed",
                result.DownloadedFiles + result.SkippedFiles,
                result.DownloadedFiles + result.SkippedFiles,
                result.Bytes,
                result.Bytes,
                null);

            _logger.LogInformation(
                "已安装 Java 运行时 {Component}（{Version}）到 {Directory}。",
                component,
                runtime.Version?.Raw ?? "未知版本",
                runtimeDirectory);

            return Result<JavaRuntime>.Ok(runtime);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadException exception)
        {
            _logger.LogError(exception, "下载 Java 运行时失败。");
            return Result<JavaRuntime>.Fail(
                "java.download_failed",
                _localizer.Get("java.error.downloadFailed", exception.Message),
                string.Join("; ", exception.Failures.Take(5)));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(exception, "安装 Java 运行时失败。");
            return Result<JavaRuntime>.Fail(
                "java.install_failed",
                _localizer.Get("java.error.installFailed", exception.Message));
        }
    }

    private async Task<Result<JavaRuntime>> DownloadFromProviderAsync(
        IJavaRuntimeProvider provider,
        JavaRequirement requirement,
        IProgress<JavaInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            Report(progress, JavaInstallStage.Provider, "java.stage.provider", 0, 0, 0, 0, provider.DisplayName);

            var packageResult = await provider.ResolveAsync(requirement, cancellationToken).ConfigureAwait(false);
            if (!packageResult.IsSuccess)
            {
                return Result<JavaRuntime>.Fail(packageResult.Error!);
            }

            var package = packageResult.Value!;
            var component = SanitizeComponent(package.Component);
            var runtimeId = $"{provider.Id}-{component}";
            var destination = Path.Combine(_paths.JavaDirectory, runtimeId);

            var cacheDirectory = Path.Combine(_paths.CacheDirectory, "java");
            Directory.CreateDirectory(cacheDirectory);
            var archiveName = Path.GetFileName(package.FileName);
            if (string.IsNullOrWhiteSpace(archiveName))
            {
                archiveName = $"java-{requirement.MinimumMajor}.zip";
            }

            var archivePath = Path.Combine(cacheDirectory, $"{runtimeId}-{archiveName}");
            var hash = !string.IsNullOrWhiteSpace(package.Sha256) ? package.Sha256 : package.Sha1;
            var algorithm = !string.IsNullOrWhiteSpace(package.Sha256)
                ? HashingAlgorithm.Sha256
                : HashingAlgorithm.Sha1;

            var request = new DownloadRequest(
                archivePath,
                [new Uri(package.DownloadUrl)],
                hash,
                algorithm,
                package.Size,
                package.DisplayName);

            Report(
                progress,
                JavaInstallStage.Download,
                "java.stage.download",
                0,
                1,
                0,
                package.Size ?? 0,
                package.DisplayName);

            var forwarding = new ForwardingProgress<DownloadProgress>(update =>
                progress?.Report(new JavaInstallProgress(
                    JavaInstallStage.Download,
                    _localizer.Get("java.stage.download"),
                    update.CompletedFiles,
                    update.TotalFiles,
                    update.CompletedBytes,
                    update.TotalBytes,
                    update.CurrentFile)
                {
                    BytesPerSecond = update.BytesPerSecond,
                    IsPaused = update.IsPaused,
                }));

            await _downloads.DownloadAsync([request], forwarding, cancellationToken).ConfigureAwait(false);

            Report(
                progress,
                JavaInstallStage.Extract,
                "java.stage.extract",
                0,
                0,
                0,
                0,
                package.DisplayName);

            var extracted = await JavaArchiveInstaller
                .ExtractAsync(archivePath, package.ArchiveKind, destination, cancellationToken)
                .ConfigureAwait(false);
            if (!extracted.IsSuccess)
            {
                return Result<JavaRuntime>.Fail(extracted.Error!);
            }

            var runtime = _locator.FromPath(destination);
            if (runtime is null)
            {
                return Result<JavaRuntime>.Fail(
                    "java.runtime_invalid",
                    _localizer.Get("java.error.runtimeInvalid"));
            }

            if (runtime.Version is null && package.Version is not null)
            {
                runtime = runtime with { Version = package.Version };
            }

            try
            {
                File.Delete(archivePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "无法删除 Java 安装包缓存：{Path}", archivePath);
            }

            Report(
                progress,
                JavaInstallStage.Completed,
                "java.stage.completed",
                1,
                1,
                0,
                0,
                null);

            _logger.LogInformation(
                "已安装第三方 Java 运行时 {Provider} {Version} 到 {Directory}。",
                provider.DisplayName,
                runtime.Version?.Raw ?? package.Version?.Raw ?? "未知版本",
                destination);

            return Result<JavaRuntime>.Ok(runtime);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadException exception)
        {
            _logger.LogError(exception, "下载第三方 Java 运行时失败。");
            return Result<JavaRuntime>.Fail(
                "java.download_failed",
                _localizer.Get("java.error.downloadFailed", exception.Message),
                string.Join("; ", exception.Failures.Take(5)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidOperationException or UriFormatException)
        {
            _logger.LogError(exception, "安装第三方 Java 运行时失败。");
            return Result<JavaRuntime>.Fail(
                "java.install_failed",
                _localizer.Get("java.error.installFailed", exception.Message));
        }
    }

    private static string SanitizeComponent(string component)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(component
            .Select(character => invalid.Contains(character) || character is '/' or '\\' ? '-' : character)
            .ToArray())
            .Trim();
        return sanitized.Length == 0 ? "java" : sanitized;
    }

    private static (string Component, JavaRuntimeIndexEntry Entry, JavaVersion? Version)? SelectComponent(
        Dictionary<string, List<JavaRuntimeIndexEntry>> components,
        JavaRequirement requirement)
    {
        if (!string.IsNullOrWhiteSpace(requirement.Component) &&
            components.TryGetValue(requirement.Component, out var preferred) &&
            preferred.Count > 0 &&
            preferred[0].Manifest?.Url is not null)
        {
            var preferredVersion = JavaVersion.TryParse(preferred[0].Version?.Name);

            // Minecraft 声明的组件可能低于实例的有效要求（例如 Mod 要求 Java 25），
            // 此时继续按主版本挑选，不能为了“组件名匹配”安装一个不兼容的运行时。
            if (preferredVersion is null || requirement.IsCompatible(preferredVersion))
            {
                return (
                    requirement.Component,
                    preferred[0],
                    preferredVersion);
            }
        }

        var candidates = components
            .Where(pair => !string.Equals(pair.Key, "minecraft-java-exe", StringComparison.OrdinalIgnoreCase))
            .Where(pair => pair.Value.Count > 0 && pair.Value[0].Manifest?.Url is not null)
            .Select(pair => (
                Component: pair.Key,
                Entry: pair.Value[0],
                Version: JavaVersion.TryParse(pair.Value[0].Version?.Name)))
            .Where(candidate => requirement.IsCompatible(candidate.Version))
            .OrderBy(candidate => candidate.Version!.Major == requirement.MinimumMajor ? 0 : 1)
            .ThenBy(candidate => candidate.Version!.Major)
            .ThenByDescending(candidate => candidate.Version!.Minor)
            .ThenByDescending(candidate => candidate.Version!.Patch)
            .ToArray();

        return candidates.Length == 0 ? null : candidates[0];
    }

    private static string? ResolveDestination(string root, string runtimeDirectory, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0)
        {
            return null;
        }

        var destination = Path.GetFullPath(
            Path.Combine(runtimeDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));

        return destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? destination : null;
    }

    private void CreateLink(string relativePath, string? target, string destination)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(destination) || Directory.Exists(destination))
            {
                File.Delete(destination);
            }
            else
            {
                try
                {
                    File.Delete(destination);
                }
                catch (FileNotFoundException)
                {
                    // 不存在即可。
                }
            }

            File.CreateSymbolicLink(destination, target.Replace('\\', '/'));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogWarning(exception, "创建 Java 运行时符号链接失败：{Path} -> {Target}", relativePath, target);
        }
    }

    private static void MarkExecutable(IEnumerable<string> files)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode mode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        foreach (var file in files)
        {
            try
            {
                File.SetUnixFileMode(file, mode);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // 无法设置执行位时继续；Java 仍可能可用。
            }
        }
    }

    private void Report(
        IProgress<JavaInstallProgress>? progress,
        JavaInstallStage stage,
        string messageKey,
        int completedFiles,
        int totalFiles,
        long completedBytes,
        long totalBytes,
        string? currentFile)
    {
        progress?.Report(new JavaInstallProgress(
            stage,
            _localizer.Get(messageKey),
            completedFiles,
            totalFiles,
            completedBytes,
            totalBytes,
            currentFile));
    }

    /// <summary>
    /// Java 安装进度映射到任务中心：索引、清单、下载、解压等阶段使用稳定 StageId，
    /// 下载阶段提供字节与速度。
    /// </summary>
    private static OperationProgressUpdate MapJavaProgress(JavaInstallProgress progress) => new()
    {
        StageId = JavaStageId(progress.Stage),
        StageText = progress.Message,
        CurrentItem = progress.CurrentFile,
        Completed = progress.CompletedFiles,
        Total = progress.TotalFiles > 0 ? progress.TotalFiles : null,
        BytesDownloaded = progress.CompletedBytes,
        TotalBytes = progress.TotalBytes > 0 ? progress.TotalBytes : null,
        BytesPerSecond = progress.BytesPerSecond > 0 ? progress.BytesPerSecond : null,
        IsPaused = progress.IsPaused,
    };

    private static string JavaStageId(JavaInstallStage stage) => stage switch
    {
        JavaInstallStage.Index => "java.index",
        JavaInstallStage.Manifest => "java.manifest",
        JavaInstallStage.Provider => "java.provider",
        JavaInstallStage.Download => "java.download",
        JavaInstallStage.Extract => "java.extract",
        JavaInstallStage.Files => "java.files",
        JavaInstallStage.Completed => "java.completed",
        _ => "java",
    };

    private sealed class ForwardingProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
