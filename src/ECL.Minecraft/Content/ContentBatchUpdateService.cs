// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Ecl.Core;
using Ecl.Minecraft.CurseForge;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Content;

/// <summary>批量更新中单个内容项的状态；失败与“未尝试”必须分开表达，不得折叠为成功。</summary>
public enum ContentBatchUpdateItemStatus
{
    /// <summary>计划已生成且可执行，但尚未应用（预览状态）。</summary>
    Ready = 0,

    Updated = 1,

    /// <summary>选择集合中的项目没有可证明来源的已装记录或没有候选版本。</summary>
    NoCandidate = 2,

    /// <summary>实例策略不允许更新（NotifyOnly/Block），领域层拒绝执行。</summary>
    BlockedByPolicy = 3,

    /// <summary>生成安装计划失败（网络/来源/身份错误）。</summary>
    PlanFailed = 4,

    /// <summary>计划存在阻断项（缺依赖/不兼容），未执行任何写入。</summary>
    PlanBlocked = 5,

    /// <summary>下载或事务执行失败；按 F-04 语义回滚到旧文件。</summary>
    InstallFailed = 6,

    /// <summary>之前的项失败/取消后未再尝试。</summary>
    NotAttempted = 7,

    /// <summary>执行中被取消；取消点只在项与项之间或下载阶段。</summary>
    Canceled = 8,
}

/// <summary>批量更新选择：空或 <c>null</c> 表示“所有当前可更新项”；否则只处理给定稳定项目 ID。</summary>
public sealed record ContentBatchUpdateSelection(IReadOnlyList<string>? ProjectIds = null)
{
    public bool SelectsAll => ProjectIds is null || ProjectIds.Count == 0;
}

/// <summary>批量更新中的单个条目：候选事实、策略结论、计划与执行结果。</summary>
public sealed record ContentBatchUpdateItem
{
    public required string ProjectId { get; init; }

    public required string FileName { get; init; }

    public string? LatestVersionId { get; init; }

    public string? LatestVersionNumber { get; init; }

    public string? LatestFileName { get; init; }

    public ContentBatchUpdateItemStatus Status { get; init; }

    public string? Reason { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? PlanId { get; init; }

    /// <summary>预览用的只读安装计划；不下载、不落盘。JSON 输出时忽略，避免把完整计划当安装依据。</summary>
    [JsonIgnore]
    public ContentInstallPlan? Plan { get; init; }

    public IReadOnlyList<string> InstalledFiles { get; init; } = [];

    public IReadOnlyList<string> RemovedFiles { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool Succeeded => Status == ContentBatchUpdateItemStatus.Updated;
}

/// <summary>批量更新计划：检查结果 + 每项计划结论；不执行任何下载或写入。</summary>
public sealed record ContentBatchUpdatePlan(
    string InstanceId,
    InstanceContentUpdatePolicy Policy,
    DateTimeOffset CheckedAt,
    IReadOnlyList<ContentBatchUpdateItem> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Skipped)
{
    public bool PolicyAllowsUpdate => Policy == InstanceContentUpdatePolicy.Allow;

    public int ReadyCount => Items.Count(item => item.Status == ContentBatchUpdateItemStatus.Ready);

    public bool HasApplicableItems => PolicyAllowsUpdate && ReadyCount > 0;
}

/// <summary>批量更新执行结果：逐项状态与真实文件写入/删除清单。</summary>
public sealed record ContentBatchUpdateResult(
    string InstanceId,
    InstanceContentUpdatePolicy Policy,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool Canceled,
    IReadOnlyList<ContentBatchUpdateItem> Items,
    IReadOnlyList<string> Warnings)
{
    public int UpdatedCount => Items.Count(item => item.Status == ContentBatchUpdateItemStatus.Updated);

    public int FailedCount => Items.Count(item =>
        item.Status is ContentBatchUpdateItemStatus.PlanFailed
            or ContentBatchUpdateItemStatus.PlanBlocked
            or ContentBatchUpdateItemStatus.InstallFailed);

    public int BlockedCount => Items.Count(item => item.Status == ContentBatchUpdateItemStatus.BlockedByPolicy);

    public int NotAttemptedCount => Items.Count(item =>
        item.Status is ContentBatchUpdateItemStatus.NotAttempted or ContentBatchUpdateItemStatus.Canceled);

    /// <summary>只有全部计划内的项都成功更新时才为真；空计划不算成功。</summary>
    public bool AllSucceeded => Items.Count > 0 && UpdatedCount == Items.Count && !Canceled;
}

public sealed record ContentBatchUpdateProgress(string ProjectId, int Index, int Total, string Stage);

public interface IContentBatchUpdateService
{
    /// <summary>检查更新并为选择集合生成安装计划；不下载、不写实例目录、不修改安装记录。</summary>
    Task<Result<ContentBatchUpdatePlan>> PlanAsync(
        string instanceId,
        ContentBatchUpdateSelection? selection = null,
        CancellationToken cancellationToken = default);

    /// <summary>执行计划；每一项都按最新实例策略与计划前置校验重新验证，失败逐项报告。</summary>
    Task<Result<ContentBatchUpdateResult>> ApplyAsync(
        ContentBatchUpdatePlan plan,
        IProgress<ContentBatchUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// F-06 S5 批量更新：检查 → 逐项计划 → 逐项执行（唯一执行器 + 实例事务）→ 更新安装记录。
/// <para>
/// 策略门禁：计划阶段与执行阶段都读取实例策略；<c>Block</c>/<c>NotifyOnly</c> 下不会生成可执行计划、
/// 不会下载、不会写入，任何调用方都无法用一次确认绕过。
/// </para>
/// <para>
/// 并发：同一进程内按实例串行化批量执行；跨进程并发仍由 F-04 实例写门在事务点串行化
/// （下载阶段不持写门），跨进程记录竞争仍是交接中记录的已知限制。
/// </para>
/// </summary>
public sealed class ContentBatchUpdateService : IContentBatchUpdateService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ApplyLocks = new(StringComparer.Ordinal);

    private readonly IInstanceService _instances;
    private readonly IContentUpdateService _updates;
    private readonly IInstanceContentUpdatePolicyService _policy;
    private readonly IContentInstallPlanner _planner;
    private readonly IContentPlanExecutor _executor;
    private readonly ICurseForgeInstallRecordStore _records;
    private readonly ILogger<ContentBatchUpdateService> _logger;
    private readonly TimeProvider _time;

    public ContentBatchUpdateService(
        IInstanceService instances,
        IContentUpdateService updates,
        IInstanceContentUpdatePolicyService policy,
        IContentInstallPlanner planner,
        IContentPlanExecutor executor,
        ICurseForgeInstallRecordStore records,
        ILogger<ContentBatchUpdateService>? logger = null,
        TimeProvider? time = null)
    {
        _instances = instances;
        _updates = updates;
        _policy = policy;
        _planner = planner;
        _executor = executor;
        _records = records;
        _logger = logger ?? NullLogger<ContentBatchUpdateService>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<ContentBatchUpdatePlan>> PlanAsync(
        string instanceId,
        ContentBatchUpdateSelection? selection = null,
        CancellationToken cancellationToken = default)
    {
        selection ??= new ContentBatchUpdateSelection();
        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ContentBatchUpdatePlan>.Fail("instance.not_found", "实例不存在。");
        }

        var checkedResult = await _updates.CheckAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!checkedResult.IsSuccess)
        {
            return Result<ContentBatchUpdatePlan>.Fail(checkedResult.Error!);
        }

        var check = checkedResult.Value!;
        var warnings = new List<string>(check.Warnings);
        var skipped = new List<string>(check.Skipped);
        var items = new List<ContentBatchUpdateItem>();

        var candidates = Select(check.Candidates, selection, items, warnings);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await PlanItemAsync(instance, check.Policy, candidate, cancellationToken)
                .ConfigureAwait(false));
        }

        var plan = new ContentBatchUpdatePlan(
            instanceId,
            check.Policy,
            check.CheckedAt,
            items,
            warnings,
            skipped);

        _logger.LogInformation(
            "批量更新计划：实例 {InstanceId}，策略 {Policy}，候选 {Candidates}，可执行 {Ready}，警告 {Warnings}。",
            instanceId,
            plan.Policy,
            items.Count,
            plan.ReadyCount,
            warnings.Count);
        return Result<ContentBatchUpdatePlan>.Ok(plan);
    }

    public async Task<Result<ContentBatchUpdateResult>> ApplyAsync(
        ContentBatchUpdatePlan plan,
        IProgress<ContentBatchUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var instance = await _instances.GetAsync(plan.InstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ContentBatchUpdateResult>.Fail("instance.not_found", "实例不存在。");
        }

        var gate = ApplyLocks.GetOrAdd(plan.InstanceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyCoreAsync(instance, plan, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Result<ContentBatchUpdateResult>> ApplyCoreAsync(
        GameInstance instance,
        ContentBatchUpdatePlan plan,
        IProgress<ContentBatchUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow();
        var warnings = new List<string>(plan.Warnings);
        var results = new List<ContentBatchUpdateItem>(plan.Items.Count);

        // 执行阶段重新读取策略：计划生成后策略可能已改为 Block/NotifyOnly。
        var policyResult = await _policy.GetAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        if (!policyResult.IsSuccess)
        {
            return Result<ContentBatchUpdateResult>.Fail(policyResult.Error!);
        }

        var policy = policyResult.Value;
        var canceled = false;
        var index = 0;
        foreach (var item in plan.Items)
        {
            index++;
            if (canceled)
            {
                results.Add(item with { Status = ContentBatchUpdateItemStatus.NotAttempted });
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                results.Add(item with { Status = ContentBatchUpdateItemStatus.Canceled });
                continue;
            }

            if (policy != InstanceContentUpdatePolicy.Allow)
            {
                results.Add(item with
                {
                    Status = ContentBatchUpdateItemStatus.BlockedByPolicy,
                    Reason = policy == InstanceContentUpdatePolicy.Block ? "policy.block" : "policy.notify",
                    ErrorCode = "content.policy.blocked",
                    ErrorMessage = "实例更新策略不允许执行批量更新。",
                });
                continue;
            }

            if (item.Status != ContentBatchUpdateItemStatus.Ready || item.Plan is null)
            {
                results.Add(item);
                continue;
            }

            progress?.Report(new ContentBatchUpdateProgress(item.ProjectId, index, plan.Items.Count, "apply"));
            results.Add(await ApplyItemAsync(instance, item, index, plan.Items.Count, progress, warnings, cancellationToken)
                .ConfigureAwait(false));
        }

        // 取消可能发生在下载中途或最后一项之后：以真实项状态为准，不隐藏部分完成。
        canceled |= results.Any(result => result.Status == ContentBatchUpdateItemStatus.Canceled);

        var completedAt = _time.GetUtcNow();
        var result = new ContentBatchUpdateResult(
            instance.Id,
            policy,
            startedAt,
            completedAt,
            canceled,
            results,
            warnings);

        _logger.LogInformation(
            "批量更新执行完成：实例 {InstanceId}，策略 {Policy}，更新 {Updated}，失败 {Failed}，阻断 {Blocked}，取消 {Canceled}。",
            instance.Id,
            policy,
            result.UpdatedCount,
            result.FailedCount,
            result.BlockedCount,
            result.Canceled);
        return Result<ContentBatchUpdateResult>.Ok(result);
    }

    private async Task<ContentBatchUpdateItem> ApplyItemAsync(
        GameInstance instance,
        ContentBatchUpdateItem item,
        int index,
        int total,
        IProgress<ContentBatchUpdateProgress>? progress,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var executed = await _executor
                .ExecuteAsync(
                    item.Plan!,
                    new ContentUpdateReplacement([item.FileName]),
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!executed.IsSuccess)
            {
                var error = executed.Error!;
                warnings.Add($"{item.FileName}：更新失败（{error.Code}）：{error.Message}");
                return item with
                {
                    Status = ContentBatchUpdateItemStatus.InstallFailed,
                    ErrorCode = error.Code,
                    ErrorMessage = error.Message,
                };
            }

            var itemWarnings = new List<string>(item.Warnings);
            itemWarnings.AddRange(executed.Value!.Warnings);

            // 文件事务提交成功后再更新安装记录；记录失败不回滚已提交文件，但必须如实提示。
            var recordUpdate = ApplyRecord(instance, item);
            if (!recordUpdate.IsSuccess)
            {
                itemWarnings.Add(recordUpdate.Error!.Message);
                warnings.Add($"{item.FileName}：{recordUpdate.Error.Message}");
            }

            progress?.Report(new ContentBatchUpdateProgress(item.ProjectId, index, total, "done"));
            return item with
            {
                Status = ContentBatchUpdateItemStatus.Updated,
                PlanId = item.Plan!.PlanId,
                InstalledFiles = executed.Value.InstalledFiles,
                RemovedFiles = executed.Value.RemovedFiles ?? [],
                Warnings = itemWarnings,
                Reason = recordUpdate.IsSuccess ? "updated" : "updated.record_not_written",
            };
        }
        catch (OperationCanceledException)
        {
            return item with { Status = ContentBatchUpdateItemStatus.Canceled };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 执行器内部的意外异常不能中断整个批次，但必须标记为失败而不是成功。
            _logger.LogWarning(exception, "批量更新项执行异常：{ProjectId}", item.ProjectId);
            warnings.Add($"{item.FileName}：更新异常：{exception.Message}");
            return item with
            {
                Status = ContentBatchUpdateItemStatus.InstallFailed,
                ErrorCode = "content.update.failed",
                ErrorMessage = exception.Message,
            };
        }
    }

    private Result ApplyRecord(GameInstance instance, ContentBatchUpdateItem item)
    {
        if (!int.TryParse(item.ProjectId, out var projectId) ||
            !int.TryParse(item.LatestVersionId, out var fileId) ||
            string.IsNullOrWhiteSpace(item.LatestFileName))
        {
            return Result.Fail(
                "curseforge.record_invalid",
                $"无法为 {item.FileName} 写入安装记录：缺少稳定项目/文件 ID。");
        }

        return _records.Apply(instance, [new CurseForgeInstallRecord(projectId, fileId, item.LatestFileName!)]);
    }

    private async Task<ContentBatchUpdateItem> PlanItemAsync(
        GameInstance instance,
        InstanceContentUpdatePolicy policy,
        ContentUpdateCandidate candidate,
        CancellationToken cancellationToken)
    {
        var baseline = new ContentBatchUpdateItem
        {
            ProjectId = candidate.Installed.ProjectId,
            FileName = candidate.Installed.FileName,
            LatestVersionId = candidate.LatestVersionId,
            LatestVersionNumber = candidate.LatestVersionNumber,
            LatestFileName = candidate.LatestFileName,
        };

        if (!candidate.Allowed || policy != InstanceContentUpdatePolicy.Allow)
        {
            return baseline with
            {
                Status = ContentBatchUpdateItemStatus.BlockedByPolicy,
                Reason = candidate.Reason,
            };
        }

        var planned = await _planner
            .PlanAsync(
                instance.Id,
                new ContentInstallPlanRequest(
                    ContentSourceKind.CurseForge,
                    candidate.Installed.ProjectId,
                    candidate.LatestVersionId,
                    IncludeOptionalDependencies: false,
                    IncludeRequiredDependencies: true,
                    Kind: ContentKind.Mod),
                cancellationToken)
            .ConfigureAwait(false);
        if (!planned.IsSuccess)
        {
            var error = planned.Error!;
            return baseline with
            {
                Status = ContentBatchUpdateItemStatus.PlanFailed,
                ErrorCode = error.Code,
                ErrorMessage = error.Message,
            };
        }

        var plan = planned.Value!;
        if (!plan.IsInstallable)
        {
            return baseline with
            {
                Status = ContentBatchUpdateItemStatus.PlanBlocked,
                PlanId = plan.PlanId,
                ErrorCode = "content.plan.blocked",
                ErrorMessage = string.Join("; ", plan.Blockers),
                Warnings = plan.Warnings,
                Plan = plan,
            };
        }

        if (plan.Root is null ||
            !string.Equals(plan.Root.VersionId, candidate.LatestVersionId, StringComparison.Ordinal))
        {
            return baseline with
            {
                Status = ContentBatchUpdateItemStatus.PlanBlocked,
                PlanId = plan.PlanId,
                ErrorCode = "content.plan.version_mismatch",
                ErrorMessage = "计划解析出的版本与更新检查候选不一致，已拒绝执行。",
                Warnings = plan.Warnings,
                Plan = plan,
            };
        }

        return baseline with
        {
            Status = ContentBatchUpdateItemStatus.Ready,
            PlanId = plan.PlanId,
            Plan = plan,
            Warnings = plan.Warnings,
        };
    }

    private static IReadOnlyList<ContentUpdateCandidate> Select(
        IReadOnlyList<ContentUpdateCandidate> candidates,
        ContentBatchUpdateSelection selection,
        List<ContentBatchUpdateItem> missing,
        List<string> warnings)
    {
        if (selection.SelectsAll)
        {
            return candidates;
        }

        var selected = new List<ContentUpdateCandidate>();
        foreach (var projectId in selection.ProjectIds!.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var match = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Installed.ProjectId, projectId, StringComparison.Ordinal));
            if (match is null)
            {
                missing.Add(new ContentBatchUpdateItem
                {
                    ProjectId = projectId,
                    FileName = string.Empty,
                    Status = ContentBatchUpdateItemStatus.NoCandidate,
                    Reason = "content.update.no_candidate",
                    ErrorCode = "content.update.no_candidate",
                    ErrorMessage = "该实例没有可证明来源的更新候选。",
                });
                continue;
            }

            selected.Add(match);
        }

        warnings.Add("只处理显式选择的项目；未选择的候选不会下载或写入。");
        return selected;
    }
}
