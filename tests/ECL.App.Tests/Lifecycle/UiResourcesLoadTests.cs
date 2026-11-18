// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.App.Shell.Notifications;
using Ecl.App.Tests.Support;
using Ecl.App.Views;
using Xunit;

namespace Ecl.App.Tests.Lifecycle;

/// <summary>
/// F-12 新增视图必须在真实主题资源下可加载：静态资源名写错或缺少样式
/// 应在测试阶段暴露，而不是等到用户打开设置页或关闭窗口时。
/// </summary>
public sealed class UiResourcesLoadTests
{
    [Fact]
    public void GeneralSettingsClosePromptAndNotificationViewsLoadWithTheRealTheme()
    {
        using var ui = new UiThread();
        ui.Invoke(() => TestApplication.Run(() =>
        {
            var settings = new SettingsGeneralPageView();
            Assert.NotNull(settings.Content);

            var closePrompt = new ClosePromptDialogView();
            Assert.NotNull(closePrompt.Content);

            var notifications = new NotificationHostView();
            Assert.NotNull(notifications.Content);
        }));
    }
}
