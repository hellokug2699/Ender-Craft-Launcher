// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Ecl.Core;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;

namespace Ecl.Minecraft.Engine.Lifecycle;

public sealed record InstanceActivityGuardState(
    string InstanceId,
    bool Exists,
    bool HasActiveSession,
    string? SessionId,
    int? ProcessId,
    bool ProcessAlive,
    bool HasPendingTransaction,
    InstanceActivityState TransactionState,
    bool IdentityConflict,
    string? Detail);

/// <summary>
/// 实例生命周期活动门禁（F-03 §5.2）：删除/移动/重配置/重定位必须在领域层检查
/// 活动游戏会话、事务状态与身份冲突；UI 禁用按钮不是一致性边界。
/// </summary>
public interface IInstanceActivityGuard
{
    Task<Result<InstanceActivityGuardState>> InspectAsync(
        string instanceId,
        CancellationToken cancellationToken = default);
}

public sealed class InstanceActivityGuard(
    IInstanceService instances,
    IGameSessionService sessions,
    InstanceTransactionCoordinator transactions) : IInstanceActivityGuard
{
    private static readonly GameSessionState[] ActiveStates =
    [
        GameSessionState.Preparing,
        GameSessionState.Starting,
        GameSessionState.Running,
    ];

    public async Task<Result<InstanceActivityGuardState>> InspectAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<InstanceActivityGuardState>.Fail("instance.invalid_id", "实例 ID 不合法。");
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<InstanceActivityGuardState>.Ok(new InstanceActivityGuardState(
                instanceId,
                Exists: false,
                HasActiveSession: false,
                SessionId: null,
                ProcessId: null,
                ProcessAlive: false,
                HasPendingTransaction: false,
                InstanceActivityState.Missing,
                IdentityConflict: false,
                Detail: "实例不存在。"));
        }

        string? sessionId = null;
        int? processId = null;
        var alive = false;
        foreach (var session in sessions.List())
        {
            var manifest = session.Manifest;
            if (!string.Equals(manifest.InstanceId, instanceId, StringComparison.Ordinal) ||
                !ActiveStates.Contains(manifest.State))
            {
                continue;
            }

            sessionId = manifest.SessionId;
            processId = manifest.ProcessId;
            alive = IsAlive(manifest.ProcessId);
            if (alive || manifest.ProcessId is null)
            {
                break;
            }
        }

        var hasActiveSession = sessionId is not null && (alive || processId is null);
        var activity = await transactions.GetActivityStateAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var transactionState = activity.IsSuccess ? activity.Value : InstanceActivityState.Idle;
        var pendingTransaction = transactionState is
            InstanceActivityState.Writing or
            InstanceActivityState.Recovering or
            InstanceActivityState.NeedsRecovery or
            InstanceActivityState.Changing;
        var identityConflict = transactionState == InstanceActivityState.IdentityConflict;

        return Result<InstanceActivityGuardState>.Ok(new InstanceActivityGuardState(
            instanceId,
            Exists: true,
            HasActiveSession: hasActiveSession,
            SessionId: hasActiveSession ? sessionId : null,
            ProcessId: processId,
            ProcessAlive: alive,
            HasPendingTransaction: pendingTransaction,
            transactionState,
            IdentityConflict: identityConflict,
            Detail: hasActiveSession
                ? "实例有活动游戏会话。"
                : pendingTransaction
                    ? $"实例事务状态：{transactionState}。"
                    : identityConflict
                        ? "实例身份存在冲突。"
                        : null));
    }

    private static bool IsAlive(int? processId)
    {
        if (processId is not { } id || id <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
