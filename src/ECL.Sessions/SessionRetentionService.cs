// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Sessions;

public sealed record SessionPrunePlan(
    IReadOnlyList<string> SessionIds,
    long Bytes,
    IReadOnlyList<string> ProtectedReasons)
{
    public bool IsEmpty => SessionIds.Count == 0;
}

public interface ISessionRetentionService
{
    SessionPrunePlan Plan(TimeSpan minimumAge);

    Result Apply(SessionPrunePlan plan);
}

/// <summary>
/// 会话保留策略（ADR 0015 §8）：只有已完成/已中断且超过保留期的会话才允许物理清理。
/// 运行中、状态未知或仍被引用的会话永不删除；清理必须由用户显式发起。
/// </summary>
public sealed class SessionRetentionService : ISessionRetentionService
{
    private readonly GameSessionStore _store;
    private readonly ILogger<SessionRetentionService> _logger;

    public SessionRetentionService(IAppPaths paths, ILogger<SessionRetentionService> logger)
    {
        _store = new GameSessionStore(paths);
        _logger = logger;
    }

    public SessionPrunePlan Plan(TimeSpan minimumAge)
    {
        var cutoff = DateTimeOffset.UtcNow - minimumAge;
        var candidates = new List<string>();
        var reasons = new List<string>();
        long bytes = 0;

        foreach (var session in _store.LoadAll())
        {
            var manifest = session.Manifest;
            if (manifest.State is GameSessionState.Running
                or GameSessionState.Starting
                or GameSessionState.GameExitedDraining
                or GameSessionState.RecoveryPending
                or GameSessionState.ProcessStateUnknown
                or GameSessionState.Preparing)
            {
                reasons.Add($"{session.SessionId}: 状态 {manifest.State} 不允许清理");
                continue;
            }

            if (manifest.CreatedAtUtc > cutoff)
            {
                continue;
            }

            candidates.Add(session.SessionId);
            bytes += DirectorySize(session.DirectoryPath);
        }

        return new SessionPrunePlan(candidates, bytes, reasons);
    }

    public Result Apply(SessionPrunePlan plan)
    {
        if (plan.IsEmpty)
        {
            return Result.Ok();
        }

        foreach (var sessionId in plan.SessionIds)
        {
            if (!GameSessionStore.IsSafeSessionId(sessionId))
            {
                return Result.Fail("session.prune.invalid_id", $"非法的会话 ID：{sessionId}");
            }

            var session = _store.Load(sessionId);
            if (session is null)
            {
                continue;
            }

            // 计划生成后状态可能变化：执行前重新检查，避免删除刚变得活跃的会话。
            if (session.Manifest.State is not (
                GameSessionState.CaptureCompleted
                or GameSessionState.CaptureIncomplete
                or GameSessionState.Interrupted))
            {
                _logger.LogWarning("跳过状态已变化的会话：{SessionId}（{State}）", sessionId, session.Manifest.State);
                continue;
            }

            try
            {
                Directory.Delete(session.DirectoryPath, recursive: true);
                _logger.LogInformation("已清理会话 {SessionId}。", sessionId);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Result.Fail(
                    "session.prune.failed",
                    $"清理会话 {sessionId} 失败：{exception.Message}");
            }
        }

        return Result.Ok();
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length)
                : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
