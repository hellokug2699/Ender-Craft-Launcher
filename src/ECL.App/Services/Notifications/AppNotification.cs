// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Services.Notifications;

/// <summary>
/// 通知类别。每一类都对应一个真实存在的偏好开关；没有“总开关”，
/// 这样关闭某一类提示不会意外吞掉失败与强制提示。
/// </summary>
public enum AppNotificationCategory
{
    TaskCompleted,
    TaskFailed,
    BackgroundTask,
    Hint,
}

public enum AppNotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// 一条已发布的应用内通知。
///
/// <paramref name="Id"/> 是稳定标识（例如 <c>operation:&lt;guid&gt;:completed</c>），
/// 用于去重：同一次事件在不同路径上被重复发布时只应呈现一次。
/// <paramref name="IsMandatory"/> 用于“必须让用户知道”的结果（例如托管数据删除结论），
/// 它绕过类别偏好，但不能绕过脱敏与截断。
/// </summary>
public sealed record AppNotification(
    string Id,
    AppNotificationCategory Category,
    AppNotificationSeverity Severity,
    string Title,
    string Message,
    DateTimeOffset CreatedAt,
    string? CorrelationId = null,
    string? NavigationDestination = null,
    bool IsMandatory = false);

/// <summary>
/// Shell 的稳定目的地 Key（<c>ShellViewModel.NavigationItems</c>）。通知只携带 Key，
/// 真正导航由订阅方执行；未知 Key 由 Shell 自行拒绝，通知层不猜测页面。
/// </summary>
public static class NotificationDestinations
{
    public const string About = "about";
    public const string Instances = "instances";
    public const string Logs = "logs";
    public const string Settings = "settings";
}
