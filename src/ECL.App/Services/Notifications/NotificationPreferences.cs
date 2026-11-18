// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;

namespace Ecl.App.Services.Notifications;

/// <summary>
/// 通知偏好的领域视图。刻意不提供“全部通知”总开关：失败提示与强制提示
/// 不应该被一个笼统的开关静默关闭（失败必须可见）。
/// </summary>
public interface INotificationPreferences
{
    bool IsEnabled(AppNotificationCategory category);
}

/// <summary>
/// 四类通知到设置键的映射（F-12）。所有键默认开启，默认值定义在
/// <c>LauncherOptionsProvider</c>；这里只读，不写回设置。
/// </summary>
public sealed class NotificationPreferences(ISettingsService settings) : INotificationPreferences
{
    public bool IsEnabled(AppNotificationCategory category) => category switch
    {
        AppNotificationCategory.TaskCompleted => settings.Get<bool>(LauncherKeys.NotifyTaskCompleted),
        AppNotificationCategory.TaskFailed => settings.Get<bool>(LauncherKeys.NotifyTaskFailed),
        AppNotificationCategory.BackgroundTask => settings.Get<bool>(LauncherKeys.NotifyBackgroundTasks),
        AppNotificationCategory.Hint => settings.Get<bool>(LauncherKeys.ShowOperationHints),
        // 未知类别按“不显示”处理：宁可少打扰，也不把无法分类的内容硬塞给用户。
        _ => false,
    };
}
