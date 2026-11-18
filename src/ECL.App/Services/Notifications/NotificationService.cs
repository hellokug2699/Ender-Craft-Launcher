// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Services.Notifications;

/// <summary>
/// 应用内通知的唯一发布入口。通知只是“告知”，不执行操作，也不替代确认弹窗：
/// 需要用户批准的动作仍然必须走 <c>IDialogService.Confirm</c>。
/// </summary>
public interface INotificationService
{
    event EventHandler<AppNotification>? Published;

    /// <summary>最近通知（最新在前，有界）。返回快照，调用方不能修改内部列表。</summary>
    IReadOnlyList<AppNotification> Recent { get; }

    void Publish(AppNotification notification);
}

/// <summary>
/// 内存通知中心（F-12）。
///
/// 安全边界：
/// - Title / Message 先按凭据规则脱敏（<see cref="SecretRedaction.Text"/>），
///   再按本地路径规则脱敏（<see cref="LocalPathRedaction"/>），最后截断；
///   订阅方（提示条、托盘气泡、诊断导出）拿到的一定是可直接展示的文本。
/// - 同一个 <see cref="AppNotification.Id"/> 在 5 分钟内只发布一次，
///   避免同一结果因为刷新或重试被反复提示。
/// - <see cref="AppNotification.IsMandatory"/> 绕过类别偏好，但不绕过脱敏、
///   去重与截断，也不会因此获得任何新的操作权限。
///
/// 线程契约：可从任意线程发布；事件在锁外触发，订阅方的异常不影响调用方。
/// </summary>
public sealed class NotificationService : INotificationService
{
    public const int MaxRecent = 50;
    public const int MaxTextLength = 500;

    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(5);

    private readonly INotificationPreferences _preferences;
    private readonly ILogger<NotificationService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly List<AppNotification> _recent = [];
    private readonly Dictionary<string, DateTimeOffset> _lastPublishedById = new(StringComparer.Ordinal);

    public NotificationService(
        INotificationPreferences preferences,
        ILogger<NotificationService> logger,
        TimeProvider? timeProvider = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<AppNotification>? Published;

    public IReadOnlyList<AppNotification> Recent
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }

    public void Publish(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (string.IsNullOrWhiteSpace(notification.Id) || string.IsNullOrWhiteSpace(notification.Title))
        {
            // 编程错误不该让调用方崩溃：丢掉这条并留下可诊断的日志。
            _logger.LogWarning("忽略缺少 Id 或标题的通知：{Id}", notification.Id);
            return;
        }

        if (!notification.IsMandatory && !_preferences.IsEnabled(notification.Category))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        AppNotification sanitized;
        lock (_gate)
        {
            if (_lastPublishedById.TryGetValue(notification.Id, out var last) &&
                now - last < DuplicateWindow)
            {
                return;
            }

            sanitized = Sanitize(notification);
            _lastPublishedById[notification.Id] = now;
            _recent.Insert(0, sanitized);
            TrimLocked();
            PruneDuplicatesLocked(now);
        }

        RaisePublished(sanitized);
    }

    /// <summary>发布前统一处理文本；保证内部缓存与订阅方看到的完全一致。</summary>
    private AppNotification Sanitize(AppNotification notification) =>
        notification with
        {
            Title = Clean(notification.Title),
            Message = Clean(notification.Message),
        };

    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = LocalPathRedaction.Redact(SecretRedaction.Text(text));
        return redacted.Length <= MaxTextLength
            ? redacted
            : string.Concat(redacted.AsSpan(0, MaxTextLength - 1), "…");
    }

    private void TrimLocked()
    {
        while (_recent.Count > MaxRecent)
        {
            _recent.RemoveAt(_recent.Count - 1);
        }
    }

    private void PruneDuplicatesLocked(DateTimeOffset now)
    {
        if (_lastPublishedById.Count <= MaxRecent * 2)
        {
            return;
        }

        foreach (var id in _lastPublishedById
                     .Where(pair => now - pair.Value >= DuplicateWindow)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _lastPublishedById.Remove(id);
        }
    }

    private void RaisePublished(AppNotification notification)
    {
        var handler = Published;
        if (handler is null)
        {
            return;
        }

        // 观察者（提示条、托盘气泡）出错不能影响发布方——通常是任务或启动流程。
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<AppNotification>)subscriber)(this, notification);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "通知订阅方处理失败：{Id}", notification.Id);
            }
        }
    }
}
