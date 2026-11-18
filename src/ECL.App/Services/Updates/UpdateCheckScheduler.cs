// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Services.Notifications;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Services.Updates;

/// <summary>更新检查周期。默认在启动时检查；更长的周期用于减少常驻进程的网络请求。</summary>
public enum UpdateCheckCadence
{
    Startup,
    Daily,
    Weekly,
}

/// <summary>
/// 更新检查调度入口（F-12）。检查本身仍然只读：不下载、不替换文件、不启动 Bootstrapper，
/// 结论与关于页“检查更新”完全一致（ADR 0051 / E3-1）。
/// </summary>
public interface IUpdateCheckScheduler
{
    /// <summary>最近一次检查结论；从未检查或检查失败时保持原值或为 null。</summary>
    UpdateCheckReport? LastReport { get; }

    event EventHandler<UpdateCheckReport>? ReportAvailable;

    /// <summary>
    /// 启动按周期执行的检查循环。返回的任务在取消时完成，因此调用方应把它登记为
    /// 受管后台工作（<c>IApplicationLifecycle.TrackWork</c>），而不是在 UI 线程直接 await。
    /// 幂等：重复调用返回同一个循环任务。
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 立即检查（受单飞与周期约束）：用户手动触发时忽略周期，但 <c>launcher.checkUpdates</c>
    /// 关闭时仍然不触网并返回 Disabled 结论。并发调用最多产生一次网络请求。
    /// </summary>
    Task<UpdateCheckReport?> CheckNowAsync(bool userInitiated, CancellationToken cancellationToken = default);
}

/// <summary>
/// 按 <c>launcher.updateCheckInterval</c> 调度只读更新检查。
///
/// 诚实边界：
/// - <c>launcher.checkUpdates=false</c> 时不调用 <see cref="IUpdateCheckService"/>，也不写状态键；
/// - 只有真正完成一次网络检查（UpToDate / UpdateAvailable）才写 <c>lastCheckedAt</c> 与
///   <c>lastCheckedChannel</c>；未配置更新源（Unavailable）不写，避免把“没检查”伪装成“已检查”；
/// - 通道变化立即到期：切换 Stable/Beta/Nightly 后不必等满一个周期；
/// - 失败与可用更新会发通知，Unavailable 只记日志与 LastReport（开发构建没有更新源是常态，
///   每次都提示会变成噪音）。
///
/// 线程契约：所有状态都在锁内读写；事件在锁外触发；循环异常只记日志，绝不结束进程。
/// </summary>
public sealed class UpdateCheckScheduler : IUpdateCheckScheduler
{
    private static readonly TimeSpan MinimumLoopDelay = TimeSpan.FromMinutes(1);

    private readonly ISettingsService _settings;
    private readonly IUpdateCheckService _updates;
    private readonly INotificationService _notifications;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UpdateCheckScheduler> _logger;
    private readonly ILocalizer? _localizer;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _loopCancellation = new();

    private Task<UpdateCheckReport?>? _inFlight;
    private Task? _loop;
    private UpdateCheckReport? _lastReport;

    public UpdateCheckScheduler(
        ISettingsService settings,
        IUpdateCheckService updates,
        INotificationService notifications,
        TimeProvider timeProvider,
        ILogger<UpdateCheckScheduler> logger,
        ILocalizer? localizer = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _localizer = localizer;
    }

    public UpdateCheckReport? LastReport
    {
        get
        {
            lock (_gate)
            {
                return _lastReport;
            }
        }
    }

    public event EventHandler<UpdateCheckReport>? ReportAvailable;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return _loop;
            }

            // Task.Run 把循环的启动推迟到锁外，避免在持锁时同步执行第一次检查。
            _loop = Task.Run(() => RunLoopAsync(_loopCancellation.Token, cancellationToken));
            return _loop;
        }
    }

    public Task<UpdateCheckReport?> CheckNowAsync(bool userInitiated, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<UpdateCheckReport?> completion;
        lock (_gate)
        {
            if (_inFlight is { } running)
            {
                // 单飞：并发的第二次调用复用同一个网络请求，不产生第二份流量与第二组通知。
                return running;
            }

            completion = new TaskCompletionSource<UpdateCheckReport?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
        }

        // 槽位已经登记，再来一次调用一定会复用；检查本身在锁外执行。
        _ = RunCheckAsync(completion, userInitiated, cancellationToken);
        return completion.Task;
    }

    private async Task RunCheckAsync(
        TaskCompletionSource<UpdateCheckReport?> completion,
        bool userInitiated,
        CancellationToken cancellationToken)
    {
        try
        {
            var report = await CheckCoreAsync(userInitiated, cancellationToken).ConfigureAwait(false);
            completion.TrySetResult(report);
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlight, completion.Task))
                {
                    _inFlight = null;
                }
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken loopToken, CancellationToken externalToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(loopToken, externalToken);
        var token = linked.Token;
        try
        {
            // 启动时先按当前周期评估一次：startup 周期就是“只在这里检查”。
            await CheckNowAsync(userInitiated: false, token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(ComputeNextDelay(), _timeProvider, token).ConfigureAwait(false);
                await CheckNowAsync(userInitiated: false, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 关闭应用或周期循环被取消是预期路径。
        }
        catch (Exception exception)
        {
            // 循环不能因为任何异常杀死进程；下次启动会重新评估。
            _logger.LogError(exception, "更新检查循环异常结束。");
        }
    }

    private async Task<UpdateCheckReport?> CheckCoreAsync(bool userInitiated, CancellationToken cancellationToken)
    {
        var channel = ResolveChannel();
        if (!_settings.Get<bool>(LauncherKeys.CheckUpdates))
        {
            // 关闭时绝不触网；返回明确结论而不是“没反应”。
            var disabled = new UpdateCheckReport(
                UpdateCheckOutcome.Disabled,
                channel,
                null,
                null,
                null,
                null,
                "update.disabled",
                "更新检查已关闭。");
            PublishReport(disabled);
            return disabled;
        }

        if (!IsDue(userInitiated, channel))
        {
            return LastReport;
        }

        UpdateCheckReport report;
        try
        {
            report = await _updates.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "更新检查失败：{Channel}", channel);
            Publish(new AppNotification(
                $"update.check.failed:{channel}:{exception.GetType().Name}",
                AppNotificationCategory.TaskFailed,
                AppNotificationSeverity.Error,
                Text("notification.update.failed.title"),
                Text("notification.update.failed.message", exception.Message),
                _timeProvider.GetUtcNow(),
                NavigationDestination: NotificationDestinations.About));
            return null;
        }

        // 只有真的核对过远端清单才算“检查过”；Unavailable 表示前置条件缺失、未触网。
        if (report.Outcome is UpdateCheckOutcome.UpToDate or UpdateCheckOutcome.UpdateAvailable)
        {
            WriteCheckedState(channel);
        }

        PublishReport(report);
        NotifyFor(report, channel);
        return report;
    }

    private bool IsDue(bool userInitiated, string channel)
    {
        // 用户手动点击“检查更新”不受周期限制；关闭开关时已在上面短路。
        if (userInitiated)
        {
            return true;
        }

        if (ResolveCadence() == UpdateCheckCadence.Startup)
        {
            return true;
        }

        var lastChannel = _settings.Get<string>(LauncherKeys.UpdateLastCheckedChannel) ?? string.Empty;
        if (!string.Equals(lastChannel, channel, StringComparison.OrdinalIgnoreCase))
        {
            // 通道变化后旧结论不再成立，立即重查。
            return true;
        }

        var lastCheckedAt = TryReadLastCheckedAt();
        if (lastCheckedAt is null)
        {
            return true;
        }

        return _timeProvider.GetUtcNow() - lastCheckedAt.Value >= ResolveInterval();
    }

    private TimeSpan ComputeNextDelay()
    {
        var interval = ResolveInterval();
        if (interval <= TimeSpan.Zero)
        {
            // startup 周期同样需要一个复查节拍：长时间运行时仍应感知通道变化与新版本。
            interval = TimeSpan.FromDays(1);
        }

        var lastCheckedAt = TryReadLastCheckedAt();
        if (lastCheckedAt is null)
        {
            return MinimumLoopDelay;
        }

        var remaining = lastCheckedAt.Value + interval - _timeProvider.GetUtcNow();
        if (remaining <= MinimumLoopDelay)
        {
            return MinimumLoopDelay;
        }

        return remaining > interval ? interval : remaining;
    }

    private TimeSpan ResolveInterval() => ResolveCadence() switch
    {
        UpdateCheckCadence.Daily => TimeSpan.FromDays(1),
        UpdateCheckCadence.Weekly => TimeSpan.FromDays(7),
        _ => TimeSpan.Zero,
    };

    private UpdateCheckCadence ResolveCadence() =>
        _settings.Get<string>(LauncherKeys.UpdateCheckInterval) switch
        {
            LauncherKeys.UpdateCheckIntervals.Daily => UpdateCheckCadence.Daily,
            LauncherKeys.UpdateCheckIntervals.Weekly => UpdateCheckCadence.Weekly,
            // 未知值（手改配置或未来新增档位）按最保守的启动检查处理，不放弃检查。
            _ => UpdateCheckCadence.Startup,
        };

    /// <summary>原始设置值用于比较通道变化；空值表示未配置，按原样记录。</summary>
    private string ResolveChannel() => _settings.Get<string>(LauncherKeys.UpdateChannel) ?? string.Empty;

    private DateTimeOffset? TryReadLastCheckedAt()
    {
        var raw = _settings.Get<string>(LauncherKeys.UpdateLastCheckedAt);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    private void WriteCheckedState(string channel)
    {
        var now = _timeProvider.GetUtcNow();
        if (!_settings.TrySetRaw(
                LauncherKeys.UpdateLastCheckedAt,
                now.ToString("O", CultureInfo.InvariantCulture),
                null,
                out var timeError))
        {
            _logger.LogWarning("写入更新检查时间失败：{Error}", timeError);
        }

        if (!_settings.TrySetRaw(LauncherKeys.UpdateLastCheckedChannel, channel, null, out var channelError))
        {
            _logger.LogWarning("写入更新检查通道失败：{Error}", channelError);
        }
    }

    private void NotifyFor(UpdateCheckReport report, string channel)
    {
        switch (report.Outcome)
        {
            case UpdateCheckOutcome.UpdateAvailable:
                Publish(new AppNotification(
                    $"update.available:{channel}:{report.AvailableVersion}",
                    AppNotificationCategory.BackgroundTask,
                    AppNotificationSeverity.Info,
                    Text("notification.update.available.title"),
                    Text(
                        "notification.update.available.message",
                        report.AvailableVersion ?? string.Empty,
                        report.CurrentVersion ?? string.Empty),
                    _timeProvider.GetUtcNow(),
                    NavigationDestination: NotificationDestinations.About));
                break;
            case UpdateCheckOutcome.Failed:
                Publish(new AppNotification(
                    $"update.check.failed:{channel}:{report.ErrorCode}",
                    AppNotificationCategory.TaskFailed,
                    AppNotificationSeverity.Error,
                    Text("notification.update.failed.title"),
                    Text("notification.update.failed.message", report.Message ?? report.ErrorCode ?? string.Empty),
                    _timeProvider.GetUtcNow(),
                    NavigationDestination: NotificationDestinations.About));
                break;
            case UpdateCheckOutcome.Unavailable:
                // 开发构建没有更新源是常态：只记录，不打扰。
                _logger.LogInformation(
                    "更新检查不可用：{Code} {Message}",
                    report.ErrorCode,
                    report.Message);
                break;
            default:
                break;
        }
    }

    private void PublishReport(UpdateCheckReport report)
    {
        lock (_gate)
        {
            _lastReport = report;
        }

        var handler = ReportAvailable;
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<UpdateCheckReport>)subscriber)(this, report);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "更新检查结论订阅方处理失败：{Outcome}", report.Outcome);
            }
        }
    }

    private void Publish(AppNotification notification)
    {
        try
        {
            _notifications.Publish(notification);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "发布更新通知失败：{Id}", notification.Id);
        }
    }

    private string Text(string key, params object?[] arguments) =>
        _localizer is null ? key : _localizer.Get(key, arguments);
}
