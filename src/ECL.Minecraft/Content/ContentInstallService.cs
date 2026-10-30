// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text.Json;
using Ecl.Core;
using Ecl.Download;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Modrinth;
using Ecl.Minecraft.Mods;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Content;

public interface IContentInstallService
{
    /// <summary>安装到指定实例。整合包会先下载归档再交给整合包导入流程。</summary>
    Task<Result<ContentInstallReport>> InstallAsync(
        string instanceId,
        ContentHit hit,
        IProgress<ContentProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>只下载到启动器下载目录，不需要实例。</summary>
    Task<Result<ContentDownloadReport>> DownloadAsync(
        ContentHit hit,
        IProgress<ContentProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>下载整合包归档并导入到指定实例。</summary>
    Task<Result<ModpackImportResult>> ImportModpackAsync(
        string instanceId,
        ContentHit hit,
        IProgress<ContentProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>下载整合包，根据包内元数据自动创建实例并导入。</summary>
    Task<Result<ModpackImportResult>> CreateInstanceAndImportModpackAsync(
        ContentHit hit,
        IProgress<ContentProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 统一的“下载 / 安装”执行器。CurseForge Mod 复用已有的依赖解析逻辑；
/// Modrinth 文件自行解析版本与必需依赖；整合包统一落盘为归档后再导入。
/// </summary>
public sealed class ContentInstallService(
    IInstanceService instances,
    IModpackService modpacks,
    ICurseForgeClient curseForgeClient,
    ICurseForgeService curseForgeService,
    IModrinthClient modrinth,
    IDownloadService downloads,
    IAppPaths paths,
    ILogger<ContentInstallService> logger) : IContentInstallService
{
    private const int MaxDependencyDepth = 4;
    private const int MaxDependencyFiles = 128;
    private const string ModrinthHost = "modrinth.com";

    public async Task<Result<ContentInstallReport>> InstallAsync(
        string instanceId,
        ContentHit hit,
        IProgress<ContentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ContentInstallReport>.Fail("instance.not_found", "实例不存在。");
        }

        if (hit.Kind == ContentKind.ModPack)
        {
            var import = await ImportModpackAsync(instanceId, hit, progress, cancellationToken)
                .ConfigureAwait(false);
            return import.IsSuccess
                ? Result<ContentInstallReport>.Ok(new ContentInstallReport(
                    import.Value!.Name,
                    hit.Source,
                    ContentKind.ModPack,
                    instanceId,
                    import.Value.ArchivePath,
                    [import.Value.ArchivePath],
                    import.Value.Warnings))
                : Result<ContentInstallReport>.Fail(import.Error!);
        }

        if (hit.Source == ContentSource.CurseForge && hit.Kind == ContentKind.Mod)
        {
            progress?.Report(new ContentProgress("resolve", "正在解析 CurseForge Mod…"));
            var forwarding = new ForwardingProgress<CurseForgeProgress>(update =>
                progress?.Report(new ContentProgress(update.Stage, update.Message, update.Completed, update.Total)));
            var result = await curseForgeService
                .InstallAsync(instanceId, hit.Id, progress: forwarding, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return result.IsSuccess
                ? Result<ContentInstallReport>.Ok(new ContentInstallReport(
                    result.Value!.ModName,
                    ContentSource.CurseForge,
                    ContentKind.Mod,
                    instanceId,
                    result.Value.FileName,
                    result.Value.InstalledFiles,
                    result.Value.Warnings))
                : Result<ContentInstallReport>.Fail(result.Error!);
        }

        progress?.Report(new ContentProgress("resolve", "正在解析文件…"));
        var resolved = await ResolveFilesAsync(
                hit,
                instance.GameVersion,
                ContentKinds.UsesLoader(hit.Kind) ? instance.Loader : null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return Result<ContentInstallReport>.Fail(resolved.Error!);
        }

        var targetDirectory = Path.Combine(
            paths.GetInstanceGameDirectory(instance.Id),
            ContentKinds.FolderName(hit.Kind));
        var downloadResult = await DownloadFilesAsync(
                resolved.Value!,
                targetDirectory,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (!downloadResult.IsSuccess)
        {
            return Result<ContentInstallReport>.Fail(downloadResult.Error!);
        }

        var installedFiles = downloadResult.Value!;
        logger.LogInformation(
            "已从 {Source} 安装 {Kind} {Name} 到实例 {InstanceId}：{Files}",
            hit.Source,
            hit.Kind,
            hit.Name,
            instanceId,
            string.Join(", ", installedFiles));

        return Result<ContentInstallReport>.Ok(new ContentInstallReport(
            hit.Name,
            hit.Source,
            hit.Kind,
            instanceId,
            installedFiles.FirstOrDefault(),
            installedFiles,
            resolved.Value!.Warnings));
    }

    public async Task<Result<ContentDownloadReport>> DownloadAsync(
        ContentHit hit,
        IProgress<ContentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveFilesAsync(hit, null, null, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return Result<ContentDownloadReport>.Fail(resolved.Error!);
        }

        var primary = resolved.Value!.Files[0];
        var directory = Path.Combine(paths.DownloadsDirectory, ContentKinds.FolderName(hit.Kind));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, primary.FileName);

        // 无实例下载时也把 Modrinth 的必需依赖一起取回，避免用户拿到一个缺依赖的 Mod。
        var downloadResult = await DownloadFilesAsync(
                resolved.Value,
                directory,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (!downloadResult.IsSuccess)
        {
            return Result<ContentDownloadReport>.Fail(downloadResult.Error!);
        }

        return Result<ContentDownloadReport>.Ok(new ContentDownloadReport(
            hit.Name,
            hit.Kind,
            destination,
            primary.Size ?? (File.Exists(destination) ? new FileInfo(destination).Length : 0)));
    }

    public async Task<Result<ModpackImportResult>> ImportModpackAsync(
        string instanceId,
        ContentHit hit,
        IProgress<ContentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ModpackImportResult>.Fail("instance.not_found", "实例不存在。");
        }

        var archive = await DownloadModpackArchiveAsync(
                hit,
                instance.GameVersion,
                instance.Loader,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (!archive.IsSuccess)
        {
            return Result<ModpackImportResult>.Fail(archive.Error!);
        }

        return await ImportArchiveAsync(instance, archive.Value!, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<ModpackImportResult>> CreateInstanceAndImportModpackAsync(
        ContentHit hit,
        IProgress<ContentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var archive = await DownloadModpackArchiveAsync(hit, null, null, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!archive.IsSuccess)
        {
            return Result<ModpackImportResult>.Fail(archive.Error!);
        }

        progress?.Report(new ContentProgress("inspect", "正在读取整合包信息…"));
        var metadata = ModpackArchiveInspector.Read(archive.Value!);
        if (!metadata.IsSuccess)
        {
            return Result<ModpackImportResult>.Fail(metadata.Error!);
        }

        var pack = metadata.Value!;
        if (string.IsNullOrWhiteSpace(pack.GameVersion))
        {
            return Result<ModpackImportResult>.Fail(
                "modpack.game_version_unknown",
                "整合包没有声明 Minecraft 版本，无法自动创建实例；请先创建实例后导入。");
        }

        var name = string.IsNullOrWhiteSpace(pack.Name) ? hit.Name : pack.Name;
        var createResult = await instances
            .CreateAsync(
                TrimName(name),
                pack.GameVersion,
                pack.Loader,
                pack.LoaderVersion,
                fabricApiVersion: null,
                optiFineVersion: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!createResult.IsSuccess)
        {
            return Result<ModpackImportResult>.Fail(createResult.Error!);
        }

        progress?.Report(new ContentProgress("import", "正在导入整合包…"));
        var import = await ImportArchiveAsync(createResult.Value!, archive.Value!, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!import.IsSuccess)
        {
            return import;
        }

        return Result<ModpackImportResult>.Ok(import.Value! with { InstanceId = createResult.Value!.Id });
    }

    private async Task<Result<ModpackImportResult>> ImportArchiveAsync(
        GameInstance instance,
        string archivePath,
        IProgress<ContentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var forwarding = new ForwardingProgress<ModpackProgress>(update =>
            progress?.Report(new ContentProgress(update.Stage, update.Message, update.Completed, update.Total)));
        var result = await modpacks
            .ImportAsync(instance.Id, archivePath, forwarding, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Result<ModpackImportResult>.Fail(result.Error!);
        }

        var report = result.Value!;
        return Result<ModpackImportResult>.Ok(new ModpackImportResult(
            report.Name,
            report.Version,
            report.DownloadedFiles,
            report.SkippedFiles,
            report.OverrideFiles,
            report.Warnings,
            archivePath,
            instance.Id));
    }

    private async Task<Result<string>> DownloadModpackArchiveAsync(
        ContentHit hit,
        string? gameVersion,
        string? loader,
        IProgress<ContentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveFilesAsync(hit, gameVersion, loader, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return Result<string>.Fail(resolved.Error!);
        }

        var primary = resolved.Value!.Files[0];
        var directory = Path.Combine(paths.DownloadsDirectory, "modpacks");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, primary.FileName);

        var downloadResult = await DownloadFilesAsync(
                new ResolvedDownload([primary], []),
                directory,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        return downloadResult.IsSuccess
            ? Result<string>.Ok(destination)
            : Result<string>.Fail(downloadResult.Error!);
    }

    private async Task<Result<ResolvedDownload>> ResolveFilesAsync(
        ContentHit hit,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        var primary = hit.Source switch
        {
            ContentSource.Modrinth => await ResolveModrinthAsync(hit, gameVersion, loader, cancellationToken)
                .ConfigureAwait(false),
            ContentSource.CurseForge => await ResolveCurseForgeAsync(hit, gameVersion, loader, cancellationToken)
                .ConfigureAwait(false),
            _ => Result<RemoteFile>.Fail("content.invalid_source", "未知的下载来源。"),
        };
        if (!primary.IsSuccess)
        {
            return Result<ResolvedDownload>.Fail(primary.Error!);
        }

        var files = new List<RemoteFile> { primary.Value! };
        var warnings = new List<string>();

        // Modrinth Mod 的必需依赖按项目递归补齐；CurseForge 交给 CurseForgeService。
        if (hit.Source == ContentSource.Modrinth && hit.Kind == ContentKind.Mod)
        {
            await AddModrinthDependenciesAsync(
                    primary.Value!,
                    gameVersion,
                    loader,
                    files,
                    warnings,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hit.Id },
                    depth: 0,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return Result<ResolvedDownload>.Ok(new ResolvedDownload(files, warnings));
    }

    private async Task<Result<RemoteFile>> ResolveModrinthAsync(
        ContentHit hit,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        var versions = await modrinth
            .GetVersionsAsync(hit.Id, gameVersion, loader, cancellationToken)
            .ConfigureAwait(false);
        if (!versions.IsSuccess)
        {
            return Result<RemoteFile>.Fail(versions.Error!);
        }

        var version = PickModrinthVersion(versions.Value!);
        return ToRemoteFile(hit, version);
    }

    private static ModrinthVersion? PickModrinthVersion(IReadOnlyList<ModrinthVersion> versions) =>
        versions
            .OrderBy(version => ReleaseRank(version.VersionType))
            .ThenByDescending(version => version.DatePublished)
            .FirstOrDefault();

    private static int ReleaseRank(string? versionType) => versionType?.ToLowerInvariant() switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => 3,
    };

    private static Result<RemoteFile> ToRemoteFile(ContentHit hit, ModrinthVersion? version)
    {
        var file = version?.PrimaryFile;
        if (file is null)
        {
            return Result<RemoteFile>.Fail(
                "modrinth.no_file",
                $"{hit.Name} 没有可下载的文件。");
        }

        if (!TryCreateModrinthUri(file.Url, out var uri))
        {
            return Result<RemoteFile>.Fail(
                "modrinth.invalid_url",
                $"{hit.Name} 的文件地址不受支持：{file.Url}");
        }

        if (!IsSafeFileName(file.Filename))
        {
            return Result<RemoteFile>.Fail(
                "modrinth.invalid_file_name",
                $"{hit.Name} 返回了不安全的文件名。");
        }

        return Result<RemoteFile>.Ok(new RemoteFile(
            file.Filename,
            uri,
            file.Hashes.Sha1,
            file.Size > 0 ? file.Size : null,
            version!.Id,
            version.ProjectId));
    }

    private async Task<Result<RemoteFile>> ResolveCurseForgeAsync(
        ContentHit hit,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(hit.Id, out var projectId) || projectId <= 0)
        {
            return Result<RemoteFile>.Fail("curseforge.invalid_project", "CurseForge 项目 ID 无效。");
        }

        var mod = await curseForgeClient.GetModAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!mod.IsSuccess)
        {
            return Result<RemoteFile>.Fail(mod.Error!);
        }

        var mappedLoader = CurseForgeLoaders.FromInstanceLoader(loader);
        CurseForgeFile? chosen = null;
        var index = mod.Value!.LatestFilesIndexes
            .Where(item => item.FileId > 0)
            .Where(item => mappedLoader is null || item.ModLoader == (int)mappedLoader.Value)
            .Where(item => string.IsNullOrWhiteSpace(gameVersion) ||
                           string.Equals(item.GameVersion, gameVersion, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.ReleaseType == 1 ? 0 : 1)
            .FirstOrDefault();

        if (index is not null)
        {
            var file = await curseForgeClient
                .GetFileAsync(projectId, index.FileId, cancellationToken)
                .ConfigureAwait(false);
            if (file.IsSuccess)
            {
                chosen = file.Value;
            }
        }

        if (chosen is null)
        {
            var files = await curseForgeClient
                .GetFilesForModAsync(projectId, gameVersion, mappedLoader, 50, cancellationToken)
                .ConfigureAwait(false);
            if (!files.IsSuccess)
            {
                return Result<RemoteFile>.Fail(files.Error!);
            }

            chosen = files.Value!
                .Where(file => string.IsNullOrWhiteSpace(gameVersion) || file.SupportsGameVersion(gameVersion))
                .Where(file => mappedLoader is null || file.SupportsLoader(mappedLoader.Value))
                .OrderBy(file => file.ReleaseType == 1 ? 0 : 1)
                .ThenByDescending(file => file.FileDate)
                .FirstOrDefault();
        }

        if (chosen is null)
        {
            return Result<RemoteFile>.Fail(
                "curseforge.no_compatible_file",
                $"{hit.Name} 没有适用于当前实例的文件。");
        }

        var url = chosen.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            var resolved = await curseForgeClient
                .GetDownloadUrlAsync(projectId, chosen.Id, cancellationToken)
                .ConfigureAwait(false);
            url = resolved.IsSuccess ? resolved.Value : null;
        }

        if (!CurseForgeDownloads.TryCreateUri(url, out var uri))
        {
            return Result<RemoteFile>.Fail(
                "curseforge.distribution_restricted",
                $"{hit.Name} 没有可用的 CurseForge CDN 地址；作者可能禁止第三方分发。");
        }

        if (!IsSafeFileName(chosen.FileName))
        {
            return Result<RemoteFile>.Fail("curseforge.invalid_file_name", "CurseForge 返回了不安全的文件名。");
        }

        return Result<RemoteFile>.Ok(new RemoteFile(
            chosen.FileName,
            uri,
            chosen.Sha1,
            chosen.FileLength > 0 ? chosen.FileLength : null,
            chosen.Id.ToString(),
            projectId.ToString()));
    }

    private async Task AddModrinthDependenciesAsync(
        RemoteFile file,
        string? gameVersion,
        string? loader,
        List<RemoteFile> files,
        List<string> warnings,
        HashSet<string> visitedProjects,
        int depth,
        CancellationToken cancellationToken)
    {
        if (files.Count >= MaxDependencyFiles)
        {
            const string warning = "Modrinth 必需依赖数量超过 128 个，已停止继续解析；请手动检查整合包依赖。";
            if (!warnings.Contains(warning, StringComparer.Ordinal))
            {
                warnings.Add(warning);
            }

            return;
        }

        if (depth >= MaxDependencyDepth || string.IsNullOrWhiteSpace(file.VersionId))
        {
            if (depth >= MaxDependencyDepth)
            {
                warnings.Add("Modrinth 依赖层级超过限制，请手动检查缺失的依赖。");
            }

            return;
        }

        var version = await modrinth.GetVersionAsync(file.VersionId, cancellationToken).ConfigureAwait(false);
        if (!version.IsSuccess)
        {
            warnings.Add($"读取 {file.FileName} 的依赖失败：{version.Error!.Message}");
            return;
        }

        foreach (var dependency in version.Value!.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (files.Count >= MaxDependencyFiles)
            {
                const string warning = "Modrinth 必需依赖数量超过 128 个，已停止继续解析；请手动检查整合包依赖。";
                if (!warnings.Contains(warning, StringComparer.Ordinal))
                {
                    warnings.Add(warning);
                }

                return;
            }
            if (!string.Equals(dependency.DependencyType, "required", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(dependency.ProjectId))
            {
                continue;
            }

            if (!visitedProjects.Add(dependency.ProjectId))
            {
                continue;
            }

            ModrinthVersion? dependencyVersion = null;
            if (!string.IsNullOrWhiteSpace(dependency.VersionId))
            {
                var exact = await modrinth
                    .GetVersionAsync(dependency.VersionId, cancellationToken)
                    .ConfigureAwait(false);
                dependencyVersion = exact.IsSuccess ? exact.Value : null;
            }

            if (dependencyVersion is null)
            {
                var candidates = await modrinth
                    .GetVersionsAsync(dependency.ProjectId, gameVersion, loader, cancellationToken)
                    .ConfigureAwait(false);
                dependencyVersion = candidates.IsSuccess ? PickModrinthVersion(candidates.Value!) : null;
            }

            if (dependencyVersion is null)
            {
                warnings.Add($"依赖项目 {dependency.ProjectId} 没有适用于当前实例的版本。");
                continue;
            }

            var dependencyFile = ToRemoteFile(
                new ContentHit(
                    ContentSource.Modrinth,
                    dependency.ProjectId,
                    null,
                    dependency.FileName ?? dependency.ProjectId,
                    string.Empty,
                    null,
                    0,
                    null,
                    null,
                    null,
                    ContentKind.Mod),
                dependencyVersion);
            if (!dependencyFile.IsSuccess)
            {
                warnings.Add(dependencyFile.Error!.Message);
                continue;
            }

            if (files.Any(item => string.Equals(item.FileName, dependencyFile.Value!.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var resolvedDependency = dependencyFile.Value!;
            files.Add(resolvedDependency);
            await AddModrinthDependenciesAsync(
                    resolvedDependency,
                    gameVersion,
                    loader,
                    files,
                    warnings,
                    visitedProjects,
                    depth + 1,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<Result<IReadOnlyList<string>>> DownloadFilesAsync(
        ResolvedDownload resolved,
        string targetDirectory,
        IProgress<ContentProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);

        var requests = resolved.Files
            .Select(file => new DownloadRequest(
                Path.Combine(targetDirectory, file.FileName),
                [file.Url],
                file.Sha1,
                HashingAlgorithm.Sha1,
                file.Size,
                file.FileName))
            .ToArray();

        var forwarding = new ForwardingProgress<DownloadProgress>(update =>
            progress?.Report(new ContentProgress(
                "download",
                "正在下载…",
                update.CompletedFiles,
                update.TotalFiles)
            {
                BytesPerSecond = update.BytesPerSecond,
            }));

        try
        {
            await downloads.DownloadAsync(requests, forwarding, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadException exception)
        {
            return Result<IReadOnlyList<string>>.Fail(
                "download.failed",
                $"下载失败：{exception.Message}",
                string.Join("; ", exception.Failures.Take(5)));
        }

        return Result<IReadOnlyList<string>>.Ok(requests.Select(request => Path.GetFileName(request.DestinationPath)).ToArray());
    }

    private static bool TryCreateModrinthUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !(string.Equals(parsed.Host, ModrinthHost, StringComparison.OrdinalIgnoreCase) ||
              parsed.Host.EndsWith("." + ModrinthHost, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsSafeFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName is not "." and not ".." &&
        string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) &&
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string TrimName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length switch
        {
            0 => "整合包实例",
            > 48 => trimmed[..48],
            _ => trimmed,
        };
    }

    private sealed record RemoteFile(
        string FileName,
        Uri Url,
        string? Sha1,
        long? Size,
        string? VersionId,
        string? ProjectId);

    private sealed record ResolvedDownload(
        IReadOnlyList<RemoteFile> Files,
        IReadOnlyList<string> Warnings);

    private sealed class ForwardingProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}

/// <summary>从 .mrpack / CurseForge .zip 中读取创建实例所需的元数据。</summary>
public static class ModpackArchiveInspector
{
    public sealed record ModpackMetadata(
        string Name,
        string? GameVersion,
        string Loader,
        string? LoaderVersion,
        string? PackVersion);

    public static Result<ModpackMetadata> Read(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            return Result<ModpackMetadata>.Fail("modpack.not_found", $"整合包不存在：{archivePath}");
        }

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var modrinthIndex = archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName, "modrinth.index.json", StringComparison.OrdinalIgnoreCase));
            if (modrinthIndex is not null)
            {
                return ReadModrinth(modrinthIndex);
            }

            var manifest = archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase));
            return manifest is not null
                ? ReadCurseForge(manifest, archivePath)
                : Result<ModpackMetadata>.Fail(
                    "modpack.unsupported",
                    "不是受支持的整合包：缺少 modrinth.index.json 或 manifest.json。");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Result<ModpackMetadata>.Fail("modpack.invalid_archive", $"读取整合包失败：{exception.Message}");
        }
    }

    private static Result<ModpackMetadata> ReadModrinth(ZipArchiveEntry indexEntry)
    {
        using var document = JsonDocument.Parse(ReadText(indexEntry));
        var root = document.RootElement;
        var name = GetString(root, "name") ?? "整合包实例";
        var version = GetString(root, "versionId");
        var dependencies = root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object
            ? deps
            : default;

        var gameVersion = dependencies.ValueKind == JsonValueKind.Object
            ? GetString(dependencies, "minecraft")
            : null;

        var (loader, loaderVersion) = DetectModrinthLoader(dependencies);
        return Result<ModpackMetadata>.Ok(new ModpackMetadata(name, gameVersion, loader, loaderVersion, version));
    }

    private static (string Loader, string? Version) DetectModrinthLoader(JsonElement dependencies)
    {
        if (dependencies.ValueKind != JsonValueKind.Object)
        {
            return ("vanilla", null);
        }

        if (dependencies.TryGetProperty("fabric-loader", out var fabric))
        {
            return ("fabric", fabric.GetString());
        }

        if (dependencies.TryGetProperty("neoforge", out var neoforge))
        {
            return ("neoforge", neoforge.GetString());
        }

        if (dependencies.TryGetProperty("forge", out var forge))
        {
            return ("forge", forge.GetString());
        }

        if (dependencies.TryGetProperty("quilt-loader", out _))
        {
            return ("quilt", null);
        }

        return ("vanilla", null);
    }

    private static Result<ModpackMetadata> ReadCurseForge(ZipArchiveEntry manifestEntry, string archivePath)
    {
        using var document = JsonDocument.Parse(ReadText(manifestEntry));
        var root = document.RootElement;
        var name = GetString(root, "name") ?? Path.GetFileNameWithoutExtension(archivePath);
        var version = GetString(root, "version");
        var minecraft = root.TryGetProperty("minecraft", out var mc) && mc.ValueKind == JsonValueKind.Object
            ? mc
            : default;
        var gameVersion = minecraft.ValueKind == JsonValueKind.Object ? GetString(minecraft, "version") : null;

        var loader = "vanilla";
        string? loaderVersion = null;
        if (minecraft.ValueKind == JsonValueKind.Object &&
            minecraft.TryGetProperty("modLoaders", out var loaders) &&
            loaders.ValueKind == JsonValueKind.Array)
        {
            var primary = loaders.EnumerateArray().FirstOrDefault(item => GetBool(item, "primary") == true);
            var id = primary.ValueKind == JsonValueKind.Undefined
                ? loaders.EnumerateArray().Select(item => GetString(item, "id")).FirstOrDefault()
                : GetString(primary, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                var separator = id.IndexOf('-');
                loader = (separator < 0 ? id : id[..separator]).ToLowerInvariant();
                loaderVersion = separator < 0 ? null : id[(separator + 1)..];
            }
        }

        return Result<ModpackMetadata>.Ok(new ModpackMetadata(name, gameVersion, loader, loaderVersion, version));
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? GetBool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
