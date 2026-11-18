// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.Services;
using Ecl.App.Services.Confirmation;
using Ecl.App.Services.Updates;
using Ecl.App.Shell.Lifecycle;
using Ecl.App.Tests.Support;
using Ecl.App.ViewModels;
using Ecl.App.ViewModels.Settings;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Deployment;
using Ecl.Deployment.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.App.Tests.Settings;

/// <summary>
/// F-12 设置 → 常规页：五组真实设置 + 代码签名卡、隐藏项的显式呈现、
/// confirmOnClose 的依赖可见性、搜索定位、托盘原因的如实说明，以及
/// “恢复默认值不静默删除 Windows 登录启动项”。
/// </summary>
public sealed class SettingsGeneralPageTests
{
    [Fact]
    public void PageRendersFiveGroupsAndCodeSigningWithoutCapabilityGaps()
    {
        using var host = new Host();

        Assert.Equal(SettingsPageKeys.General, host.Page.Key);
        Assert.Equal("常规", host.Page.Title);
        Assert.Contains(host.Page.StartGroup, host.Page.Sections);
        Assert.Contains(host.Page.WindowGroup, host.Page.Sections);
        Assert.Contains(host.Page.UpdateGroup, host.Page.Sections);
        Assert.Contains(host.Page.NotificationGroup, host.Page.Sections);
        Assert.Contains(host.Page.ConfirmationGroup, host.Page.Sections);
        Assert.Single(host.Page.Sections.OfType<CodeSigningSectionViewModel>());
        // F-12 接线完成后不再有“尚未接入”的常规页缺口。
        Assert.Empty(host.Page.Sections.OfType<SettingsUnavailableSectionViewModel>());

        Assert.Equal(
            [
                "launcher.startWithWindows",
                "launcher.startMinimizedToTray",
                "launcher.showHomeOnStartup",
            ],
            host.Page.StartGroup.Options.Select(option => option.Key));
        Assert.Equal(4, host.Page.NotificationGroup.Options.Count);
        Assert.Equal(3, host.Page.ConfirmationGroup.Options.Count);
    }

    [Fact]
    public void TogglingStartMinimizedSavesImmediately()
    {
        using var host = new Host();
        var option = host.Option(LauncherKeys.StartMinimizedToTray);

        Assert.False(option.BoolValue);
        option.BoolValue = true;

        Assert.Equal("true", host.Settings.GetRawString(LauncherKeys.StartMinimizedToTray));
    }

    [Fact]
    public void ConfirmOnCloseIsOnlyVisibleWhenTheCloseButtonExits()
    {
        using var host = new Host();
        var closeAction = host.Option(LauncherKeys.CloseButtonAction);
        var confirmOnClose = host.Option(LauncherKeys.ConfirmOnClose);

        Assert.False(confirmOnClose.IsVisible);

        closeAction.TextValue = LauncherKeys.CloseButtonActions.Exit;
        Assert.True(confirmOnClose.IsVisible);

        closeAction.TextValue = LauncherKeys.CloseButtonActions.Ask;
        Assert.False(confirmOnClose.IsVisible);
    }

    [Fact]
    public void SearchLocatesTheOptionThatGatesConfirmOnClose()
    {
        using var host = new Host();

        Assert.True(host.Page.Highlight(LauncherKeys.ConfirmOnClose));

        // 目标不可见时定位到控制它的关闭按钮行为，并说明解锁条件。
        Assert.True(host.Option(LauncherKeys.CloseButtonAction).IsHighlighted);
        Assert.Contains("仅在", host.Page.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayUnavailableExplainsTheRealReason()
    {
        using var host = new Host();
        host.Tray.IsAvailable = false;
        host.Tray.UnavailableReasonKey = "tray.unavailable.initFailed";

        var page = host.RecreatePage();

        Assert.False(page.IsTrayAvailable);
        Assert.True(page.ShowTrayUnavailable);
        Assert.Contains("不可用", page.TrayStatusText, StringComparison.Ordinal);
        Assert.Contains("初始化系统托盘失败", page.TrayReasonText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateSectionShowsLastCheckAndTheSchedulerReport()
    {
        using var host = new Host();
        host.Settings.TrySetRaw(
            LauncherKeys.UpdateLastCheckedAt,
            new DateTimeOffset(2026, 11, 18, 8, 30, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture),
            null,
            out _);

        var scheduler = new FakeUpdateCheckScheduler();
        scheduler.Reports.Enqueue(new UpdateCheckReport(
            UpdateCheckOutcome.UpdateAvailable,
            "main",
            "0.9.1",
            "0.10.0",
            null,
            null,
            null,
            null));
        await scheduler.CheckNowAsync(userInitiated: true);
        host.Scheduler = scheduler;

        var page = host.RecreatePage();

        Assert.DoesNotContain("尚未", page.LastCheckedText, StringComparison.Ordinal);
        Assert.Contains("2026-11-18", page.LastCheckedText, StringComparison.Ordinal);
        Assert.Contains("0.10.0", page.LastResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void ResettingDefaultsKeepsTheRegistryEntryWhenTheUserDeclinesRemoval()
    {
        using var host = new Host();
        host.Settings.TrySetRaw(LauncherKeys.StartWithWindows, "true", null, out _);
        host.Registration.State = new AutoStartState(
            true,
            "\"C:\\ECL\\ECL.exe\" --autostart",
            true,
            null,
            null);
        host.Dialogs.ConfirmResults.Enqueue(true);  // 通用重置确认
        host.Dialogs.ConfirmResults.Enqueue(false); // 是否删除登录启动项：否
        var page = host.RecreatePage();

        page.ResetToDefaults();

        Assert.Empty(host.Registration.SetCalls);
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(LauncherKeys.StartWithWindows));
        Assert.Contains("仍保留", page.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ResettingDefaultsRemovesTheRegistryEntryOnlyAfterExplicitConfirmation()
    {
        using var host = new Host();
        host.Settings.TrySetRaw(LauncherKeys.StartWithWindows, "true", null, out _);
        host.Registration.State = new AutoStartState(
            true,
            "\"C:\\ECL\\ECL.exe\" --autostart",
            true,
            null,
            null);
        host.Dialogs.ConfirmResults.Enqueue(true);
        host.Dialogs.ConfirmResults.Enqueue(true);
        var page = host.RecreatePage();

        page.ResetToDefaults();

        Assert.Equal([false], host.Registration.SetCalls);
        Assert.Equal(SettingsValueSource.Default, host.Settings.GetSource(LauncherKeys.StartWithWindows));
    }

    private sealed class Host : IDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        public Host()
        {
            _root = Path.Combine(Path.GetTempPath(), "ecl-general-page-tests", Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(_root);
            paths.EnsureCreated();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEclCore(paths, CultureInfo.GetCultureInfo("zh-CN"));
            services.AddEclConfiguration();
            _provider = services.BuildServiceProvider();

            Settings = _provider.GetRequiredService<ISettingsService>();
            Localizer = _provider.GetRequiredService<ILocalizer>();
            CodeSigning = new CodeSigningTrustService(Path.Combine(_root, "signing"));
            Page = RecreatePage();
        }

        public ISettingsService Settings { get; }

        public ILocalizer Localizer { get; }

        public CodeSigningTrustService CodeSigning { get; }

        public FakeDialogService Dialogs { get; } = new();

        public FakeAutoStartRegistration Registration { get; } = new();

        public FakeTrayHost Tray { get; } = new();

        public FakeUpdateCheckScheduler Scheduler { get; set; } = new();

        public SettingsGeneralPageViewModel Page { get; private set; }

        public SettingsGeneralPageViewModel RecreatePage()
        {
            Page = SettingsGeneralPageFactory.Create(
                Settings,
                Localizer,
                Dialogs,
                CodeSigning,
                Tray,
                Registration,
                Scheduler);
            return Page;
        }

        public OptionEditorViewModel Option(string key) =>
            Page.Options.Single(option => option.Key == key);

        public void Dispose()
        {
            _provider.Dispose();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响断言。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public Queue<bool> ConfirmResults { get; } = new();

        public bool Confirm(
            string title,
            string message,
            string? acknowledgementToken = null,
            ConfirmationKind kind = ConfirmationKind.Mandatory) =>
            ConfirmResults.Count > 0 && ConfirmResults.Dequeue();

        public InstanceDeletionChoice ChooseInstanceDeletion(
            string title,
            string message,
            bool canDeleteManagedData) => InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowError(string title, string message)
        {
        }

        public string? PickOpenFile(string title, string filter) => null;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}
