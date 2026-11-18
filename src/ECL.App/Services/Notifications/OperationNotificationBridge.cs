// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Operations;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Services.Notifications;

/// <summary>
/// 把任务中心的快照变化映射成应用内通知（F-12）。
///
/// 映射规则（只表达事实，不推断业务结论）：
/// - <see cref="OperationState.Completed"/> → 完成通知（可被“任务完成”偏好关闭）；
/// - <see cref="OperationState.Failed"/> → 失败通知（可被“任务失败”偏好关闭，但日志与任务状态仍保留）；
/// - 暂停 / 停滞 / 重试 → 后台任务提示，属于“进行中”的补充信息，按 (任务, 状态) 节流 30 秒；
/// - <see cref="OperationState.Cancelled"/> 不提示：用户主动取消时再弹提示是噪音。
///
/// 线程契约：<see cref="IOperationStore.Changed"/> 可能来自任意线程；本类只在锁内维护节流状态，
/// 发布本身不封送线程。UI 线程封送由订阅方（<see cref="INotificationService.Published"/> 的宿主）负责。
/// 本类不取消、不重试、不修改任务，只观察。
/// </summary>
public sealed class OperationNotificationBridge : IDisposable
{
    /// <summary>同一任务同一状态的最短提示间隔；避免进度抖动时刷屏。</summary>
    public static readonly TimeSpan BackgroundThrottle = TimeSpan.FromSeconds(30);

    private const int MaxThrottleEntries = 200;

    private readonly IOperationStore _store;
    private readonly INotificationService _notifications;
    private readonly ILogger<OperationNotificationBridge> _logger;
    private readonly ILocalizer? _localizer;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid OperationId, string State), DateTimeOffset> _backgroundThrottle = [];
    private bool _disposed;

    public OperationNotificationBridge(
        IOperationStore store,
        INotificationService notifications,
        ILogger<OperationNotificationBridge> logger,
        ILocalizer? localizer = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _localizer = localizer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _store.Changed += OnChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnChanged;
        lock (_gate)
        {
            _backgroundThrottle.Clear();
        }
    }

    private void OnChanged(object? sender, OperationSnapshot snapshot)
    {
        if (_disposed || snapshot is null)
        {
            return;
        }

        try
        {
            Handle(snapshot);
        }
        catch (Exception exception)
        {
            // 通知失败不能影响任务本身，也不能打断其他订阅方。
            _logger.LogWarning(exception, "任务通知映射失败：{OperationId}", snapshot.Id);
        }
    }

    private void Handle(OperationSnapshot snapshot)
    {
        if (snapshot.IsTerminal)
        {
            ClearThrottle(snapshot.Id);
            switch (snapshot.State)
            {
                case OperationState.Completed:
                    Publish(new AppNotification(
                        $"operation:{snapshot.Id:N}:completed",
                        AppNotificationCategory.TaskCompleted,
                        AppNotificationSeverity.Success,
                        Text("notification.operation.completed.title"),
                        Text("notification.operation.completed.message", snapshot.Title),
                        _timeProvider.GetUtcNow(),
                        CorrelationId: snapshot.Id.ToString("N")));
                    break;
                case OperationState.Failed:
                    Publish(new AppNotification(
                        $"operation:{snapshot.Id:N}:failed",
                        AppNotificationCategory.TaskFailed,
                        AppNotificationSeverity.Error,
                        Text("notification.operation.failed.title"),
                        snapshot.Error is { } error
                            ? Text("notification.operation.failed.message", snapshot.Title, error.Message)
                            : Text("notification.operation.failed.messageNoError", snapshot.Title),
                        _timeProvider.GetUtcNow(),
                        CorrelationId: snapshot.Id.ToString("N")));
                    break;
                case OperationState.Cancelled:
                    // 用户主动取消：不提示，也不保留节流记录。
                    break;
            }

            return;
        }

        var state = ResolveBackgroundState(snapshot);
        if (state is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var key = (snapshot.Id, state);
        lock (_gate)
        {
            if (_backgroundThrottle.TryGetValue(key, out var last) && now - last < BackgroundThrottle)
            {
                return;
            }

            _backgroundThrottle[key] = now;
            TrimThrottleLocked();
        }

        var (titleKey, messageKey) = state switch
        {
            "paused" => ("notification.operation.paused.title", "notification.operation.paused.message"),
            "stalled" => ("notification.operation.stalled.title", "notification.operation.stalled.message"),
            _ => ("notification.operation.retrying.title", "notification.operation.retrying.message"),
        };

        // 进行中的状态提示带时间桶：同一任务同一状态在 30 秒后允许再次提示，
        // 而通知中心按 Id 去重的窗口是 5 分钟；终端状态则使用稳定 Id（只应出现一次）。
        Publish(new AppNotification(
            $"operation:{snapshot.Id:N}:{state}:{now.ToUnixTimeSeconds()}",
            AppNotificationCategory.BackgroundTask,
            AppNotificationSeverity.Warning,
            Text(titleKey),
            Text(messageKey, snapshot.Title),
            now,
            CorrelationId: snapshot.Id.ToString("N")));
    }

    /// <summary>
    /// 非终态里只有三种“值得告知”的状态。运行中（含普通进度刷新）不是通知，
    /// 否则每个进度回调都会产生一条提示。
    /// </summary>
    private static string? ResolveBackgroundState(OperationSnapshot snapshot)
    {
        if (snapshot.IsPaused)
        {
            return "paused";
        }

        if (snapshot.IsStalled)
        {
            return "stalled";
        }

        return snapshot.State == OperationState.Retrying ? "retrying" : null;
    }

    private void Publish(AppNotification notification)
    {
        try
        {
            _notifications.Publish(notification);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "发布任务通知失败：{Id}", notification.Id);
        }
    }

    private void ClearThrottle(Guid operationId)
    {
        lock (_gate)
        {
            foreach (var key in _backgroundThrottle.Keys.Where(key => key.OperationId == operationId).ToArray())
            {
                _backgroundThrottle.Remove(key);
            }
        }
    }

    private void TrimThrottleLocked()
    {
        if (_backgroundThrottle.Count <= MaxThrottleEntries)
        {
            return;
        }

        foreach (var key in _backgroundThrottle
                     .OrderBy(pair => pair.Value)
                     .Take(_backgroundThrottle.Count - MaxThrottleEntries)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _backgroundThrottle.Remove(key);
        }
    }

    private string Text(string key, params object?[] arguments) =>
        _localizer is null ? key : _localizer.Get(key, arguments);
}
