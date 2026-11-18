// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services.Notifications;
using Ecl.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Ecl.App.Shell.Notifications;

/// <summary>
/// 应用内通知宿主（F-12）：把 <see cref="INotificationService.Published"/> 映射成
/// 屏幕右下角最多 3 条的可点击提示。
///
/// 边界：
/// - 通知只是“告知”：关闭/超时只移除提示，不取消、不回滚任何任务；
/// - 最多同时 3 条，超出的旧提示先移除，避免提示条挤满窗口；
/// - 点击只在通知带 <see cref="AppNotification.NavigationDestination"/> 时导航，
///   未知目的地由 Shell 自行拒绝，通知层不猜测页面；
/// - 默认 6 秒后自动消失，时长由 <see cref="TimeProvider"/> 驱动（测试可注入假时钟）；
/// - 线程契约：发布可能来自任意线程，所有集合变更封送到创建宿主的 UI Dispatcher。
/// </summary>
public sealed partial class NotificationHostViewModel : ObservableObject, IDisposable
{
    /// <summary>同时可见的提示上限；这是呈现约束，不是通知保留上限（服务端另有有界缓存）。</summary>
    public const int MaxVisible = 3;

    /// <summary>普通提示的展示时长。过短读不完，过长会挡住页面操作。</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(6);

    private readonly INotificationService _notifications;
    private readonly ILocalizer _localizer;
    private readonly TimeProvider _timeProvider;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<NotificationHostViewModel>? _logger;
    private bool _disposed;

    public NotificationHostViewModel(
        INotificationService notifications,
        ILocalizer localizer,
        Dispatcher dispatcher,
        TimeProvider? timeProvider = null,
        ILogger<NotificationHostViewModel>? logger = null)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        Items.CollectionChanged += OnItemsChanged;
        _notifications.Published += OnPublished;
    }

    /// <summary>当前可见提示（最新在前）。</summary>
    public ObservableCollection<NotificationItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>
    /// 点击提示时的导航入口；由 Shell 注入（<c>ShellViewModel.ActivateDestination</c>）。
    /// 页面不自行创建导航。
    /// </summary>
    public Func<string, bool>? Navigate { get; set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifications.Published -= OnPublished;
        Items.CollectionChanged -= OnItemsChanged;
        foreach (var item in Items)
        {
            item.CancelAutoDismiss();
        }

        Items.Clear();
    }

    [RelayCommand]
    private void Activate(NotificationItemViewModel? item)
    {
        if (item is null || _disposed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.NavigationDestination))
        {
            try
            {
                // 未知目的地属于 Shell 的职责：这里只报告导航是否被接受，不猜测替代页面。
                Navigate?.Invoke(item.NavigationDestination);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "通知导航失败：{Destination}", item.NavigationDestination);
            }
        }

        Remove(item);
    }

    [RelayCommand]
    private void Dismiss(NotificationItemViewModel? item)
    {
        if (item is not null)
        {
            Remove(item);
        }
    }

    private void OnPublished(object? sender, AppNotification notification)
    {
        if (_disposed || notification is null)
        {
            return;
        }

        Dispatch(() => Show(notification));
    }

    private void Show(AppNotification notification)
    {
        if (_disposed)
        {
            return;
        }

        // 同一个 Id 只应出现一次：服务端已按 5 分钟去重，这里再防御一次刷新/重放。
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (string.Equals(Items[index].Id, notification.Id, StringComparison.Ordinal))
            {
                Items.RemoveAt(index);
            }
        }

        while (Items.Count >= MaxVisible)
        {
            // 旧提示先让位：新结论通常比旧状态更值得用户注意。
            var oldest = Items[^1];
            oldest.CancelAutoDismiss();
            Items.RemoveAt(Items.Count - 1);
        }

        var item = new NotificationItemViewModel(
            notification,
            _localizer.Get("notification.host.dismiss"));
        Items.Insert(0, item);
        ScheduleAutoDismiss(item);
    }

    private void ScheduleAutoDismiss(NotificationItemViewModel item)
    {
        var cancellation = new CancellationTokenSource();
        item.SetAutoDismiss(cancellation);
        _ = AutoDismissAsync(item, cancellation.Token);
    }

    private async Task AutoDismissAsync(NotificationItemViewModel item, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DefaultLifetime, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Dispatch(() => Remove(item));
    }

    private void Remove(NotificationItemViewModel item)
    {
        if (_disposed)
        {
            return;
        }

        item.CancelAutoDismiss();
        Items.Remove(item);
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 在检查后开始关停：通知已无处呈现，丢弃不影响任务状态。
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasItems));
}

/// <summary>
/// 一条可见通知的只读投影。文本已经由通知服务脱敏与截断，界面不再二次加工。
/// </summary>
public sealed class NotificationItemViewModel : ObservableObject
{
    private CancellationTokenSource? _autoDismiss;

    public NotificationItemViewModel(AppNotification notification, string dismissText)
    {
        ArgumentNullException.ThrowIfNull(notification);

        Id = notification.Id;
        Category = notification.Category;
        Severity = notification.Severity;
        Title = notification.Title;
        Message = notification.Message;
        NavigationDestination = notification.NavigationDestination;
        DismissText = dismissText;
    }

    public string Id { get; }

    public AppNotificationCategory Category { get; }

    public AppNotificationSeverity Severity { get; }

    public string Title { get; }

    public string Message { get; }

    public string? NavigationDestination { get; }

    public string DismissText { get; }

    public bool CanNavigate => !string.IsNullOrWhiteSpace(NavigationDestination);

    internal void SetAutoDismiss(CancellationTokenSource cancellation)
    {
        _autoDismiss?.Dispose();
        _autoDismiss = cancellation;
    }

    internal void CancelAutoDismiss()
    {
        var cancellation = _autoDismiss;
        _autoDismiss = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }
}
