// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Ecl.Core;
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

    public string ComputeHash() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '|',
            InstanceId,
            GameVersion ?? string.Empty,
            Loader ?? string.Empty,
            Source,
            RootKey,
            PolicyVersion,
            string.Join(";", Files.Select(file => $"{file.Key}:{file.VersionId}:{file.FileName}"))))));
}

/// <summary>计划请求：只接受稳定身份（项目 ID + 可选版本 ID），不接受“按文件名找最新”。</summary>
public sealed record ContentInstallPlanRequest(
    ContentSourceKind Source,
    string ProjectId,
    string? VersionId = null,
    bool IncludeOptionalDependencies = false);

public interface IContentInstallPlanner
{
    /// <summary>生成只读安装计划；不下载、不写实例目录、不修改任何内容。</summary>
    Task<Result<ContentInstallPlan>> PlanAsync(
        string instanceId,
        ContentInstallPlanRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// F-06 S1 依赖图解析：复用现有 <see cref="IModrinthClient"/> 身份 API，把“模组 → 必需依赖”
/// 展开成显式图（节点 + 边 + 状态），并给出可安装文件清单。
/// CurseForge 计划与安装入口统一消费本计划属于后续切片，见 F-06 规格 §10。
/// </summary>
public sealed class ContentInstallPlanner : IContentInstallPlanner
{
    private const int MaxDepth = 4;
    private const int MaxNodes = 128;

    private readonly IInstanceService _instances;
    private readonly IModrinthClient _modrinth;
    private readonly ILogger<ContentInstallPlanner> _logger;
    private readonly TimeProvider _time;

    public ContentInstallPlanner(
        IInstanceService instances,
        IModrinthClient modrinth,
        ILogger<ContentInstallPlanner>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _modrinth = modrinth;
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

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ContentInstallPlan>.Fail("instance.not_found", "实例不存在。");
        }

        if (request.Source != ContentSourceKind.Modrinth)
        {
            return Result<ContentInstallPlan>.Fail(
                "content.plan.unsupported_source",
                "当前只实现 Modrinth 依赖图计划；CurseForge 计划属后续切片，不在此假装可用。");
        }

        var nodes = new List<ContentDependencyNode>();
        var edges = new List<ContentDependencyEdge>();
        var blockers = new List<string>();
        var warnings = new List<string>();
        var incompatible = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var rootNode = request.VersionId is null
            ? await ResolveLatestAsync(
                request.ProjectId,
                instance,
                requestedBy: null,
                cancellationToken).ConfigureAwait(false)
            : await ResolveVersionAsync(
                request.VersionId,
                instance,
                requestedBy: null,
                cancellationToken).ConfigureAwait(false);
        if (rootNode is null)
        {
            return Result<ContentInstallPlan>.Fail(
                "content.plan.not_found",
                "没有找到与实例版本/加载器兼容的内容版本。");
        }

        nodes.Add(rootNode);
        visited.Add(rootNode.Key);
        if (rootNode.Status == ContentDependencyStatus.Incompatible)
        {
            blockers.Add("content.plan.incompatible");
        }

        if (rootNode.Status is not (ContentDependencyStatus.Resolved or ContentDependencyStatus.Incompatible))
        {
            blockers.Add("content.plan.unresolved_root");
        }

        var queue = new Queue<(ContentDependencyNode Node, int Depth)>();
        queue.Enqueue((rootNode, 0));
        var versionCache = new Dictionary<string, ModrinthVersion?>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = queue.Dequeue();
            if (current.VersionId is null || depth >= MaxDepth)
            {
                continue;
            }

            if (!versionCache.TryGetValue(current.VersionId, out var version))
            {
                var fetched = await _modrinth.GetVersionAsync(current.VersionId, cancellationToken)
                    .ConfigureAwait(false);
                version = fetched.IsSuccess ? fetched.Value : null;
                versionCache[current.VersionId] = version;
            }

            if (version is null)
            {
                warnings.Add($"无法读取 {current.Key} 的依赖列表；结果可能不完整。");
                continue;
            }

            foreach (var dependency in version.Dependencies)
            {
                var required = string.Equals(dependency.DependencyType, "required", StringComparison.OrdinalIgnoreCase);
                if (!required && !request.IncludeOptionalDependencies)
                {
                    var optionalKey = BuildKey(ContentSourceKind.Modrinth, dependency.ProjectId, dependency.VersionId);
                    edges.Add(new ContentDependencyEdge(current.Key, optionalKey, Required: false, dependency.DependencyType));
                    continue;
                }

                string? dependencyVersionId = dependency.VersionId;
                ContentDependencyNode? node;
                if (!string.IsNullOrWhiteSpace(dependencyVersionId))
                {
                    node = await ResolveVersionAsync(
                        dependencyVersionId,
                        instance,
                        current.Key,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(dependency.ProjectId))
                {
                    node = await ResolveLatestAsync(
                        dependency.ProjectId!,
                        instance,
                        current.Key,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    node = new ContentDependencyNode(
                        BuildKey(ContentSourceKind.Modrinth, null, null, dependency.FileName),
                        ContentSourceKind.Modrinth,
                        null,
                        null,
                        FileName: dependency.FileName,
                        Required: required,
                        RequestedBy: current.Key,
                        Status: ContentDependencyStatus.Ambiguous,
                        Reason: "依赖只给出文件名，无法确定稳定身份。");
                }

                if (node is null)
                {
                    var missingKey = BuildKey(
                        ContentSourceKind.Modrinth,
                        dependency.ProjectId,
                        dependency.VersionId,
                        dependency.FileName);
                    nodes.Add(new ContentDependencyNode(
                        missingKey,
                        ContentSourceKind.Modrinth,
                        dependency.ProjectId,
                        dependency.VersionId,
                        FileName: dependency.FileName,
                        Required: required,
                        RequestedBy: current.Key,
                        Status: ContentDependencyStatus.NotFound,
                        Reason: "没有与实例版本/加载器兼容的版本。"));
                    edges.Add(new ContentDependencyEdge(current.Key, missingKey, required, dependency.DependencyType));
                    if (required)
                    {
                        blockers.Add("content.plan.unresolved_dependency");
                    }

                    continue;
                }

                edges.Add(new ContentDependencyEdge(current.Key, node.Key, required, dependency.DependencyType));
                if (visited.Add(node.Key))
                {
                    nodes.Add(node);
                    if (nodes.Count > MaxNodes)
                    {
                        blockers.Add("content.plan.limit_reached");
                        warnings.Add($"依赖节点超过 {MaxNodes} 个，计划被截断；不得按截断结果宣称完整。");
                        queue.Clear();
                        break;
                    }

                    if (node.Status == ContentDependencyStatus.Incompatible)
                    {
                        incompatible.Add(node.Key);
                        if (required)
                        {
                            blockers.Add("content.plan.incompatible_dependency");
                        }
                    }
                    else if (node.Status == ContentDependencyStatus.NotFound && required)
                    {
                        blockers.Add("content.plan.unresolved_dependency");
                    }

                    if (node.Status == ContentDependencyStatus.Resolved)
                    {
                        queue.Enqueue((node, depth + 1));
                    }
                }
            }
        }

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
            rootNode.Key,
            nodes,
            edges,
            distinctBlockers,
            warnings,
            incompatible,
            "f06-1");
        _logger.LogInformation(
            "已生成内容计划 {PlanId}：实例 {InstanceId}，节点 {Nodes}，可安装文件 {Files}，阻断 {Blockers}。",
            plan.PlanId,
            instance.Id,
            plan.Nodes.Count,
            plan.Files.Count,
            plan.Blockers.Count);
        return Result<ContentInstallPlan>.Ok(plan);
    }

    private async Task<ContentDependencyNode?> ResolveVersionAsync(
        string versionId,
        GameInstance instance,
        string? requestedBy,
        CancellationToken cancellationToken)
    {
        var fetched = await _modrinth.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsSuccess || fetched.Value is null)
        {
            return null;
        }

        return ToNode(fetched.Value, instance, requestedBy, required: true);
    }

    private async Task<ContentDependencyNode?> ResolveLatestAsync(
        string projectId,
        GameInstance instance,
        string? requestedBy,
        CancellationToken cancellationToken)
    {
        var versions = await _modrinth
            .GetVersionsAsync(projectId, instance.GameVersion, instance.Loader, cancellationToken)
            .ConfigureAwait(false);
        if (!versions.IsSuccess)
        {
            return null;
        }

        var compatible = versions.Value!
            .Where(version => version.SupportsGameVersion(instance.GameVersion) &&
                              version.SupportsLoader(instance.Loader))
            .OrderByDescending(version => version.DatePublished ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        return compatible is null ? null : ToNode(compatible, instance, requestedBy, required: true);
    }

    private static ContentDependencyNode ToNode(
        ModrinthVersion version,
        GameInstance instance,
        string? requestedBy,
        bool required)
    {
        var key = BuildKey(ContentSourceKind.Modrinth, version.ProjectId, version.Id);
        var compatible = version.SupportsGameVersion(instance.GameVersion) && version.SupportsLoader(instance.Loader);
        var file = version.PrimaryFile;
        return new ContentDependencyNode(
            key,
            ContentSourceKind.Modrinth,
            version.ProjectId,
            version.Id,
            ProjectName: null,
            VersionNumber: version.VersionNumber,
            FileName: file?.Filename,
            DownloadUrl: compatible ? file?.Url : null,
            Sha1: file?.Hashes.Sha1,
            Size: file?.Size,
            Required: required,
            RequestedBy: requestedBy,
            Status: compatible ? ContentDependencyStatus.Resolved : ContentDependencyStatus.Incompatible,
            Reason: compatible
                ? null
                : $"需要 MC {string.Join('/', version.GameVersions)} / 加载器 {string.Join('/', version.Loaders)}，实例为 {instance.GameVersion}/{instance.Loader}。");
    }

    private static string BuildKey(
        ContentSourceKind source,
        string? projectId,
        string? versionId,
        string? fileName = null) =>
        $"{source.ToString().ToLowerInvariant()}:{projectId ?? fileName ?? "unknown"}@{versionId ?? "latest"}";
}
