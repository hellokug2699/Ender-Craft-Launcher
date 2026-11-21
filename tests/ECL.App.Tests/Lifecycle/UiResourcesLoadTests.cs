// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
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

    /// <summary>
    /// 日志工作区用浮层（搜索与过滤）、右侧证据栏与图标路径重构后，必须在真实主题下仍可加载：
    /// 缺少 StaticResource 会在用户打开日志页时才失败。
    /// </summary>
    [Fact]
    public void LogsWorkspaceViewLoadsWithTheRealTheme()
    {
        using var ui = new UiThread();
        ui.Invoke(() => TestApplication.Run(() =>
        {
            var logs = new LogSessionsView();
            Assert.NotNull(logs.Content);
            Assert.NotNull(logs.FindName("LogList"));
        }));
    }

    /// <summary>
    /// 右键菜单必须有深色主题样式：没有隐式样式时 WPF 会回落到系统浅色菜单，
    /// 在深色界面里就是一块白板（2026-11-21 维护者反馈）。
    /// </summary>
    [Fact]
    public void ContextMenusUseTheDarkThemeChrome()
    {
        using var ui = new UiThread();
        ui.Invoke(() => TestApplication.Run(() =>
        {
            var menu = new ContextMenu();
            var item = new MenuItem { Header = "示例菜单项" };
            menu.Items.Add(item);

            var resources = Application.Current.Resources;
            Assert.Same(resources[typeof(ContextMenu)], menu.Style);
            Assert.Same(resources[typeof(MenuItem)], item.Style);

            // 背景必须是主题表面色，而不是系统菜单色。
            Assert.Same(resources["SurfaceAltBrush"], menu.Background);
            Assert.Same(resources["TextPrimaryBrush"], menu.Foreground);
            Assert.Same(resources["TextPrimaryBrush"], item.Foreground);
            Assert.NotNull(menu.Template);
            Assert.NotNull(item.Template);
        }));
    }
}
