// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Modrinth;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Content;

/// <summary>依赖解析上下文。只包含解析所需的游戏版本与加载器，不依赖实例对象。</summary>
public sealed record DependencyResolutionContext(string GameVersion, string Loader);

/// <summary>
/// 依赖解析结果状态。区分“没找到”“匹配到多个项目”“项目存在但没有兼容版本”，
/// 避免把所有失败都压成同一种“找不到”。
/// </summary>
public enum DependencyResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    Incompatible,
    Failed,
}

/// <summary>
/// 已解析的依赖：携带稳定的来源、项目、版本与文件信息，安装阶段不需要再按 mod id 解析。
/// </summary>
public sealed record ResolvedDependency(
    string RequestedId,
    DependencyResolutionStatus Status,
    ContentSource? Source = null,
    string? ProjectId = null,
    string? Slug = null,
    string? ProjectName = null,
    string? VersionId = null,
    string? VersionNumber = null,
    string? FileName = null,
    Uri? FileUrl = null,
    string? Sha1 = null,
    long? Size = null,
    string? Reason = null)
{
    public bool IsResolved => Status == DependencyResolutionStatus.Resolved;

    public string DisplayName => string.IsNullOrWhiteSpace(ProjectName) ? RequestedId : ProjectName;
}

/// <summary>一次依赖解析的完整结果。安装器只消费该图，不自行猜测平台语义。</summary>
public sealed record ResolvedDependencyGraph(IReadOnlyList<ResolvedDependency> Items)
{
    public IReadOnlyList<ResolvedDependency> Resolved =>
        Items.Where(item => item.IsResolved).ToArray();

    public IReadOnlyList<ResolvedDependency> Unresolved =>
        Items.Where(item => !item.IsResolved).ToArray();
}

public sealed record DependencyResolutionProgress(
    int Completed,
    int Total,
    string? CurrentDependency);

public interface IDependencyResolver
{
    /// <summary>按依赖声明的 mod id / slug 精确解析到稳定的项目、版本与文件。</summary>
    Task<ResolvedDependency> ResolveAsync(
        string dependencyId,
        DependencyResolutionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>批量解析并返回依赖图；顺序与输入一致（重复 ID 只解析一次）。</summary>
    Task<ResolvedDependencyGraph> ResolveManyAsync(
        IReadOnlyList<string> dependencyIds,
        DependencyResolutionContext context,
        IProgress<DependencyResolutionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 缺失依赖的解析器。优先 Modrinth，再回退 CurseForge；两边都只接受精确的 project ID / slug。
/// 不按项目名称做模糊匹配，避免把同名但无关的 Mod 当成依赖安装。
/// </summary>
public sealed class DependencyResolver(
    IModrinthClient modrinth,
    ICurseForgeClient curseForge,
    ILogger<DependencyResolver> logger) : IDependencyResolver
{
    private const string ModrinthHost = "modrinth.com";

    public async Task<ResolvedDependency> ResolveAsync(
        string dependencyId,
        DependencyResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        dependencyId = dependencyId.Trim();
        if (dependencyId.Length == 0)
        {
            return NotFound(dependencyId, "依赖 ID 为空。");
        }

        var fromModrinth = await ResolveModrinthAsync(dependencyId, context, cancellationToken)
            .ConfigureAwait(false);
        if (fromModrinth.IsResolved)
        {
            return fromModrinth;
        }

        var fromCurseForge = await ResolveCurseForgeAsync(dependencyId, context, cancellationToken)
            .ConfigureAwait(false);
        if (fromCurseForge.Status != DependencyResolutionStatus.NotFound)
        {
            // CurseForge 给出了更具体的结果（歧义 / 不兼容 / 上游错误）时优先返回。
            return fromCurseForge;
        }

        if (fromModrinth.Status is DependencyResolutionStatus.Incompatible or DependencyResolutionStatus.Failed)
        {
            return fromModrinth;
        }

        logger.LogInformation("依赖 {DependencyId} 在 Modrinth 与 CurseForge 均未找到。", dependencyId);
        return NotFound(dependencyId, "Modrinth 与 CurseForge 都没有精确匹配该项目。");
    }

    public async Task<ResolvedDependencyGraph> ResolveManyAsync(
        IReadOnlyList<string> dependencyIds,
        DependencyResolutionContext context,
        IProgress<DependencyResolutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var unique = dependencyIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var items = new List<ResolvedDependency>(unique.Length);
        for (var index = 0; index < unique.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DependencyResolutionProgress(index, unique.Length, unique[index]));
            items.Add(await ResolveAsync(unique[index], context, cancellationToken).ConfigureAwait(false));
        }

        progress?.Report(new DependencyResolutionProgress(unique.Length, unique.Length, null));
        return new ResolvedDependencyGraph(items);
    }

    private async Task<ResolvedDependency> ResolveModrinthAsync(
        string dependencyId,
        DependencyResolutionContext context,
        CancellationToken cancellationToken)
    {
        var project = await modrinth.GetProjectAsync(dependencyId, cancellationToken).ConfigureAwait(false);
        if (!project.IsSuccess)
        {
            return project.Error!.Code == "modrinth.not_found"
                ? NotFound(dependencyId, "Modrinth 没有该项目。")
                : Failed(dependencyId, project.Error.Message);
        }

        var value = project.Value!;
        if (value.MappedProjectType != ModrinthProjectType.Mod)
        {
            return Incompatible(dependencyId, "Modrinth 项目不是 Mod。");
        }

        var versions = await modrinth
            .GetVersionsAsync(
                value.Id,
                context.GameVersion,
                ModrinthApi.LoaderForModrinth(context.Loader),
                cancellationToken)
            .ConfigureAwait(false);
        if (!versions.IsSuccess)
        {
            return Failed(dependencyId, versions.Error!.Message);
        }

        var version = versions.Value!
            .OrderBy(item => ReleaseRank(item.VersionType))
            .ThenByDescending(item => item.DatePublished)
            .FirstOrDefault();
        if (version is null)
        {
            return Incompatible(
                dependencyId,
                $"Modrinth 项目没有适用于 Minecraft {context.GameVersion} / {context.Loader} 的版本。",
                value.Id,
                value.Slug,
                value.Title);
        }

        var file = version.PrimaryFile;
        if (file is null)
        {
            return Incompatible(dependencyId, "Modrinth 版本没有可下载文件。", value.Id, value.Slug, value.Title);
        }

        if (!TryCreateModrinthUri(file.Url, out var uri))
        {
            return Incompatible(dependencyId, "Modrinth 文件地址不受支持。", value.Id, value.Slug, value.Title);
        }

        if (!IsSafeFileName(file.Filename))
        {
            return Incompatible(dependencyId, "Modrinth 返回了不安全的文件名。", value.Id, value.Slug, value.Title);
        }

        return new ResolvedDependency(
            dependencyId,
            DependencyResolutionStatus.Resolved,
            ContentSource.Modrinth,
            value.Id,
            value.Slug,
            string.IsNullOrWhiteSpace(value.Title) ? dependencyId : value.Title,
            version.Id,
            version.VersionNumber,
            file.Filename,
            uri,
            file.Hashes.Sha1,
            file.Size > 0 ? file.Size : null);
    }

    private async Task<ResolvedDependency> ResolveCurseForgeAsync(
        string dependencyId,
        DependencyResolutionContext context,
        CancellationToken cancellationToken)
    {
        // 只按 slug 查项目，不带游戏版本/加载器，先区分“项目不存在”和“项目存在但不兼容”。
        var search = await curseForge
            .SearchModsAsync(
                new CurseForgeSearchQuery
                {
                    Slug = dependencyId,
                    ClassId = CurseForgeClassIds.Mod,
                    PageSize = 10,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!search.IsSuccess)
        {
            return Failed(dependencyId, search.Error!.Message);
        }

        var matches = search.Value!.Mods
            .Where(mod => string.Equals(mod.Slug, dependencyId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            return NotFound(dependencyId, "CurseForge 没有该 slug 的项目。");
        }

        if (matches.Length > 1)
        {
            var names = string.Join("、", matches.Select(mod => $"{mod.Name}（{mod.Id}）"));
            return new ResolvedDependency(
                dependencyId,
                DependencyResolutionStatus.Ambiguous,
                ContentSource.CurseForge,
                Reason: $"CurseForge 有多个项目使用 slug {dependencyId}：{names}；未自动安装。");
        }

        var mod = matches[0];
        var loader = CurseForgeLoaders.FromInstanceLoader(context.Loader);
        var files = await curseForge
            .GetFilesForModAsync(mod.Id, context.GameVersion, loader, 50, cancellationToken)
            .ConfigureAwait(false);
        if (!files.IsSuccess)
        {
            return Failed(dependencyId, files.Error!.Message, mod.Id.ToString(CultureInfo.InvariantCulture));
        }

        var file = files.Value!
            .Where(item => string.IsNullOrWhiteSpace(context.GameVersion) ||
                           item.SupportsGameVersion(context.GameVersion))
            .Where(item => loader is null || item.SupportsLoader(loader.Value))
            .OrderBy(item => item.ReleaseType == 1 ? 0 : 1)
            .ThenByDescending(item => item.FileDate)
            .FirstOrDefault();
        if (file is null)
        {
            return Incompatible(
                dependencyId,
                $"CurseForge 项目没有适用于 Minecraft {context.GameVersion} / {context.Loader} 的文件。",
                mod.Id.ToString(CultureInfo.InvariantCulture),
                mod.Slug,
                mod.Name);
        }

        var url = file.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            var resolved = await curseForge
                .GetDownloadUrlAsync(mod.Id, file.Id, cancellationToken)
                .ConfigureAwait(false);
            url = resolved.IsSuccess ? resolved.Value : null;
        }

        if (!CurseForgeDownloads.TryCreateUri(url, out var uri))
        {
            return Incompatible(
                dependencyId,
                "CurseForge 没有可用的 CDN 地址；作者可能禁止第三方分发。",
                mod.Id.ToString(CultureInfo.InvariantCulture),
                mod.Slug,
                mod.Name);
        }

        if (!CurseForgeDownloads.IsSafeFileName(file.FileName))
        {
            return Incompatible(
                dependencyId,
                "CurseForge 返回了不安全的文件名。",
                mod.Id.ToString(CultureInfo.InvariantCulture),
                mod.Slug,
                mod.Name);
        }

        return new ResolvedDependency(
            dependencyId,
            DependencyResolutionStatus.Resolved,
            ContentSource.CurseForge,
            mod.Id.ToString(CultureInfo.InvariantCulture),
            mod.Slug,
            string.IsNullOrWhiteSpace(mod.Name) ? dependencyId : mod.Name,
            file.Id.ToString(CultureInfo.InvariantCulture),
            file.DisplayName,
            file.FileName,
            uri,
            file.Sha1,
            file.FileLength > 0 ? file.FileLength : null);
    }

    private static int ReleaseRank(string? versionType) => versionType?.ToLowerInvariant() switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => 3,
    };

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

    private static ResolvedDependency NotFound(string dependencyId, string reason) =>
        new(dependencyId, DependencyResolutionStatus.NotFound, Reason: reason);

    private static ResolvedDependency Incompatible(
        string dependencyId,
        string reason,
        string? projectId = null,
        string? slug = null,
        string? projectName = null) =>
        new(
            dependencyId,
            DependencyResolutionStatus.Incompatible,
            ProjectId: projectId,
            Slug: slug,
            ProjectName: projectName,
            Reason: reason);

    private static ResolvedDependency Failed(
        string dependencyId,
        string reason,
        string? projectId = null) =>
        new(
            dependencyId,
            DependencyResolutionStatus.Failed,
            ProjectId: projectId,
            Reason: reason);
}
