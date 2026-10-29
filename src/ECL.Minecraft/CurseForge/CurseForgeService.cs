// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Download;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.CurseForge;

public interface ICurseForgeService
{
    CurseForgeStatus Status { get; }

    Task<Result<CurseForgeSearchResult>> SearchAsync(
        CurseForgeSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<Result<CurseForgeInstallReport>> InstallAsync(
        string instanceId,
        string modReference,
        int? fileId = null,
        bool includeDependencies = true,
        IProgress<CurseForgeProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 把 CurseForge 搜索结果与文件解析成“可安装到某个实例”的具体文件，并处理必需依赖。
/// 所有下载都落在实例的 <c>minecraft/mods/</c>。
/// </summary>
public sealed class CurseForgeService(
    IInstanceService instances,
    IAppPaths paths,
    ICurseForgeClient client,
    IDownloadService downloads,
    ILogger<CurseForgeService> logger) : ICurseForgeService
{
    private const int MaxDependencyDepth = 6;
    private const string InstallStateFileName = ".ecl-curseforge.json";

    public CurseForgeStatus Status => client.Status;

    public Task<Result<CurseForgeSearchResult>> SearchAsync(
        CurseForgeSearchQuery query,
        CancellationToken cancellationToken = default) =>
        client.SearchModsAsync(query, cancellationToken);

    public async Task<Result<CurseForgeInstallReport>> InstallAsync(
        string instanceId,
        string modReference,
        int? fileId = null,
        bool includeDependencies = true,
        IProgress<CurseForgeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modReference))
        {
            return Result<CurseForgeInstallReport>.Fail("curseforge.invalid_mod", "请提供 CurseForge 项目 ID、slug 或链接。");
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<CurseForgeInstallReport>.Fail("instance.not_found", "实例不存在。");
        }

        var loader = CurseForgeLoaders.FromInstanceLoader(instance.Loader);
        if (loader is null)
        {
            return Result<CurseForgeInstallReport>.Fail(
                "curseforge.loader_unsupported",
                "原版实例不能安装 CurseForge Mod，请先创建带加载器的实例。");
        }

        progress?.Report(new CurseForgeProgress("resolve", "正在解析 CurseForge 项目…"));
        var modResult = await ResolveModAsync(modReference, instance, loader.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!modResult.IsSuccess)
        {
            return Result<CurseForgeInstallReport>.Fail(modResult.Error!);
        }

        var mod = modResult.Value!;
        var fileResult = await ResolveFileAsync(mod, instance, loader.Value, fileId, cancellationToken)
            .ConfigureAwait(false);
        if (!fileResult.IsSuccess)
        {
            return Result<CurseForgeInstallReport>.Fail(fileResult.Error!);
        }

        var primaryFile = fileResult.Value!;
        var warnings = new List<string>();
        var plan = new List<PlannedMod> { new(mod, primaryFile) };
        var gameDirectory = GetGameDirectory(instance.Id);
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        var knownProjects = LoadInstalled(gameDirectory)
            .Where(record => File.Exists(Path.Combine(modsDirectory, record.FileName)))
            .Select(record => record.ProjectId)
            .ToHashSet();
        knownProjects.Add(mod.Id);

        if (includeDependencies)
        {
            await AddDependenciesAsync(
                    primaryFile,
                    instance,
                    loader.Value,
                    knownProjects,
                    plan,
                    warnings,
                    depth: 0,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Directory.CreateDirectory(modsDirectory);

        var requests = new List<DownloadRequest>(plan.Count);
        var downloadable = new List<PlannedMod>(plan.Count);
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isPrimary = ReferenceEquals(item, plan[0]);
            var uriResult = await ResolveDownloadUriAsync(item.Mod, item.File, cancellationToken)
                .ConfigureAwait(false);
            if (!uriResult.IsSuccess)
            {
                if (isPrimary)
                {
                    return Result<CurseForgeInstallReport>.Fail(uriResult.Error!);
                }

                warnings.Add($"{item.Mod.Name}：{uriResult.Error!.Message}");
                continue;
            }

            if (!CurseForgeDownloads.IsSafeFileName(item.File.FileName))
            {
                var error = new EclError("curseforge.invalid_file_name", $"文件名为空或不安全：{item.File.FileName}");
                if (isPrimary)
                {
                    return Result<CurseForgeInstallReport>.Fail(error);
                }

                warnings.Add($"{item.Mod.Name}：{error.Message}");
                continue;
            }

            var destination = Path.Combine(modsDirectory, item.File.FileName);
            requests.Add(new DownloadRequest(
                destination,
                [uriResult.Value!],
                item.File.Sha1,
                HashingAlgorithm.Sha1,
                item.File.FileLength > 0 ? item.File.FileLength : null,
                $"{item.Mod.Name} · {item.File.FileName}"));
            downloadable.Add(item);
        }

        try
        {
            progress?.Report(new CurseForgeProgress("download", "正在下载 CurseForge 文件…", 0, requests.Count));
            var forwarding = new ForwardingProgress<DownloadProgress>(update =>
                progress?.Report(new CurseForgeProgress(
                    "download",
                    "正在下载 CurseForge 文件…",
                    update.CompletedFiles,
                    update.TotalFiles)));

            await downloads.DownloadAsync(requests, forwarding, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadException exception)
        {
            logger.LogError(exception, "安装 CurseForge 项目 {Mod} 时下载失败。", mod.Name);
            return Result<CurseForgeInstallReport>.Fail(
                "curseforge.download_failed",
                $"下载 CurseForge 文件失败：{exception.Message}",
                string.Join("; ", exception.Failures.Take(5)));
        }

        var installed = LoadInstalled(GetGameDirectory(instance.Id));
        var installedAt = DateTimeOffset.UtcNow.ToString("O");
        foreach (var item in downloadable)
        {
            installed.RemoveAll(record => record.ProjectId == item.Mod.Id);
            installed.Add(new InstalledModRecord
            {
                ProjectId = item.Mod.Id,
                FileId = item.File.Id,
                FileName = item.File.FileName,
                InstalledAt = installedAt,
            });
        }

        try
        {
            SaveInstalled(gameDirectory, installed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"无法写入安装记录：{exception.Message}");
            logger.LogWarning(exception, "写入 CurseForge 安装记录失败：{Directory}", gameDirectory);
        }

        progress?.Report(new CurseForgeProgress("done", "CurseForge Mod 安装完成。", downloadable.Count, downloadable.Count));
        var installedFiles = downloadable.Select(item => item.File.FileName).ToArray();
        logger.LogInformation(
            "已从 CurseForge 安装 {Mod}（项目 {ProjectId}，文件 {FileId}）到实例 {InstanceId}，共 {Count} 个文件。",
            mod.Name,
            mod.Id,
            primaryFile.Id,
            instance.Id,
            installedFiles.Length);

        return Result<CurseForgeInstallReport>.Ok(new CurseForgeInstallReport(
            mod.Name,
            mod.Id,
            primaryFile.Id,
            primaryFile.FileName,
            installedFiles,
            warnings));
    }

    private async Task<Result<CurseForgeMod>> ResolveModAsync(
        string reference,
        GameInstance instance,
        CurseForgeModLoader loader,
        CancellationToken cancellationToken)
    {
        var trimmed = reference.Trim();
        if (int.TryParse(trimmed, out var modId) && modId > 0)
        {
            return await client.GetModAsync(modId, cancellationToken).ConfigureAwait(false);
        }

        var slug = ExtractSlug(trimmed);
        var bySlug = await client.FindModBySlugAsync(slug, cancellationToken).ConfigureAwait(false);
        if (!bySlug.IsSuccess)
        {
            return Result<CurseForgeMod>.Fail(bySlug.Error!);
        }

        if (bySlug.Value is not null)
        {
            return Result<CurseForgeMod>.Ok(bySlug.Value);
        }

        var search = await client.SearchModsAsync(
                new CurseForgeSearchQuery
                {
                    SearchFilter = trimmed,
                    GameVersion = instance.GameVersion,
                    Loader = loader,
                    PageSize = 10,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!search.IsSuccess)
        {
            return Result<CurseForgeMod>.Fail(search.Error!);
        }

        var mod = search.Value!.Mods.FirstOrDefault();
        return mod is null
            ? Result<CurseForgeMod>.Fail(
                "curseforge.mod_not_found",
                $"没有找到 CurseForge 项目：{reference}",
                $"游戏版本 {instance.GameVersion}，加载器 {CurseForgeLoaders.DisplayName(loader)}")
            : Result<CurseForgeMod>.Ok(mod);
    }

    private async Task<Result<CurseForgeFile>> ResolveFileAsync(
        CurseForgeMod mod,
        GameInstance instance,
        CurseForgeModLoader loader,
        int? fileId,
        CancellationToken cancellationToken)
    {
        if (fileId is { } explicitFileId)
        {
            var explicitFile = await client.GetFileAsync(mod.Id, explicitFileId, cancellationToken)
                .ConfigureAwait(false);
            return explicitFile.IsSuccess
                ? ValidateFile(explicitFile.Value!, instance, loader)
                : Result<CurseForgeFile>.Fail(explicitFile.Error!);
        }

        var candidates = mod.LatestFilesIndexes
            .Where(index => index.FileId > 0 &&
                            index.ModLoader == (int)loader &&
                            string.Equals(index.GameVersion, instance.GameVersion, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var chosen = candidates.FirstOrDefault(index => index.ReleaseType == 1) ?? candidates.FirstOrDefault();
        if (chosen is not null)
        {
            var file = await client.GetFileAsync(mod.Id, chosen.FileId, cancellationToken).ConfigureAwait(false);
            if (file.IsSuccess)
            {
                var validated = ValidateFile(file.Value!, instance, loader);
                if (validated.IsSuccess)
                {
                    return validated;
                }
            }
        }

        var listed = await client
            .GetFilesForModAsync(mod.Id, instance.GameVersion, loader, 50, cancellationToken)
            .ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return Result<CurseForgeFile>.Fail(listed.Error!);
        }

        var fallback = listed.Value!
            .FirstOrDefault(file => file.SupportsGameVersion(instance.GameVersion) && file.SupportsLoader(loader));
        return fallback is null
            ? Result<CurseForgeFile>.Fail(
                "curseforge.no_compatible_file",
                $"{mod.Name} 没有适用于 Minecraft {instance.GameVersion} / {CurseForgeLoaders.DisplayName(loader)} 的文件。")
            : Result<CurseForgeFile>.Ok(fallback);
    }

    private async Task AddDependenciesAsync(
        CurseForgeFile file,
        GameInstance instance,
        CurseForgeModLoader loader,
        HashSet<int> knownProjects,
        List<PlannedMod> plan,
        List<string> warnings,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth >= MaxDependencyDepth)
        {
            warnings.Add("依赖层级超过限制，已停止自动解析；请手动检查缺失的依赖。");
            return;
        }

        foreach (var dependency in file.Dependencies.Where(item => item.RelationType == 3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!knownProjects.Add(dependency.ModId))
            {
                continue;
            }

            var modResult = await client.GetModAsync(dependency.ModId, cancellationToken).ConfigureAwait(false);
            if (!modResult.IsSuccess)
            {
                warnings.Add($"依赖项目 {dependency.ModId} 解析失败：{modResult.Error!.Message}");
                continue;
            }

            var dependencyMod = modResult.Value!;
            var dependencyFile = await ResolveFileAsync(dependencyMod, instance, loader, null, cancellationToken)
                .ConfigureAwait(false);
            if (!dependencyFile.IsSuccess)
            {
                warnings.Add($"{dependencyMod.Name}：{dependencyFile.Error!.Message}");
                continue;
            }

            plan.Add(new PlannedMod(dependencyMod, dependencyFile.Value!));
            await AddDependenciesAsync(
                    dependencyFile.Value!,
                    instance,
                    loader,
                    knownProjects,
                    plan,
                    warnings,
                    depth + 1,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<Result<Uri>> ResolveDownloadUriAsync(
        CurseForgeMod mod,
        CurseForgeFile file,
        CancellationToken cancellationToken)
    {
        var url = file.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            var resolved = await client.GetDownloadUrlAsync(file.ModId, file.Id, cancellationToken)
                .ConfigureAwait(false);
            url = resolved.IsSuccess ? resolved.Value : null;
        }

        return CurseForgeDownloads.TryCreateUri(url, out var uri)
            ? Result<Uri>.Ok(uri)
            : Result<Uri>.Fail(
                "curseforge.distribution_restricted",
                $"{mod.Name} 的 {file.FileName} 没有可用的 CurseForge CDN 下载地址；作者可能禁止第三方分发。");
    }

    private static Result<CurseForgeFile> ValidateFile(
        CurseForgeFile file,
        GameInstance instance,
        CurseForgeModLoader loader)
    {
        if (!file.SupportsGameVersion(instance.GameVersion))
        {
            return Result<CurseForgeFile>.Fail(
                "curseforge.incompatible",
                $"{file.FileName} 不支持 Minecraft {instance.GameVersion}。");
        }

        if (!file.SupportsLoader(loader))
        {
            return Result<CurseForgeFile>.Fail(
                "curseforge.incompatible",
                $"{file.FileName} 不支持 {CurseForgeLoaders.DisplayName(loader)} 加载器。");
        }

        if (!CurseForgeDownloads.IsSafeFileName(file.FileName))
        {
            return Result<CurseForgeFile>.Fail(
                "curseforge.invalid_file_name",
                $"CurseForge 返回了不安全的文件名：{file.FileName}");
        }

        return Result<CurseForgeFile>.Ok(file);
    }

    private string GetGameDirectory(string instanceId) => paths.GetInstanceGameDirectory(instanceId);

    private static string ExtractSlug(string reference)
    {
        if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri) ||
            !(string.Equals(uri.Host, "curseforge.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".curseforge.com", StringComparison.OrdinalIgnoreCase)))
        {
            return reference;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var projectIndex = Array.LastIndexOf(segments, "mc-mods");
        if (projectIndex >= 0 && projectIndex + 1 < segments.Length)
        {
            return segments[projectIndex + 1];
        }

        return segments.Length == 0 ? reference : segments[^1];
    }

    private static List<InstalledModRecord> LoadInstalled(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, InstallStateFileName);
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var state = JsonSerializer.Deserialize<InstallStateDocument>(File.ReadAllText(path), EclJson.Default);
            return state?.Mods?
                .Where(record => record.ProjectId > 0 && record.FileId > 0 && !string.IsNullOrWhiteSpace(record.FileName))
                .ToList() ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void SaveInstalled(string gameDirectory, IReadOnlyList<InstalledModRecord> records)
    {
        Directory.CreateDirectory(gameDirectory);
        var path = Path.Combine(gameDirectory, InstallStateFileName);
        var state = new InstallStateDocument { Mods = records.ToList() };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, EclJson.Indented));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record PlannedMod(CurseForgeMod Mod, CurseForgeFile File);

    private sealed class InstallStateDocument
    {
        public List<InstalledModRecord> Mods { get; set; } = [];
    }

    private sealed class InstalledModRecord
    {
        public int ProjectId { get; set; }

        public int FileId { get; set; }

        public string FileName { get; set; } = string.Empty;

        public string InstalledAt { get; set; } = string.Empty;
    }

    private sealed class ForwardingProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
