// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Ecl.Core;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Modrinth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Content;

/// <summary>依赖图中单个节点的解析结论（F-06 §3；状态不可折叠为布尔值）。</summary>
public enum ContentDependencyStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    Incompatible,
    Failed,
    SkippedOptional,
}

/// <summary>内容来源；用于稳定身份与所有权证据，不用文件名推断。</summary>
public enum ContentSourceKind
{
    Modrinth,
    CurseForge,
}

/// <summary>
/// 依赖图节点。键 = <c>&lt;source&gt;:&lt;projectId&gt;@&lt;versionId&gt;</c>；
/// 无版本 ID（仅项目 ID）时用 <c>&lt;source&gt;:&lt;projectId&gt;@latest</c>，解析结果必须写回真实版本。
/// </summary>
public sealed record ContentDependencyNode(
    string Key,
    ContentSourceKind Source,
    string? ProjectId,
    string? VersionId,
    string? ProjectName = null,
    string? VersionNumber = null,
    string? FileName = null,
    string? DownloadUrl = null,
    string? Sha1 = null,
    long? Size = null,
    bool Required = true,
    string? RequestedBy = null,
    ContentDependencyStatus Status = ContentDependencyStatus.Resolved,
    string? Reason = null);

public sealed record ContentDependencyEdge(
    string FromKey,
    string ToKey,
    bool Required,
    string? DependencyType = null);

/// <summary>
/// 内容安装计划（F-06 S2）：解析结果 + 依赖边 + 文件清单的一次性只读快照。
/// 安装阶段只能消费这里的文件（含真实版本 ID/URL/哈希），不得按项目 ID 二次猜测依赖版本。
/// </summary>
public sealed record ContentInstallPlan(
    int SchemaVersion,
    string PlanId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string InstanceId,
    string? GameVersion,
    string? Loader,
    ContentSourceKind Source,
    ContentKind Kind,
    string RootKey,
    IReadOnlyList<ContentDependencyNode> Nodes,
    IReadOnlyList<ContentDependencyEdge> Edges,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> IncompatibleSkipped,
    string PolicyVersion = "f06-1")
{
    /// <summary>可安装文件：只有 Resolved 节点有真实版本 ID 与下载地址。</summary>
    public IReadOnlyList<ContentDependencyNode> Files =>
        Nodes.Where(node => node.Status == ContentDependencyStatus.Resolved &&
                            node.VersionId is not null && node.DownloadUrl is not null).ToArray();

    public bool IsInstallable => Blockers.Count == 0 && Files.Count > 0;

    public long TotalBytes => Files.Sum(file => file.Size ?? 0);

    /// <summary>计划是否描述的是“更新已存在的内容”（由执行方在替换前写入内容记录）。</summary>
    public ContentDependencyNode? Root =>
        Nodes.FirstOrDefault(node => string.Equals(node.Key, RootKey, StringComparison.Ordinal));

    public string ComputeHash() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '|',
            InstanceId,
            GameVersion ?? string.Empty,
            Loader ?? string.Empty,
            Source,
            Kind,
            RootKey,
            PolicyVersion,
            string.Join(";", Files.Select(file => $"{file.Key}:{file.VersionId}:{file.FileName}"))))));
}

/// <summary>计划请求：只接受稳定身份（项目 ID + 可选版本 ID），不接受“按文件名找最新”。</summary>
public sealed record ContentInstallPlanRequest(
    ContentSourceKind Source,
    string ProjectId,
    string? VersionId = null,
    bool IncludeOptionalDependencies = false,
    bool IncludeRequiredDependencies = true,
    ContentKind Kind = ContentKind.Mod);

public interface IContentInstallPlanner
{
    /// <summary>生成只读安装计划；不下载、不写实例目录、不修改任何内容。</summary>
    Task<Result<ContentInstallPlan>> PlanAsync(
        string instanceId,
        ContentInstallPlanRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// F-06 S1/S4 依赖图解析：把“内容 → 必需依赖”展开成显式图（节点 + 边 + 状态），并给出可安装文件清单。
/// Modrinth 与 CurseForge 都只接受稳定项目 ID；解析出的节点携带真实版本 ID、文件名、下载地址与哈希，
/// 安装阶段（<see cref="IContentPlanExecutor"/>）只消费这些文件，不再按项目 ID 二次选择版本。
/// </summary>
public sealed class ContentInstallPlanner : IContentInstallPlanner
{
    private const int MaxDepth = 4;
    private const int MaxNodes = 128;

    private readonly IInstanceService _instances;
    private readonly IModrinthClient _modrinth;
    private readonly ICurseForgeClient _curseForge;
    private readonly ILogger<ContentInstallPlanner> _logger;
    private readonly TimeProvider _time;

    public ContentInstallPlanner(
        IInstanceService instances,
        IModrinthClient modrinth,
        ICurseForgeClient curseForge,
        ILogger<ContentInstallPlanner>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _modrinth = modrinth;
        _curseForge = curseForge;
        _logger = logger ?? NullLogger<ContentInstallPlanner>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<ContentInstallPlan>> PlanAsync(
        string instanceId,
        ContentInstallPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return Result<ContentInstallPlan>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            return Result<ContentInstallPlan>.Fail("content.plan.invalid_project", "计划需要稳定的项目 ID。");
        }

        if (request.Kind is ContentKind.ModPack)
        {
            return Result<ContentInstallPlan>.Fail(
                "content.plan.unsupported_kind",
                "整合包不生成依赖计划；整合包必须走部署为新实例的流程。");
        }

        if (request.Kind is ContentKind.DataPack)
        {
            return Result<ContentInstallPlan>.Fail(
                "content.plan.unsupported_kind",
                "数据包必须指定目标世界；数据包安装不生成实例级依赖计划。");
        }

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ContentInstallPlan>.Fail("instance.not_found", "实例不存在。");
        }

        var context = new PlanContext(request, instance);
        return request.Source == ContentSourceKind.Modrinth
            ? await PlanModrinthAsync(instance, context, cancellationToken).ConfigureAwait(false)
            : await PlanCurseForgeAsync(instance, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<ContentInstallPlan>> PlanModrinthAsync(
        GameInstance instance,
        PlanContext context,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var rootResult = request.VersionId is null
            ? await ResolveModrinthLatestAsync(
                request.ProjectId,
                context,
                requestedBy: null,
                cancellationToken).ConfigureAwait(false)
            : await ResolveModrinthVersionAsync(
                request.VersionId,
                request.ProjectId,
                context,
                requestedBy: null,
                cancellationToken).ConfigureAwait(false);
        if (!rootResult.IsSuccess)
        {
            return Result<ContentInstallPlan>.Fail(rootResult.Error!);
        }

        var rootNode = rootResult.Value!;
        var nodes = new List<ContentDependencyNode> { rootNode };
        var edges = new List<ContentDependencyEdge>();
        var blockers = new List<string>();
        var warnings = new List<string>();
        var incompatible = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootNode.Key };
        AddRootBlockers(rootNode, blockers, incompatible);

        if (request.Kind == ContentKind.Mod && request.IncludeRequiredDependencies &&
            rootNode.Status == ContentDependencyStatus.Resolved)
        {
            var queue = new Queue<(ContentDependencyNode Node, int Depth)>();
            queue.Enqueue((rootNode, 0));
            var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                request.ProjectId,
            };

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (current, depth) = queue.Dequeue();
                if (current.VersionId is null || depth >= MaxDepth)
                {
                    continue;
                }

                var fetched = await _modrinth.GetVersionAsync(current.VersionId, cancellationToken)
                    .ConfigureAwait(false);
                if (!fetched.IsSuccess || fetched.Value is null)
                {
                    warnings.Add($"无法读取 {current.Key} 的依赖列表；结果可能不完整。");
                    continue;
                }

                foreach (var dependency in fetched.Value.Dependencies)
                {
                    var required = string.Equals(dependency.DependencyType, "required", StringComparison.OrdinalIgnoreCase);
                    if (!required && !request.IncludeOptionalDependencies)
                    {
                        var optionalKey = BuildKey(ContentSourceKind.Modrinth, dependency.ProjectId, dependency.VersionId);
                        edges.Add(new ContentDependencyEdge(current.Key, optionalKey, Required: false, dependency.DependencyType));
                        continue;
                    }

                    if (!required)
                    {
                        // 可选依赖默认不展开安装；显式要求时仍按“只记录边”处理，避免把可选内容当成必需。
                        var optionalKey = BuildKey(ContentSourceKind.Modrinth, dependency.ProjectId, dependency.VersionId);
                        edges.Add(new ContentDependencyEdge(current.Key, optionalKey, Required: false, dependency.DependencyType));
                        continue;
                    }

                    var dependencyProjectId = dependency.ProjectId;
                    if (!string.IsNullOrWhiteSpace(dependencyProjectId) &&
                        !visitedProjects.Add(dependencyProjectId!))
                    {
                        // 同一项目（任意版本）只解析一次，避免依赖环与重复文件。
                        var existing = nodes.FirstOrDefault(node =>
                            string.Equals(node.ProjectId, dependencyProjectId, StringComparison.OrdinalIgnoreCase));
                        if (existing is not null)
                        {
                            edges.Add(new ContentDependencyEdge(current.Key, existing.Key, Required: true, dependency.DependencyType));
                        }

                        continue;
                    }

                    ContentDependencyNode node;
                    if (!string.IsNullOrWhiteSpace(dependency.VersionId))
                    {
                        var resolved = await ResolveModrinthVersionAsync(
                            dependency.VersionId!,
                            dependencyProjectId,
                            context,
                            current.Key,
                            cancellationToken).ConfigureAwait(false);
                        node = resolved.IsSuccess
                            ? resolved.Value!
                            : MissingModrinthDependency(
                                dependencyProjectId,
                                dependency.VersionId,
                                dependency.FileName,
                                current.Key,
                                resolved.Error?.Message);
                    }
                    else if (!string.IsNullOrWhiteSpace(dependencyProjectId))
                    {
                        var resolved = await ResolveModrinthLatestAsync(
                            dependencyProjectId!,
                            context,
                            current.Key,
                            cancellationToken).ConfigureAwait(false);
                        node = resolved.IsSuccess
                            ? resolved.Value!
                            : MissingModrinthDependency(
                                dependencyProjectId,
                                null,
                                dependency.FileName,
                                current.Key,
                                resolved.Error?.Message);
                    }
                    else
                    {
                        node = new ContentDependencyNode(
                            BuildKey(ContentSourceKind.Modrinth, null, null, dependency.FileName),
                            ContentSourceKind.Modrinth,
                            null,
                            null,
                            FileName: dependency.FileName,
                            Required: true,
                            RequestedBy: current.Key,
                            Status: ContentDependencyStatus.Ambiguous,
                            Reason: "依赖只给出文件名，无法确定稳定身份。");
                    }

                    if (!visited.Add(node.Key))
                    {
                        edges.Add(new ContentDependencyEdge(current.Key, node.Key, Required: true, dependency.DependencyType));
                        continue;
                    }

                    nodes.Add(node);
                    edges.Add(new ContentDependencyEdge(current.Key, node.Key, Required: true, dependency.DependencyType));
                    AccumulateDependencyStatus(node, required: true, blockers, warnings, incompatible);
                    if (nodes.Count > MaxNodes)
                    {
                        blockers.Add("content.plan.limit_reached");
                        warnings.Add($"依赖节点超过 {MaxNodes} 个，计划被截断；不得按截断结果宣称完整。");
                        break;
                    }

                    if (node.Status == ContentDependencyStatus.Resolved && node.VersionId is not null)
                    {
                        queue.Enqueue((node, depth + 1));
                    }
                }
            }
        }

        return BuildPlan(instance, request, rootNode, nodes, edges, blockers, warnings, incompatible);
    }

    private async Task<Result<ContentInstallPlan>> PlanCurseForgeAsync(
        GameInstance instance,
        PlanContext context,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (!int.TryParse(request.ProjectId, out var projectId) || projectId <= 0)
        {
            return Result<ContentInstallPlan>.Fail(
                "content.plan.invalid_project",
                "CurseForge 计划需要 numeric 项目 ID；slug/链接不是稳定身份。");
        }

        var modResult = await _curseForge.GetModAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!modResult.IsSuccess)
        {
            return Result<ContentInstallPlan>.Fail(modResult.Error!);
        }

        var mod = modResult.Value!;
        var rootResult = await ResolveCurseForgeFileAsync(
            mod,
            request.VersionId,
            context,
            explicitVersion: request.VersionId is not null,
            cancellationToken).ConfigureAwait(false);
        if (!rootResult.IsSuccess)
        {
            return Result<ContentInstallPlan>.Fail(rootResult.Error!);
        }

        var rootNode = rootResult.Value!;
        var nodes = new List<ContentDependencyNode> { rootNode };
        var edges = new List<ContentDependencyEdge>();
        var blockers = new List<string>();
        var warnings = new List<string>();
        var incompatible = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootNode.Key };
        var visitedProjects = new HashSet<int> { projectId };
        AddRootBlockers(rootNode, blockers, incompatible);

        if (request.Kind == ContentKind.Mod && request.IncludeRequiredDependencies &&
            rootNode.Status == ContentDependencyStatus.Resolved &&
            int.TryParse(rootNode.VersionId, out var rootFileId))
        {
            var queue = new Queue<(string FileId, ContentDependencyNode Node, int Depth)>();
            queue.Enqueue((rootFileId.ToString(), rootNode, 0));
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (fileIdText, current, depth) = queue.Dequeue();
                if (depth >= MaxDepth || !int.TryParse(fileIdText, out var fileId) || fileId <= 0)
                {
                    continue;
                }

                var fileResult = await _curseForge.GetFileAsync(
                    int.Parse(current.ProjectId ?? "0"),
                    fileId,
                    cancellationToken).ConfigureAwait(false);
                if (!fileResult.IsSuccess || fileResult.Value is null)
                {
                    warnings.Add($"无法读取 {current.Key} 的依赖列表；结果可能不完整。");
                    continue;
                }

                foreach (var dependency in fileResult.Value.Dependencies.Where(item => item.RelationType == 3))
                {
                    if (dependency.ModId <= 0 || !visitedProjects.Add(dependency.ModId))
                    {
                        continue;
                    }

                    var dependencyMod = await _curseForge.GetModAsync(dependency.ModId, cancellationToken)
                        .ConfigureAwait(false);
                    if (!dependencyMod.IsSuccess || dependencyMod.Value is null)
                    {
                        var missing = MissingCurseForgeDependency(
                            dependency.ModId,
                            current.Key,
                            dependencyMod.Error?.Message ?? "项目不存在。");
                        nodes.Add(missing);
                        edges.Add(new ContentDependencyEdge(current.Key, missing.Key, Required: true));
                        AccumulateDependencyStatus(missing, required: true, blockers, warnings, incompatible);
                        continue;
                    }

                    var resolved = await ResolveCurseForgeFileAsync(
                        dependencyMod.Value!,
                        versionId: null,
                        context,
                        explicitVersion: false,
                        cancellationToken).ConfigureAwait(false);
                    var node = resolved.IsSuccess
                        ? resolved.Value!
                        : MissingCurseForgeDependency(
                            dependency.ModId,
                            current.Key,
                            resolved.Error?.Message);
                    if (!visited.Add(node.Key))
                    {
                        edges.Add(new ContentDependencyEdge(current.Key, node.Key, Required: true));
                        continue;
                    }

                    nodes.Add(node);
                    edges.Add(new ContentDependencyEdge(current.Key, node.Key, Required: true));
                    AccumulateDependencyStatus(node, required: true, blockers, warnings, incompatible);
                    if (nodes.Count > MaxNodes)
                    {
                        blockers.Add("content.plan.limit_reached");
                        warnings.Add($"依赖节点超过 {MaxNodes} 个，计划被截断；不得按截断结果宣称完整。");
                        break;
                    }

                    if (node.Status == ContentDependencyStatus.Resolved && node.VersionId is not null)
                    {
                        queue.Enqueue((node.VersionId, node, depth + 1));
                    }
                }
            }
        }

        return BuildPlan(instance, request, rootNode, nodes, edges, blockers, warnings, incompatible);
    }

    private Result<ContentInstallPlan> BuildPlan(
        GameInstance instance,
        ContentInstallPlanRequest request,
        ContentDependencyNode rootNode,
        List<ContentDependencyNode> nodes,
        List<ContentDependencyEdge> edges,
        List<string> blockers,
        List<string> warnings,
        List<string> incompatible)
    {
        var distinctBlockers = blockers.Distinct(StringComparer.Ordinal).ToArray();
        var issued = _time.GetUtcNow();
        var plan = new ContentInstallPlan(
            1,
            $"content-{issued:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(6).ToLowerInvariant()}",
            issued,
            issued.AddMinutes(30),
            instance.Id,
            instance.GameVersion,
            instance.Loader,
            request.Source,
            request.Kind,
            rootNode.Key,
            nodes,
            edges,
            distinctBlockers,
            warnings,
            incompatible,
            "f06-1");
        _logger.LogInformation(
            "已生成内容计划 {PlanId}：实例 {InstanceId}，来源 {Source}，种类 {Kind}，节点 {Nodes}，可安装文件 {Files}，阻断 {Blockers}。",
            plan.PlanId,
            instance.Id,
            plan.Source,
            plan.Kind,
            plan.Nodes.Count,
            plan.Files.Count,
            plan.Blockers.Count);
        return Result<ContentInstallPlan>.Ok(plan);
    }

    private static void AddRootBlockers(
        ContentDependencyNode rootNode,
        List<string> blockers,
        List<string> incompatible)
    {
        switch (rootNode.Status)
        {
            case ContentDependencyStatus.Incompatible:
                blockers.Add("content.plan.incompatible");
                incompatible.Add(rootNode.Key);
                break;
            case ContentDependencyStatus.Resolved:
                break;
            default:
                blockers.Add("content.plan.unresolved_root");
                break;
        }
    }

    private static void AccumulateDependencyStatus(
        ContentDependencyNode node,
        bool required,
        List<string> blockers,
        List<string> warnings,
        List<string> incompatible)
    {
        switch (node.Status)
        {
            case ContentDependencyStatus.Incompatible:
                if (required)
                {
                    blockers.Add("content.plan.incompatible_dependency");
                }

                incompatible.Add(node.Key);
                break;
            case ContentDependencyStatus.NotFound:
            case ContentDependencyStatus.Ambiguous:
            case ContentDependencyStatus.Failed:
                if (required)
                {
                    blockers.Add("content.plan.unresolved_dependency");
                }
                else if (node.Reason is not null)
                {
                    warnings.Add(node.Reason);
                }

                break;
        }
    }

    private async Task<Result<ContentDependencyNode>> ResolveModrinthVersionAsync(
        string versionId,
        string? expectedProjectId,
        PlanContext context,
        string? requestedBy,
        CancellationToken cancellationToken)
    {
        var fetched = await _modrinth.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsSuccess || fetched.Value is null)
        {
            return Result<ContentDependencyNode>.Fail(
                "content.plan.not_found",
                $"没有找到 Modrinth 版本 {versionId}。");
        }

        if (!string.IsNullOrWhiteSpace(expectedProjectId) &&
            !string.Equals(fetched.Value.ProjectId, expectedProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return Result<ContentDependencyNode>.Fail(
                "content.plan.version_project_mismatch",
                $"版本 {versionId} 不属于项目 {expectedProjectId}。");
        }

        var node = ToModrinthNode(fetched.Value, context, requestedBy);
        if (node.Status == ContentDependencyStatus.Failed)
        {
            return Result<ContentDependencyNode>.Fail("content.plan.not_found", node.Reason ?? "没有可下载文件。");
        }

        return Result<ContentDependencyNode>.Ok(node);
    }

    private async Task<Result<ContentDependencyNode>> ResolveModrinthLatestAsync(
        string projectId,
        PlanContext context,
        string? requestedBy,
        CancellationToken cancellationToken)
    {
        var loader = context.Request.Kind == ContentKind.Mod ? context.Instance.Loader : null;
        var versions = await _modrinth
            .GetVersionsAsync(projectId, context.Instance.GameVersion, loader, cancellationToken)
            .ConfigureAwait(false);
        if (!versions.IsSuccess)
        {
            return Result<ContentDependencyNode>.Fail(versions.Error!);
        }

        var compatible = versions.Value!
            .Where(version => version.SupportsGameVersion(context.Instance.GameVersion))
            .Where(version => context.Request.Kind != ContentKind.Mod || version.SupportsLoader(context.Instance.Loader))
            .OrderBy(version => ReleaseRank(version.VersionType))
            .ThenByDescending(version => version.DatePublished)
            .FirstOrDefault();
        if (compatible is null)
        {
            return Result<ContentDependencyNode>.Fail(
                "content.plan.not_found",
                "没有找到与实例版本/加载器兼容的内容版本。");
        }

        var node = ToModrinthNode(compatible, context, requestedBy);
        return node.Status == ContentDependencyStatus.Failed
            ? Result<ContentDependencyNode>.Fail("content.plan.not_found", node.Reason ?? "没有可下载文件。")
            : Result<ContentDependencyNode>.Ok(node);
    }

    private static ContentDependencyNode ToModrinthNode(
        ModrinthVersion version,
        PlanContext context,
        string? requestedBy)
    {
        var key = BuildKey(ContentSourceKind.Modrinth, version.ProjectId, version.Id);
        var isMod = context.Request.Kind == ContentKind.Mod;
        // 非 Mod 内容（资源包/光影包）不使用实例加载器；显式版本一律接受，最新版本只按游戏版本判断。
        var compatible = isMod
            ? version.SupportsGameVersion(context.Instance.GameVersion) &&
              version.SupportsLoader(context.Instance.Loader)
            : version.SupportsGameVersion(context.Instance.GameVersion) ||
              context.Request.VersionId is not null;
        var file = version.PrimaryFile;
        var fileSupported = file is not null && SafeFileName(file.Filename) &&
                            TryCreateModrinthUri(file.Url, out _);
        return new ContentDependencyNode(
            key,
            ContentSourceKind.Modrinth,
            version.ProjectId,
            version.Id,
            ProjectName: null,
            VersionNumber: version.VersionNumber,
            FileName: file?.Filename,
            DownloadUrl: compatible && fileSupported ? file!.Url : null,
            Sha1: file?.Hashes.Sha1,
            Size: file?.Size,
            Required: true,
            RequestedBy: requestedBy,
            Status: !compatible
                ? ContentDependencyStatus.Incompatible
                : !fileSupported
                    ? ContentDependencyStatus.Failed
                    : ContentDependencyStatus.Resolved,
            Reason: !compatible
                ? $"需要 MC {string.Join('/', version.GameVersions)} / 加载器 {string.Join('/', version.Loaders)}，实例为 {context.Instance.GameVersion}/{context.Instance.Loader}。"
                : !fileSupported
                    ? "版本没有可安全下载的主文件。"
                    : null);
    }

    private async Task<Result<ContentDependencyNode>> ResolveCurseForgeFileAsync(
        CurseForgeMod mod,
        string? versionId,
        PlanContext context,
        bool explicitVersion,
        CancellationToken cancellationToken)
    {
        CurseForgeFile? chosen = null;
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            if (!int.TryParse(versionId, out var fileId) || fileId <= 0)
            {
                return Result<ContentDependencyNode>.Fail(
                    "curseforge.invalid_file",
                    "CurseForge 文件 ID 无效。");
            }

            var file = await _curseForge.GetFileAsync(mod.Id, fileId, cancellationToken).ConfigureAwait(false);
            if (!file.IsSuccess)
            {
                return Result<ContentDependencyNode>.Fail(file.Error!);
            }

            chosen = file.Value;
        }
        else
        {
            chosen = await PickCurseForgeFileAsync(mod, context, cancellationToken).ConfigureAwait(false);
        }

        if (chosen is null)
        {
            return Result<ContentDependencyNode>.Fail(
                "curseforge.no_compatible_file",
                $"{mod.Name} 没有适用于当前实例的文件。");
        }

        var loader = CurseForgeLoaders.FromInstanceLoader(context.Instance.Loader);
        var isMod = context.Request.Kind == ContentKind.Mod;
        var compatible = isMod
            ? (explicitVersion || chosen.SupportsGameVersion(context.Instance.GameVersion)) &&
              (loader is null || chosen.SupportsLoader(loader.Value))
            : explicitVersion || chosen.SupportsGameVersion(context.Instance.GameVersion);
        var url = await ResolveCurseForgeUriAsync(chosen, cancellationToken).ConfigureAwait(false);
        if (url is null)
        {
            // 作者禁止第三方分发时没有可用 CDN 地址：这是“不可下载”，不是“计划未解析”。
            return Result<ContentDependencyNode>.Fail(
                "curseforge.distribution_restricted",
                $"{mod.Name} 的 {chosen.FileName} 没有可用的 CurseForge CDN 地址；作者可能禁止第三方分发。");
        }

        if (!SafeFileName(chosen.FileName))
        {
            return Result<ContentDependencyNode>.Fail(
                "curseforge.invalid_file_name",
                $"CurseForge 返回了不安全的文件名：{chosen.FileName}");
        }

        var key = BuildKey(ContentSourceKind.CurseForge, mod.Id.ToString(), chosen.Id.ToString());
        return Result<ContentDependencyNode>.Ok(new ContentDependencyNode(
            key,
            ContentSourceKind.CurseForge,
            mod.Id.ToString(),
            chosen.Id.ToString(),
            ProjectName: mod.Name,
            VersionNumber: chosen.DisplayName ?? chosen.FileName,
            FileName: chosen.FileName,
            DownloadUrl: compatible ? url.ToString() : null,
            Sha1: chosen.Sha1,
            Size: chosen.FileLength > 0 ? chosen.FileLength : null,
            Required: true,
            RequestedBy: null,
            Status: compatible ? ContentDependencyStatus.Resolved : ContentDependencyStatus.Incompatible,
            Reason: compatible
                ? null
                : $"{chosen.FileName} 与实例 {context.Instance.GameVersion}/{context.Instance.Loader} 不兼容。"));
    }

    private async Task<CurseForgeFile?> PickCurseForgeFileAsync(
        CurseForgeMod mod,
        PlanContext context,
        CancellationToken cancellationToken)
    {
        var loader = CurseForgeLoaders.FromInstanceLoader(context.Instance.Loader);
        if (context.Request.Kind == ContentKind.Mod && loader is null)
        {
            return null;
        }

        var candidates = mod.LatestFilesIndexes
            .Where(index => index.FileId > 0)
            .Where(index => context.Request.Kind != ContentKind.Mod || loader is null ||
                            index.ModLoader == (int)loader.Value)
            .Where(index => string.IsNullOrWhiteSpace(context.Instance.GameVersion) ||
                            string.Equals(index.GameVersion, context.Instance.GameVersion, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var indexed = candidates.FirstOrDefault(index => index.ReleaseType == 1) ?? candidates.FirstOrDefault();
        if (indexed is not null)
        {
            var file = await _curseForge.GetFileAsync(mod.Id, indexed.FileId, cancellationToken)
                .ConfigureAwait(false);
            if (file.IsSuccess && file.Value is not null)
            {
                return file.Value;
            }
        }

        var listed = await _curseForge
            .GetFilesForModAsync(mod.Id, context.Instance.GameVersion, loader, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!listed.IsSuccess || listed.Value is null)
        {
            return null;
        }

        return listed.Value
            .Where(file => context.Request.Kind != ContentKind.Mod || string.IsNullOrWhiteSpace(context.Instance.GameVersion) ||
                           file.SupportsGameVersion(context.Instance.GameVersion))
            .Where(file => loader is null || file.SupportsLoader(loader.Value))
            .OrderBy(file => file.ReleaseType == 1 ? 0 : 1)
            .ThenByDescending(file => file.FileDate)
            .FirstOrDefault();
    }

    private async Task<Uri?> ResolveCurseForgeUriAsync(
        CurseForgeFile file,
        CancellationToken cancellationToken)
    {
        var url = file.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            var resolved = await _curseForge
                .GetDownloadUrlAsync(file.ModId, file.Id, cancellationToken)
                .ConfigureAwait(false);
            url = resolved.IsSuccess ? resolved.Value : null;
        }

        return CurseForgeDownloads.TryCreateUri(url, out var uri) ? uri : null;
    }

    private static ContentDependencyNode MissingModrinthDependency(
        string? projectId,
        string? versionId,
        string? fileName,
        string? requestedBy,
        string? reason) => new(
        BuildKey(ContentSourceKind.Modrinth, projectId, versionId, fileName),
        ContentSourceKind.Modrinth,
        projectId,
        versionId,
        FileName: fileName,
        Required: true,
        RequestedBy: requestedBy,
        Status: string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(versionId)
            ? ContentDependencyStatus.Ambiguous
            : ContentDependencyStatus.NotFound,
        Reason: reason ?? "没有与实例版本/加载器兼容的版本。");

    private static ContentDependencyNode MissingCurseForgeDependency(
        int modId,
        string? requestedBy,
        string? reason) => new(
        BuildKey(ContentSourceKind.CurseForge, modId.ToString(), null),
        ContentSourceKind.CurseForge,
        modId.ToString(),
        null,
        Required: true,
        RequestedBy: requestedBy,
        Status: ContentDependencyStatus.NotFound,
        Reason: reason ?? "依赖项目不存在。");

    private static int ReleaseRank(string? versionType) => versionType?.ToLowerInvariant() switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => 3,
    };

    private static bool SafeFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName is not "." and not ".." &&
        string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) &&
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool TryCreateModrinthUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !(string.Equals(parsed.Host, "modrinth.com", StringComparison.OrdinalIgnoreCase) ||
              parsed.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static string BuildKey(
        ContentSourceKind source,
        string? projectId,
        string? versionId,
        string? fileName = null) =>
        $"{source.ToString().ToLowerInvariant()}:{projectId ?? fileName ?? "unknown"}@{versionId ?? "latest"}";

    private sealed record PlanContext(ContentInstallPlanRequest Request, GameInstance Instance);
}
