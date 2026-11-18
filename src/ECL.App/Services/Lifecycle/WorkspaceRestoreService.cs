// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Workspace.Session;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Services.Lifecycle;

/// <summary>会话快照对“现在能不能恢复”的结论。UI 只在 <see cref="Available"/> 时询问用户。</summary>
public enum WorkspaceRestoreAvailability
{
    Available,
    None,
    Unreadable,
    Incompatible,
}

/// <summary>
/// 单个条目的恢复候选。<see cref="CanRestore"/> 为 false 时 <see cref="Reason"/> 是稳定的
/// 原因代码（<c>workspace.restore.reason.*</c>），由界面本地化，不是用户可见的整句。
/// </summary>
public sealed record WorkspaceRestoreCandidate(
    string? ToolId,
    string? InstanceId,
    string? Destination,
    bool CanRestore,
    string? Reason);

public sealed record WorkspaceRestorePlan(
    WorkspaceRestoreAvailability Availability,
    IReadOnlyList<WorkspaceRestoreCandidate> Candidates,
    int Skipped);

public sealed record WorkspaceRestoreFailure(
    string? ToolId,
    string? InstanceId,
    string? Destination,
    string Reason);

/// <summary>
/// 恢复结果。逐项失败不会让整批失败：成功的条目已经恢复，失败项按原因列出，
/// 界面可以只提示失败的那部分。
/// </summary>
public sealed record WorkspaceRestoreResult(
    int Restored,
    int Skipped,
    IReadOnlyList<WorkspaceRestoreFailure> Failures)
{
    public bool IsComplete => Failures.Count == 0;
}

/// <summary>
/// 真正的导航目标（打开标签/页面）由 UI 提供：领域层只决定“恢复哪些条目”，
/// 不创建视图、不操作 Shell。宿主未注册时恢复会逐项记录
/// <c>workspace.restore.target_unavailable</c>，而不是假装已恢复。
/// </summary>
public interface IWorkspaceRestoreTarget
{
    void Restore(WorkspaceSessionEntry entry);
}

/// <summary>
/// 启动时的会话恢复询问（F-12 最小切片）。
///
/// 边界：
/// - 只读会话快照与实例记录，不修改工作区、不创建实例、不打开游戏；
/// - 实例已不存在的条目不会静默跳过，而是标记为不可恢复并给出原因；
/// - 恢复失败逐项记录，不整批失败，也不回滚已经恢复的条目（回滚导航会再次打断用户）。
/// </summary>
public sealed class WorkspaceRestoreService(
    IWorkspaceSessionStore store,
    IInstanceService instances,
    IWorkspaceRestoreTarget? target = null,
    ILogger<WorkspaceRestoreService>? logger = null)
{
    public const string ReasonInstanceMissing = "workspace.restore.reason.instance_missing";
    public const string ReasonEntryEmpty = "workspace.restore.reason.entry_empty";
    public const string ReasonTargetUnavailable = "workspace.restore.reason.target_unavailable";
    public const string ReasonApplyFailed = "workspace.restore.reason.apply_failed";

    /// <summary>
    /// 检查快照。同步入口供启动流程（后台线程）使用：<see cref="IInstanceService"/> 的读取是
    /// 文件系统调用，阻塞时间以毫秒计；UI 线程请改用 <see cref="InspectAsync"/>。
    /// </summary>
    public WorkspaceRestorePlan Inspect()
    {
        var snapshot = store.Load();
        if (snapshot is null)
        {
            return new WorkspaceRestorePlan(MapAvailability(store.LastLoadStatus), [], 0);
        }

        var candidates = new List<WorkspaceRestoreCandidate>(snapshot.Open.Count);
        foreach (var entry in snapshot.Open)
        {
            candidates.Add(BuildCandidate(entry));
        }

        return new WorkspaceRestorePlan(
            WorkspaceRestoreAvailability.Available,
            candidates,
            candidates.Count(candidate => !candidate.CanRestore));
    }

    /// <summary>异步包装，供 UI 线程调用而不阻塞消息循环。</summary>
    public Task<WorkspaceRestorePlan> InspectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Inspect, cancellationToken);

    public async Task<WorkspaceRestoreResult> RestoreAsync(
        WorkspaceRestorePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var restored = 0;
        var skipped = 0;
        var failures = new List<WorkspaceRestoreFailure>();

        foreach (var candidate in plan.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!candidate.CanRestore)
            {
                skipped++;
                continue;
            }

            if (target is null)
            {
                failures.Add(Failure(candidate, ReasonTargetUnavailable));
                continue;
            }

            try
            {
                target.Restore(new WorkspaceSessionEntry(
                    candidate.ToolId,
                    candidate.InstanceId,
                    candidate.Destination));
                restored++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger?.LogWarning(exception, "恢复工作区条目失败：{InstanceId}", candidate.InstanceId);
                failures.Add(Failure(candidate, ReasonApplyFailed));
            }
        }

        return new WorkspaceRestoreResult(restored, skipped, failures);
    }

    private WorkspaceRestoreCandidate BuildCandidate(WorkspaceSessionEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ToolId) &&
            string.IsNullOrWhiteSpace(entry.InstanceId) &&
            string.IsNullOrWhiteSpace(entry.Destination))
        {
            // 空条目没有任何可恢复目标：跳过并计数，不把它当成“恢复成功”。
            return new WorkspaceRestoreCandidate(
                entry.ToolId,
                entry.InstanceId,
                entry.Destination,
                CanRestore: false,
                ReasonEntryEmpty);
        }

        if (string.IsNullOrWhiteSpace(entry.InstanceId))
        {
            // 工具页/固定目的地不依赖实例记录，直接可恢复。
            return new WorkspaceRestoreCandidate(
                entry.ToolId,
                entry.InstanceId,
                entry.Destination,
                CanRestore: true,
                Reason: null);
        }

        var instance = instances.GetAsync(entry.InstanceId).GetAwaiter().GetResult();
        return instance is null
            ? new WorkspaceRestoreCandidate(
                entry.ToolId,
                entry.InstanceId,
                entry.Destination,
                CanRestore: false,
                ReasonInstanceMissing)
            : new WorkspaceRestoreCandidate(
                entry.ToolId,
                entry.InstanceId,
                entry.Destination,
                CanRestore: true,
                Reason: null);
    }

    private static WorkspaceRestoreFailure Failure(WorkspaceRestoreCandidate candidate, string reason) =>
        new(candidate.ToolId, candidate.InstanceId, candidate.Destination, reason);

    private static WorkspaceRestoreAvailability MapAvailability(WorkspaceSessionLoadStatus status) => status switch
    {
        WorkspaceSessionLoadStatus.Available => WorkspaceRestoreAvailability.Available,
        WorkspaceSessionLoadStatus.Unreadable => WorkspaceRestoreAvailability.Unreadable,
        WorkspaceSessionLoadStatus.Incompatible => WorkspaceRestoreAvailability.Incompatible,
        _ => WorkspaceRestoreAvailability.None,
    };
}
